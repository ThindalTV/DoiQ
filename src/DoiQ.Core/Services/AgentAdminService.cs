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
        new(a.Id, a.Name, a.Initials, a.IsTriage, a.IsActive, a.WebhookUrl, a.NotifyUrl, !string.IsNullOrEmpty(a.WebhookSecret), a.CreatedAt, a.LastSeenAt);

    public async Task<IReadOnlyList<AgentAdminDto>> ListAsync(CancellationToken ct = default) =>
        (await db.Actors.AsNoTracking().Where(a => a.Kind == ActorKind.Agent).OrderBy(a => a.Id).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<CreatedAgentResult> CreateAsync(CreateAgentRequest req, CancellationToken ct = default)
    {
        var name = await ValidateNameAsync(req.Name, null, ct);
        var key = RandomToken("dq_");
        var secret = string.IsNullOrWhiteSpace(req.WebhookSecret) ? null : req.WebhookSecret;
        var generated = secret is null ? RandomToken("whsec_") : null;
        var agent = new Actor
        {
            Kind = ActorKind.Agent, Name = name, Initials = InitialsFor(req.Initials, name),
            ApiKeyHash = ApiKeyHasher.Hash(key), IsTriage = req.IsTriage,
            WebhookUrl = Clean(req.WebhookUrl), NotifyUrl = Clean(req.NotifyUrl), WebhookSecret = secret ?? generated,
            IsActive = true, CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.Actors.Add(agent);
        if (req.IsTriage) await ClearOtherTriageAsync(null, ct);
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
        await SaveAsync(ct);
        return ToDto(a);
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
        await db.Actors.FirstOrDefaultAsync(a => a.Id == id && a.Kind == ActorKind.Agent, ct) ?? throw new NotFoundException($"Agent {id} not found.");

    private async Task ClearOtherTriageAsync(int? exceptId, CancellationToken ct)
    {
        var others = await db.Actors.Where(a => a.IsTriage && a.Id != exceptId).ToListAsync(ct);
        foreach (var o in others) o.IsTriage = false;
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
