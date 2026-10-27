using DoiQ.Shared.Dtos;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace DoiQ.Client.Services;

public enum HubState { Connecting, Live, Reconnecting, Offline }

/// <summary>
/// The browser's SignalR connection to /hubs/board. Pushed events go to <see cref="IBoardSync"/>, which ignores any it
/// already applied; after every (re)connect it also fetches what was missed through /api/events.
/// </summary>
public sealed class BoardHubClient : IAsyncDisposable
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    /// <summary>Attempts that fail in a row before the indicator says Offline instead of Reconnecting.</summary>
    private const int OfflineAfterAttempts = 3;

    private readonly IBoardSync? _sync;
    private readonly AuthState _auth;
    private readonly NavigationManager _nav;
    private readonly ClientOptions _options;
    private HubConnection? _connection;
    private CancellationTokenSource? _cts;

    public BoardHubClient(IDoiQApiClient api, AuthState auth, NavigationManager nav, ClientOptions? options = null)
    {
        _sync = api as IBoardSync;   // the in-memory fake has nothing to connect to
        _auth = auth;
        _nav = nav;
        _options = options ?? ClientOptions.Default;
        _auth.Changed += OnAuthChanged;
        State = _sync is null ? HubState.Live : HubState.Offline;
    }

    public HubState State { get; private set; }
    public event Action? StateChanged;

    /// <summary>Every pushed task change, on any board, as it arrives (the app's notifications listen here).</summary>
    public event Action<TaskChangedMessage>? TaskChanged;

    private void Set(HubState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke();
    }

    private void OnAuthChanged()
    {
        if (_auth.Status != AuthStatus.SignedIn) _ = StopAsync();
    }

    /// <summary>Connects once the board has loaded. Safe to call again; a second call while connected does nothing.</summary>
    public async Task StartAsync()
    {
        if (_sync is null || _connection is not null) return;

        var cts = _cts = new CancellationTokenSource();
        var connection = _connection = new HubConnectionBuilder()
            .WithUrl(_options.HubUrl?.Invoke() ?? _nav.ToAbsoluteUri(BoardHubEvents.HubPath), o => _options.ConfigureHub?.Invoke(o))
            .WithAutomaticReconnect(new RetryPolicy(this))
            .Build();

        connection.On<TaskChangedMessage>(BoardHubEvents.TaskChanged, m => { TaskChanged?.Invoke(m); return Apply(() => _sync.ApplyAsync(m)); });
        connection.On<TaskDeletedMessage>(BoardHubEvents.TaskDeleted, m => Apply(() => _sync.ApplyAsync(m)));
        connection.On<CommentAddedMessage>(BoardHubEvents.CommentAdded, m => Apply(() => _sync.ApplyAsync(m)));
        connection.On<FieldChangedMessage>(BoardHubEvents.FieldChanged, m => Apply(() => _sync.ApplyAsync(m)));
        connection.On<SeenChangedMessage>(BoardHubEvents.SeenChanged, m => Apply(() => _sync.ApplyAsync(m)));
        connection.On<BoardChangedMessage>(BoardHubEvents.BoardChanged, m => Apply(() => _sync.ApplyAsync(m)));
        connection.Reconnected += _ => CatchUpAsync(cts.Token);
        connection.Closed += _ => { if (!cts.IsCancellationRequested) Set(HubState.Offline); return Task.CompletedTask; };

        Set(HubState.Connecting);
        try
        {
            await LoadAsync(cts.Token);
            await ConnectAsync(connection, cts.Token);
        }
        catch (Exception) when (cts.IsCancellationRequested) { /* stopped meanwhile */ }
    }

    /// <summary>
    /// The board must be loaded before connecting (it records where catch-up starts). If it cannot load (server down),
    /// the page says so and the indicator says Offline, and this keeps trying, so live updates start once it is back.
    /// Signing out stops it.
    /// </summary>
    private async Task LoadAsync(CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _sync!.EnsureLoaded(ct);
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                Set(HubState.Offline);
                await Task.Delay(Backoff[Math.Min(attempt + 1, Backoff.Length - 1)], ct);
            }
        }
    }

    private async Task ConnectAsync(HubConnection connection, CancellationToken ct)
    {
        for (var attempt = 0; !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await connection.StartAsync(ct);
                break;
            }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (Exception)
            {
                Set(attempt + 1 >= OfflineAfterAttempts ? HubState.Offline : HubState.Reconnecting);
                await Task.Delay(Backoff[Math.Min(attempt + 1, Backoff.Length - 1)], ct);
            }
        }
        await CatchUpAsync(ct);
    }

    /// <summary>Fetches what was missed, then reports Live. Retries while the connection holds; a miss never leaves the board silently stale.</summary>
    private async Task CatchUpAsync(CancellationToken ct)
    {
        Set(HubState.Reconnecting);
        for (var attempt = 0; !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await _sync!.CatchUpAsync();
                Set(HubState.Live);
                return;
            }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (Exception)
            {
                try { await Task.Delay(Backoff[Math.Min(attempt + 1, Backoff.Length - 1)], ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>One pushed message. If applying it fails, the next catch-up repairs the board from the event feed.</summary>
    private async Task Apply(Func<Task> apply)
    {
        try { await apply(); }
        catch (Exception)
        {
            try { await _sync!.CatchUpAsync(); } catch (Exception) { /* the next reconnect or push tries again */ }
        }
    }

    public async Task StopAsync()
    {
        var connection = _connection;
        _connection = null;
        _cts?.Cancel();
        _cts = null;
        if (connection is not null)
        {
            try { await connection.DisposeAsync(); } catch (Exception) { /* already closed */ }
        }
        _sync?.Reset();
        if (_sync is not null) Set(HubState.Offline);
    }

    public async ValueTask DisposeAsync()
    {
        _auth.Changed -= OnAuthChanged;
        await StopAsync();
    }

    private sealed class RetryPolicy(BoardHubClient owner) : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext context)
        {
            // Never give up: the server may be restarting. The indicator says Offline once it has been a while.
            owner.Set(context.PreviousRetryCount + 1 >= OfflineAfterAttempts ? HubState.Offline : HubState.Reconnecting);
            return Backoff[Math.Min(context.PreviousRetryCount, Backoff.Length - 1)];
        }
    }
}
