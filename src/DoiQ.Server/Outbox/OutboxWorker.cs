using System.Threading.Channels;
using DoiQ.Core.Outbox;
using DoiQ.Core.Services;
using Microsoft.Extensions.Options;

namespace DoiQ.Server.Outbox;

/// <summary>The channel only wakes the worker; the Outbox table is the source of truth, so a restart loses nothing.</summary>
public sealed class ChannelOutboxSignal : IOutboxSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    public void Signal() => _channel.Writer.TryWrite(true);
    public ValueTask<bool> WaitAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);
    public bool TryRead() => _channel.Reader.TryRead(out _);
}

public static class OutboxServiceCollectionExtensions
{
    /// <summary>Registers the signal, the delivery HttpClient, the Outbox options and the background worker.</summary>
    public static IServiceCollection AddDoiQOutbox(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<OutboxOptions>(config.GetSection(OutboxOptions.Section));
        services.AddSingleton<ChannelOutboxSignal>();
        services.AddSingleton<IOutboxSignal>(sp => sp.GetRequiredService<ChannelOutboxSignal>());
        services.AddHttpClient<OutboxProcessor>(c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddHostedService<OutboxWorker>();
        return services;
    }
}

public sealed class OutboxWorker(IServiceScopeFactory scopes, ChannelOutboxSignal signal, IOptions<OutboxOptions> options, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(Math.Max(0.05, options.Value.PollSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
                while (await processor.ProcessDueAsync(stoppingToken) > 0) { }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Outbox pass failed");
            }

            // Wake on a signal, but poll regularly: rows carry a NotBefore debounce and retries.
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wait.CancelAfter(poll);
            try { await signal.WaitAsync(wait.Token); }
            catch (OperationCanceledException) { }
        }
    }
}
