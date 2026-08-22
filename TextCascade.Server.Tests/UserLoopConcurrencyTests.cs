using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class UserLoopConcurrencyTests
{
    private sealed class FakeCoordinator : IConnectionCoordinator
    {
        public ILogger Logger => NullLogger.Instance;
        public void CancelConnection(ConnectionContext connection, string reason) { }
        public void RebuildHub(UserHub hub) { }
        public void RemoveEmptyHubAfterRecovery(UserHub hub) { }
    }

    [Fact]
    public async Task StartIfIdleCreatesSingleTaskUnderConcurrency()
    {
        var tempState = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var stateStore = new RuntimeStateStore(tempState);
            var config = TextCascade.Server.Config.CreateDefaultConfig();
            var hub = new UserHub("alice", config, DateTimeOffset.UtcNow, new FakeCoordinator(), stateStore, 1UL);

            var startedTasks = new Task[100];
            var runners = new Task[100];
            for (var i = 0; i < 100; i++)
            {
                var idx = i;
                runners[i] = Task.Factory.StartNew(() => { startedTasks[idx] = hub.StartIfIdle(); }, TaskCreationOptions.LongRunning);
            }

            await Task.WhenAll(runners);

            var firstTask = startedTasks[0];
            Assert.NotNull(firstTask);
            for (var i = 1; i < startedTasks.Length; i++)
            {
                Assert.Same(firstTask, startedTasks[i]);
            }

            hub.UserChannel.Writer.Complete();
            await firstTask;
        }
        finally
        {
            if (File.Exists(tempState)) File.Delete(tempState);
        }
    }

    [Fact]
    public async Task RunUserLoopRejectsConcurrentReader()
    {
        var tempState = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        using var cts = new CancellationTokenSource();
        try
        {
            var stateStore = new RuntimeStateStore(tempState);
            var config = TextCascade.Server.Config.CreateDefaultConfig();
            var hub = new UserHub("alice", config, DateTimeOffset.UtcNow, new FakeCoordinator(), stateStore, 1UL);

            var firstLoop = Task.Run(() => hub.RunUserLoopAsync(cts.Token));

            // Wait a small amount to ensure first loop has claimed readerActive
            await Task.Delay(50);

            // Second concurrent reader must throw InvalidOperationException
            await Assert.ThrowsAsync<InvalidOperationException>(() => hub.RunUserLoopAsync(cts.Token));

            hub.UserChannel.Writer.Complete();
            await firstLoop;
        }
        finally
        {
            if (File.Exists(tempState)) File.Delete(tempState);
        }
    }
}
