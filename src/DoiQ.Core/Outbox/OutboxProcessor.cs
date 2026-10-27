using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoiQ.Core.Data;
using DoiQ.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DoiQ.Core.Outbox;

/// <summary>
/// Delivers due outbox rows as signed webhooks. Signature: hex HMAC-SHA256 over "&lt;timestamp&gt;.&lt;raw body&gt;"
/// in X-Webhook-Signature-V2, with X-Webhook-Timestamp (unix seconds). Failures retry with backoff (see OutboxOptions).
/// Todo rows go to the agent's WebhookUrl (Hermes route "doiq"), Notify rows to its NotifyUrl (route "doiq-notify").
/// </summary>
public sealed class OutboxProcessor(DoiQDbContext db, HttpClient http, TimeProvider time, IOptions<OutboxOptions> options, ILogger<OutboxProcessor> logger)
{
    private readonly OutboxOptions _options = options.Value;

    public static string Sign(string secret, string timestamp, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();

    /// <summary>Returns how many rows were delivered.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var due = await db.Outbox.Include(o => o.Event)
            .Where(o => o.SentAt == null && o.NotBefore <= now).OrderBy(o => o.Id).Take(50).ToListAsync(ct);
        if (due.Count == 0) return 0;
        var actors = await db.Actors.AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
        var sent = 0;

        foreach (var row in due)
        {
            actors.TryGetValue(row.TargetActorId, out var target);
            var url = row.Kind == OutboxKind.Todo ? target?.WebhookUrl : target?.NotifyUrl;
            if (string.IsNullOrWhiteSpace(url) || target is { IsActive: false })
            {
                row.SentAt = now;   // nowhere to deliver (or agent deactivated): do not retry forever
                row.LastError = target is { IsActive: false } ? "Target agent is inactive." : "No webhook URL configured for target.";
                continue;
            }

            try
            {
                var title = await db.Tasks.IgnoreQueryFilters().Where(t => t.Id == row.TaskId).Select(t => t.Title).FirstOrDefaultAsync(ct);
                var evActor = row.Event is null ? null : actors.GetValueOrDefault(row.Event.ActorId)?.Name;
                var boardName = await db.Boards.AsNoTracking().Where(b => b.Id == row.BoardId).Select(b => b.Name).FirstOrDefaultAsync(ct);
                var body = row.Kind == OutboxKind.Todo
                    ? JsonSerializer.Serialize(new { eventId = row.EventId, boardId = row.BoardId, boardName, taskId = row.TaskId, type = row.Event?.Type.ToString(), actor = evActor, title, at = row.Event?.At, reason = row.PayloadJson })
                    : JsonSerializer.Serialize(new { eventId = row.EventId, boardId = row.BoardId, boardName, taskId = row.TaskId, message = row.PayloadJson ?? title });
                var ts = time.GetUtcNow().ToUnixTimeSeconds().ToString();
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                req.Headers.Add("X-Webhook-Timestamp", ts);
                if (!string.IsNullOrEmpty(target!.WebhookSecret)) req.Headers.Add("X-Webhook-Signature-V2", Sign(target.WebhookSecret, ts, body));
                using var resp = await http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
                row.SentAt = now;
                row.LastError = null;
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                row.Attempts++;
                row.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                if (row.Attempts >= _options.MaxAttempts) row.SentAt = now;   // give up; LastError records why
                else row.NotBefore = now.AddSeconds(Math.Min(_options.RetryBaseSeconds * Math.Pow(2, row.Attempts - 1), _options.RetryMaxSeconds));
                logger.LogWarning("Outbox {Id} delivery failed (attempt {Attempts}): {Error}", row.Id, row.Attempts, row.LastError);
            }
        }

        await db.SaveChangesAsync(ct);
        return sent;
    }
}
