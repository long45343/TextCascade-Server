using Microsoft.Extensions.Hosting;

namespace TextCascade.Server;

public sealed class HeartbeatScannerService : IHostedService, IDisposable
{
    private Timer? timer;

    private readonly SyncServer syncServer;

    public HeartbeatScannerService(SyncServer syncServer)
    {
        this.syncServer = syncServer;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        timer = new Timer(Scan, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        return Task.CompletedTask;
    }

    private void Scan(object? state)
    {
        var now = DateTimeOffset.UtcNow;
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

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        timer?.Change(Timeout.Infinite, 0);
        await syncServer.ShutdownAsync(TimeSpan.FromSeconds(2), DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        timer?.Dispose();
    }
}
