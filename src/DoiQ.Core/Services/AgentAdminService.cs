using System.Security.Cryptography;
using DoiQ.Core.Data;
using DoiQ.Core.Domain;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace DoiQ.Core.Services;

/// <summary>Agent management for the UI. Keys are generated here, shown once, and only their hash is stored.</summary>
public sealed class AgentAdminService(DoiQDbContext db, TimeProvider time)
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "any", "none", "unassigned", "me", "agents" };

    private static string RandomToken(string prefix) => prefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static AgentAdminDto ToDto(Actor a) =>
        new(a.Id, a.Name, a.Initials, a.IsTriage, a.IsActive, a.WebhookUrl, a.NotifyUrl, !string.IsNullOrEmpty(a.WebhookSecret), a.CreatedAt, a.LastSeenAt,
            a.IsDispatcher, a.Responsibilities, a.IsOrchestrator);

    /// <summary>Deleted agents kept for their history are left out.</summary>
    public async Task<IReadOnlyList<AgentAdminDto>> ListAsync(CancellationToken ct = default) =>
        (await db.Actors.AsNoTracking().Where(a => a.Kind == ActorKind.Agent && a.DeletedAt == null).OrderBy(a => a.Id).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<CreatedAgentResult> CreateAsync(CreateAgentRequest req, CancellationToken ct = default)
    {
        var name = await ValidateNameAsync(req.Name, null, ct);
        var key = RandomToken("dq_");
        var secret = string.IsNullOrWhiteSpace(req.WebhookSecret) ? null : req.WebhookSecret;
        var generated = secret is null ? RandomToken("whsec_") : null;
        var agent = new Actor
        {
            Kind = ActorKind.Agent, Name = name, Initials = InitialsFor(req.Initials, name),
            ApiKeyHash = ApiKeyHasher.Hash(key), IsTriage = req.IsTriage, IsDispatcher = req.IsDispatcher, IsOrchestrator = req.IsOrchestrator,
            Responsibilities = CleanResponsibilities(req.Responsibilities),
            WebhookUrl = Clean(req.WebhookUrl), NotifyUrl = Clean(req.NotifyUrl), WebhookSecret = secret ?? generated,
            IsActive = true, CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.Actors.Add(agent);
        if (req.IsTriage) await ClearOtherTriageAsync(null, ct);
        if (req.IsDispatcher) await ClearOtherDispatcherAsync(null, ct);
        await SaveAsync(ct);
        return new CreatedAgentResult(ToDto(agent), key, generated);
    }

    public async Task<AgentAdminDto> UpdateAsync(int id, UpdateAgentRequest req, CancellationToken ct = default)
    {
        var a = await LoadAsync(id, ct);
        a.Name = await ValidateNameAsync(req.Name, id, ct);
        a.Initials = InitialsFor(req.Initials, a.Name);
        a.WebhookUrl = Clean(req.WebhookUrl);
        a.NotifyUrl = Clean(req.NotifyUrl);
        if (!string.IsNullOrWhiteSpace(req.WebhookSecret)) a.WebhookSecret = req.WebhookSecret;
        if (req.IsTriage) await ClearOtherTriageAsync(id, ct);
        a.IsTriage = req.IsTriage;
        if (req.IsDispatcher) await ClearOtherDispatcherAsync(id, ct);
        a.IsDispatcher = req.IsDispatcher;
        a.IsOrchestrator = req.IsOrchestrator;
        a.Responsibilities = CleanResponsibilities(req.Responsibilities);
        await SaveAsync(ct);
        return ToDto(a);
    }

    // ───────────────────────── orchestrators: agents managing agents ─────────────────────────

    /// <summary>An orchestrator registering an agent. The new agent gets no roles: only the user makes an agent triage, dispatcher or orchestrator.</summary>
    public async Task<CreatedAgentResult> Register(int orchestratorId, RegisterAgentRequest req, CancellationToken ct)
    {
        await RequireOrchestratorAsync(orchestratorId, ct);
        return await CreateAsync(new CreateAgentRequest(req.Name, Initials: req.Initials, WebhookUrl: req.WebhookUrl, WebhookSecret: req.WebhookSecret,
            NotifyUrl: req.NotifyUrl, Responsibilities: req.Responsibilities), ct);
    }

    /// <summary>An orchestrator changing another agent's hooks, secrets or description. Null leaves a value, "" clears it.</summary>
    public async Task<ConfiguredAgentResult> Configure(int orchestratorId, string agent, ConfigureAgentRequest req, CancellationToken ct)
    {
        var a = await LoadManagedAsync(orchestratorId, agent, "change", ct);
        if (req.Initials is not null) a.Initials = InitialsFor(req.Initials, a.Name);
        if (req.WebhookUrl is not null) a.WebhookUrl = Clean(req.WebhookUrl);
        if (req.NotifyUrl is not null) a.NotifyUrl = Clean(req.NotifyUrl);
        if (req.Responsibilities is not null) a.Responsibilities = CleanResponsibilities(req.Responsibilities);
        if (!string.IsNullOrWhiteSpace(req.WebhookSecret)) a.WebhookSecret = req.WebhookSecret;
        string? key = null;
        if (req.RotateKey)
        {
            key = RandomToken("dq_");
            a.ApiKeyHash = ApiKeyHasher.Hash(key);
        }
        await db.SaveChangesAsync(ct);
        return new ConfiguredAgentResult(ToDto(a), key);
    }

    /// <summary>An orchestrator deleting another agent; see <see cref="Delete(int, CancellationToken)"/>.</summary>
    public async Task<DeletedAgentResult> Delete(int orchestratorId, string agent, CancellationToken ct) =>
        await DeleteCore(await LoadManagedAsync(orchestratorId, agent, "delete", ct), ct);

    /// <summary>
    /// Refused while open tasks are assigned to the agent. An agent that never took part in a task is removed; one that
    /// did stays in that history, renamed "Name (deleted)" with no key, hooks or roles, so its name can be used again.
    /// </summary>
    public async Task<DeletedAgentResult> Delete(int id, CancellationToken ct) => await DeleteCore(await LoadAsync(id, ct), ct);

    private async Task<DeletedAgentResult> DeleteCore(Actor a, CancellationToken ct)
    {
        // SQLite's BEGIN IMMEDIATE takes the write lock now, so no assignment can commit between the check and the
        // delete; TaskService re-checks its assignee inside its own write transaction for the opposite order.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var open = await db.Tasks.IgnoreQueryFilters().CountAsync(t => t.AssigneeId == a.Id && t.Status != BoardStatus.Done, ct);
        if (open > 0)
            throw new ValidationException($"{a.Name} still has {open} open task{(open == 1 ? "" : "s")} assigned. Assign {(open == 1 ? "it" : "them")} to someone else first.");

        var id = a.Id;
        await db.Outbox.Where(o => o.TargetActorId == id && o.SentAt == null).ExecuteDeleteAsync(ct);
        if (!await HasHistoryAsync(id, ct))
        {
            await db.RequestKeys.Where(k => k.ActorId == id).ExecuteDeleteAsync(ct);
            var name = a.Name;
            db.Actors.Remove(a);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new DeletedAgentResult(id, name, false);
        }

        a.Name = await DeletedNameAsync(a, ct);
        a.DeletedAt = time.GetUtcNow().UtcDateTime;
        a.IsActive = false;
        a.ApiKeyHash = a.WebhookUrl = a.WebhookSecret = a.NotifyUrl = null;
        a.IsTriage = a.IsDispatcher = a.IsOrchestrator = false;
        await SaveAsync(ct);
        await tx.CommitAsync(ct);
        return new DeletedAgentResult(id, a.Name, true);
    }

    private async Task<bool> HasHistoryAsync(int id, CancellationToken ct) =>
        await db.Events.AnyAsync(e => e.ActorId == id, ct)
        || await db.Tasks.IgnoreQueryFilters().AnyAsync(t => t.AssigneeId == id || t.CreatedById == id || t.LastActivityById == id || t.ClaimedById == id, ct)
        || await db.Subtasks.AnyAsync(s => s.AssigneeId == id || s.CreatedById == id, ct)
        || await db.Fields.AnyAsync(f => f.AuthorId == id, ct)
        || await db.Comments.AnyAsync(c => c.AuthorId == id, ct)
        || await db.Mentions.AnyAsync(m => m.MentionedId == id || m.ById == id, ct);

    /// <summary>"Name (deleted)", or "Name (deleted 2)" and so on when an earlier agent of that name was deleted too.</summary>
    private async Task<string> DeletedNameAsync(Actor a, CancellationToken ct)
    {
        var taken = (await db.Actors.AsNoTracking().Where(x => x.Id != a.Id).Select(x => x.Name).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 1; ; n++)
        {
            var suffix = n == 1 ? " (deleted)" : $" (deleted {n})";
            var name = (a.Name.Length + suffix.Length > 64 ? a.Name[..(64 - suffix.Length)] : a.Name) + suffix;
            if (!taken.Contains(name)) return name;
        }
    }

    private async Task RequireOrchestratorAsync(int orchestratorId, CancellationToken ct)
    {
        var me = await db.Actors.AsNoTracking().FirstOrDefaultAsync(a => a.Id == orchestratorId, ct);
        if (me is not { Kind: ActorKind.Agent, IsActive: true, IsOrchestrator: true })
            throw new ForbiddenException("Only an orchestrator agent can register, change or delete agents. The user makes an agent an orchestrator in Settings.");
    }

    /// <summary>An agent the orchestrator may manage, by name: any agent but itself and other orchestrators.</summary>
    private async Task<Actor> LoadManagedAsync(int orchestratorId, string? name, string verb, CancellationToken ct)
    {
        await RequireOrchestratorAsync(orchestratorId, ct);
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) throw new ValidationException("Name the agent (see list_actors).");
        var a = (await db.Actors.Where(x => x.Kind == ActorKind.Agent && x.DeletedAt == null).ToListAsync(ct))
            .FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) ?? throw new NotFoundException($"There is no agent named '{n}'.");
        if (a.Id == orchestratorId)
            throw new ForbiddenException($"You cannot {verb} yourself. Use set_my_responsibilities to describe yourself; the user manages your hooks and key in Settings.");
        if (a.IsOrchestrator) throw new ForbiddenException($"{a.Name} is an orchestrator: only the user can {verb} it.");
        return a;
    }

    /// <summary>An agent describing itself (the user asked it to, or it changed). Overwrites what was there.</summary>
    public async Task<ActorDto> SetResponsibilities(int agentId, string? responsibilities, CancellationToken ct)
    {
        var a = await LoadAsync(agentId, ct);
        a.Responsibilities = CleanResponsibilities(responsibilities);
        await db.SaveChangesAsync(ct);
        return new ActorDto(a.Id, a.Kind, a.Name, a.Initials, a.IsTriage, a.IsDispatcher, a.Responsibilities);
    }

    /// <summary>The old key stops working immediately.</summary>
    public async Task<RotatedKeyResult> RotateKeyAsync(int id, CancellationToken ct = default)
    {
        var a = await LoadAsync(id, ct);
        var key = RandomToken("dq_");
        a.ApiKeyHash = ApiKeyHasher.Hash(key);
        await db.SaveChangesAsync(ct);
        return new RotatedKeyResult(key);
    }

    /// <summary>Inactive agents cannot authenticate, receive no webhooks and leave the pool; their tasks stay assigned.</summary>
    public async Task<AgentAdminDto> SetActiveAsync(int id, bool active, CancellationToken ct = default)
    {
        var a = await LoadAsync(id, ct);
        a.IsActive = active;
        await db.SaveChangesAsync(ct);
        return ToDto(a);
    }

    private async Task<Actor> LoadAsync(int id, CancellationToken ct) =>
        await db.Actors.FirstOrDefaultAsync(a => a.Id == id && a.Kind == ActorKind.Agent && a.DeletedAt == null, ct) ?? throw new NotFoundException($"Agent {id} not found.");

    private async Task ClearOtherTriageAsync(int? exceptId, CancellationToken ct)
    {
        var others = await db.Actors.Where(a => a.IsTriage && a.Id != exceptId).ToListAsync(ct);
        foreach (var o in others) o.IsTriage = false;
    }

    private async Task ClearOtherDispatcherAsync(int? exceptId, CancellationToken ct)
    {
        var others = await db.Actors.Where(a => a.IsDispatcher && a.Id != exceptId).ToListAsync(ct);
        foreach (var o in others) o.IsDispatcher = false;
    }

    public const int MaxResponsibilities = 2000;

    private static string? CleanResponsibilities(string? s)
    {
        var r = s?.Trim();
        if (string.IsNullOrEmpty(r)) return null;
        return r.Length > MaxResponsibilities ? throw new ValidationException($"Responsibilities are too long (max {MaxResponsibilities} characters).") : r;
    }

    private async Task<string> ValidateNameAsync(string? name, int? selfId, CancellationToken ct)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) throw new ValidationException("Name is required.");
        if (n.Length > 64) throw new ValidationException("Name is too long (max 64).");
        if (Reserved.Contains(n)) throw new ValidationException($"'{n}' is reserved.");
        var taken = (await db.Actors.AsNoTracking().Select(a => new { a.Id, a.Name }).ToListAsync(ct))
            .Any(a => a.Id != selfId && a.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        if (taken) throw new ConflictException($"The name '{n}' is already in use.");
        return n;
    }

    private static string InitialsFor(string? initials, string name)
    {
        var i = initials?.Trim();
        if (string.IsNullOrEmpty(i)) i = name[..1].ToUpperInvariant();
        return i.Length > 8 ? i[..8] : i;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new ConflictException("The name is already in use."); }   // unique-index race
    }
}
