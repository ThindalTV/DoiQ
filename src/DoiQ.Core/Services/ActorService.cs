using DoiQ.Core.Data;
using DoiQ.Core.Domain;
using DoiQ.Core.Options;
using DoiQ.Shared.Dtos;
using DoiQ.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DoiQ.Core.Services;

/// <summary>Optional config (Agents:N:*): an agent to create at startup when no agent of that name exists yet.</summary>
public sealed record AgentSeed(string Name, string Initials, string ApiKey, bool IsTriage, string? WebhookUrl = null, string? WebhookSecret = null, string? NotifyUrl = null);

public sealed class ActorService(DoiQDbContext db, TimeProvider? time = null, ILogger<ActorService>? logger = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private static readonly TimeSpan SeenThrottle = TimeSpan.FromMinutes(1);

    public static ActorInfo ToInfo(Actor a) => new(a.Id, a.Kind, a.Name, a.Initials, a.IsTriage);

    /// <summary>
    /// Ensures the user exists. Agents are managed from the UI; any agents given here (config/env) are create-only:
    /// an agent whose name is already taken is left exactly as it is (key, webhooks, initials, triage flag), so a key
    /// rotated in Settings survives a restart. At most one agent is triage: a seed asking for a second one is created
    /// without the flag and a warning is logged.
    /// </summary>
    public async Task Seed(AuthOptions auth, IEnumerable<AgentSeed> agents, CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (!await db.Actors.AnyAsync(a => a.Kind == ActorKind.Human, ct))
            db.Actors.Add(new Actor { Kind = ActorKind.Human, Name = auth.UserName, Initials = auth.UserInitials, CreatedAt = now });

        var triageTaken = await db.Actors.AnyAsync(a => a.IsTriage, ct);
        foreach (var seed in agents)
        {
            var taken = (await db.Actors.Select(a => a.Name).ToListAsync(ct)).Any(n => n.Equals(seed.Name, StringComparison.OrdinalIgnoreCase))
                || db.Actors.Local.Any(a => a.Name.Equals(seed.Name, StringComparison.OrdinalIgnoreCase));
            if (taken) continue;

            var triage = seed.IsTriage && !triageTaken;
            if (seed.IsTriage && triageTaken)
                logger?.LogWarning("Configured agent '{Agent}' asks to be the triage agent, but one already exists; created without the triage flag.", seed.Name);
            triageTaken |= triage;
            db.Actors.Add(new Actor
            {
                Kind = ActorKind.Agent, Name = seed.Name, Initials = seed.Initials, IsTriage = triage, CreatedAt = now,
                ApiKeyHash = ApiKeyHasher.Hash(seed.ApiKey), WebhookUrl = seed.WebhookUrl, WebhookSecret = seed.WebhookSecret, NotifyUrl = seed.NotifyUrl,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<ActorInfo?> AuthenticateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;
        var hash = ApiKeyHasher.Hash(apiKey);
        var a = await db.Actors.AsNoTracking().FirstOrDefaultAsync(x => x.ApiKeyHash == hash && x.IsActive && x.Kind == ActorKind.Agent, ct);
        if (a is null) return null;

        var now = _time.GetUtcNow().UtcDateTime;
        if (a.LastSeenAt is null || now - a.LastSeenAt >= SeenThrottle)
            await db.Actors.Where(x => x.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAt, (DateTime?)now), ct);
        return ToInfo(a);
    }

    public async Task<ActorInfo?> GetHumanAsync(CancellationToken ct = default)
    {
        var a = await db.Actors.AsNoTracking().FirstOrDefaultAsync(x => x.Kind == ActorKind.Human && x.IsActive, ct);
        return a is null ? null : ToInfo(a);
    }

    public async Task<IReadOnlyList<ActorDto>> ListAsync(CancellationToken ct = default) =>
        await db.Actors.AsNoTracking().Where(a => a.IsActive).OrderBy(a => a.Id)
            .Select(a => new ActorDto(a.Id, a.Kind, a.Name, a.Initials, a.IsTriage)).ToListAsync(ct);
}
