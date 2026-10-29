using System.Net.Http.Json;
using DoiQ.App.Core;
using DoiQ.Client.Services;
using DoiQ.Shared.Dtos;

namespace DoiQ.App.Services;

/// <summary>
/// Tells the user, while the app is in the background, about tasks that wait on them or mention them. It reads the
/// inbox when a pushed task change or comment arrives and when the app comes back to the front, and notifies what is newer than
/// the last event it looked at (kept per server, so nothing is announced twice and the first run announces nothing).
/// There is no server push: with the app closed, nothing arrives until it is opened again.
/// </summary>
public sealed class NeedsYouNotifier : IDisposable
{
    private static readonly HashSet<string> Reasons = ["AssignedWaiting", "Mentioned"];

    private readonly HttpClient _http;
    private readonly BoardHubClient _hub;
    private readonly ServerSettings _server;
    private readonly INotifier _notifier;
    private readonly AppLifecycle _lifecycle;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started, _again;

    public NeedsYouNotifier(HttpClient http, BoardHubClient hub, ServerSettings server, INotifier notifier, AppLifecycle lifecycle)
    {
        _http = http;
        _hub = hub;
        _server = server;
        _notifier = notifier;
        _lifecycle = lifecycle;
    }

    public async Task Start(CancellationToken ct)
    {
        if (_started) return;
        _started = true;
        _hub.InboxMayHaveChanged += OnPushed;
        _lifecycle.Resumed += OnResumed;
        try { await _notifier.RequestPermission(ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { /* refused or unsupported: the app works without */ }
        await Check(ct);
    }

    private void OnPushed() => _ = Check(_cts.Token);
    private void OnResumed() => _ = Check(_cts.Token);

    private string WatermarkKey => $"doiq.notified-through:{_server.BaseUrl}";

    /// <summary>One inbox read at a time; a change arriving meanwhile gets one more read after it.</summary>
    private async Task Check(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) { _again = true; return; }
        try
        {
            do
            {
                _again = false;
                await CheckOnce(ct);
            } while (_again && !ct.IsCancellationRequested);
        }
        catch (Exception) { /* offline or signed out: the next change or resume tries again */ }
        finally { _gate.Release(); }
    }

    private async Task CheckOnce(CancellationToken ct)
    {
        var inbox = await _http.GetFromJsonAsync<List<InboxItemDto>>("api/inbox", ct) ?? [];
        var seen = Preferences.Default.Get(WatermarkKey, -1L);
        if (inbox.Count == 0)
        {
            // An empty first look still counts as the first run, so the first task that lands later is announced.
            if (seen < 0) Preferences.Default.Set(WatermarkKey, 0L);
            return;
        }
        var newest = inbox.Max(i => i.LatestEventId);
        Preferences.Default.Set(WatermarkKey, Math.Max(seen, newest));
        if (seen < 0 || _lifecycle.IsForeground) return;   // first run, or the user is looking at the app

        foreach (var item in inbox.Where(i => i.LatestEventId > seen && Reasons.Contains(i.Reason)))
        {
            var body = item.Reason == "Mentioned" ? "You were mentioned." : "It is waiting on you.";
            await _notifier.Show(item.Task.Title, body, ct);
        }
    }

    public void Dispose()
    {
        _hub.InboxMayHaveChanged -= OnPushed;
        _lifecycle.Resumed -= OnResumed;
        _cts.Cancel();
        _cts.Dispose();
    }
}
