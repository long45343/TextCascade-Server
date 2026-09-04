using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TextCascade.Server;

public sealed class HeartbeatScannerService : BackgroundService
{
    private readonly SyncServer syncServer;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<HeartbeatScannerService> logger;

    public HeartbeatScannerService(SyncServer syncServer, TimeProvider timeProvider, ILogger<HeartbeatScannerService> logger)
    {
        this.syncServer = syncServer;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    Scan(timeProvider.GetUtcNow());
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Heartbeat scan failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await syncServer.ShutdownAsync(TimeSpan.FromSeconds(2), timeProvider.GetUtcNow());
    }

    private void Scan(DateTimeOffset now)
    {
        syncServer.ScanHeartbeats(now);

        var recoveryEnd = syncServer.ProcessStartTime.AddSeconds(
            syncServer.Config.Limits.SnapshotWindowSeconds);
        if (now < recoveryEnd)
        {
            return;
        }

        foreach (var pair in syncServer.Registry.All)
        {
            pair.Value.CloseRecoveryWindow(now);
        }
    }
}
