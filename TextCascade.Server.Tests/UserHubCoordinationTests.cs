using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class UserHubCoordinationTests
{
    private sealed class FakeCoordinator : IConnectionCoordinator
    {
        public ILogger Logger { get; set; } = NullLogger.Instance;
        public List<(ConnectionContext Connection, string Reason)> Cancelled { get; } = new();
        public List<UserHub> RebuiltHubs { get; } = new();
        public List<UserHub> RemovedEmptyHubs { get; } = new();

        public void CancelConnection(ConnectionContext connection, string reason)
        {
            Cancelled.Add((connection, reason));
        }

        public void RebuildHub(UserHub hub)
        {
            RebuiltHubs.Add(hub);
        }

        public void RemoveEmptyHubAfterRecovery(UserHub hub)
        {
            RemovedEmptyHubs.Add(hub);
        }
    }

    [Fact]
    public void UserHubDoesNotDependOnSyncServerConcreteType()
    {
        var fakeCoordinator = new FakeCoordinator();
        var tempState = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var stateStore = new RuntimeStateStore(tempState);
            var config = TextCascade.Server.Config.CreateDefaultConfig() with
            {
                Limits = TextCascade.Server.Config.CreateDefaultConfig().Limits with { SnapshotWindowSeconds = 0 },
            };

            var hub = new UserHub(
                "alice",
                config,
                DateTimeOffset.UtcNow,
                fakeCoordinator,
                stateStore,
                1UL);

            var now = DateTimeOffset.UtcNow;
            hub.CloseRecoveryWindow(now);

            Assert.Single(fakeCoordinator.RemovedEmptyHubs);
            Assert.Same(hub, fakeCoordinator.RemovedEmptyHubs[0]);
        }
        finally
        {
            if (File.Exists(tempState)) File.Delete(tempState);
        }
    }

    [Fact]
    public async Task UserLoopFailureNotifiesCoordinator()
    {
        var fakeCoordinator = new FakeCoordinator();
        var tempState = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var stateStore = new RuntimeStateStore(tempState);
            var config = TextCascade.Server.Config.CreateDefaultConfig();

            // Set initial version to ulong.MaxValue so next clip throws Version overflow
            var hub = new UserHub(
                "alice",
                config,
                DateTimeOffset.UtcNow,
                fakeCoordinator,
                stateStore,
                ulong.MaxValue);

            _ = hub.StartIfIdle();

            // Enqueue a clip job to trigger overflow
            var dummySocket = new System.Net.WebSockets.ClientWebSocket();
            var conn = new ConnectionContext("conn-1", "alice", "c1", "Client1", dummySocket, hub, config);
            hub.AddConnection(conn);

            // Close recovery window so clip can be applied
            hub.CloseRecoveryWindow(DateTimeOffset.UtcNow.AddSeconds(10));

            var clip = new ClientClip("id-overflow", "data", false, "hash");
            hub.UserChannel.Writer.TryWrite(new ClipJob(conn, clip));

            // Wait for user loop failure notification
            var timeout = DateTime.UtcNow.AddSeconds(3);
            while (fakeCoordinator.RebuiltHubs.Count == 0 && DateTime.UtcNow < timeout)
            {
                await Task.Delay(20);
            }

            Assert.Single(fakeCoordinator.RebuiltHubs);
            Assert.Same(hub, fakeCoordinator.RebuiltHubs[0]);
        }
        finally
        {
            if (File.Exists(tempState)) File.Delete(tempState);
        }
    }
}


