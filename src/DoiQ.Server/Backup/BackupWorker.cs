using DoiQ.Core.Backup;
using Microsoft.Extensions.Options;

namespace DoiQ.Server.Backup;

/// <summary>Runs <see cref="BackupService"/> once a day at <see cref="BackupOptions.Hour"/>, container local time.</summary>
public sealed class BackupWorker(BackupService backups, IOptions<BackupOptions> options, TimeProvider time, ILogger<BackupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Nightly backup is switched off (Backup:Enabled=false)");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetLocalNow();
            var next = BackupService.NextRun(now, options.Value.Hour);
            logger.LogInformation("Next backup at {Next}", next);
            try { await Task.Delay(next - now, time, stoppingToken); }
            catch (OperationCanceledException) { return; }

            try { await backups.Run(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Nightly backup failed");
            }
        }
    }
}
