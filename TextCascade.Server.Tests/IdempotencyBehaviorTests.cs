using System.Net.WebSockets;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class IdempotencyBehaviorTests
{
    private sealed class RecordingCoordinator : IConnectionCoordinator
    {
        public List<string> Warnings { get; } = new();
        private readonly CollectingLogger logger;

        public RecordingCoordinator()
        {
            logger = new CollectingLogger(Warnings);
        }

        public ILogger Logger => logger;

        public void CancelConnection(ConnectionContext connection, string reason) { }

        public void RebuildHub(UserHub hub) { }

        public void RemoveEmptyHubAfterRecovery(UserHub hub) { }

        private sealed class CollectingLogger : ILogger
        {
            private readonly List<string> warnings;

            public CollectingLogger(List<string> warnings)
            {
                this.warnings = warnings;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var entry = formatter(state, exception);
                if (logLevel == LogLevel.Warning)
                {
                    lock (warnings) { warnings.Add(entry); }
                }
            }
        }
    }

    private static (UserHub Hub, RecordingCoordinator Coordinator) NewHub(ulong initialVersion = 0)
    {
        var config = TextCascade.Server.Config.CreateDefaultConfig();
        var coordinator = new RecordingCoordinator();
        var statePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        var stateStore = new RuntimeStateStore(statePath, TimeSpan.Zero);
        var hub = new UserHub("alice", config, DateTimeOffset.FromUnixTimeSeconds(1760000000), coordinator, stateStore, initialVersion);
        return (hub, coordinator);
    }

    private static ConnectionContext NewConnection(string clientId = "client-A")
    {
        var socket = new ClientWebSocket();
        var config = TextCascade.Server.Config.CreateDefaultConfig();
        return new ConnectionContext($"conn-{Guid.NewGuid():N}", "alice", clientId, clientId, socket, null, config);
    }

    private static ClientClip Clip(string id, string payload, string hash = "h", bool encrypted = false) =>
        new(id, payload, encrypted, hash);

    private static (bool Acked, byte[]? Payload) DequeueAck(ConnectionContext connection)
    {
        while (connection.State.SendQueue.Reader.TryRead(out var payload))
        {
            var text = Encoding.UTF8.GetString(payload);
            if (text.Contains("clip_ack", StringComparison.Ordinal))
            {
                return (true, payload);
            }

            if (text.Contains("rate_limited", StringComparison.Ordinal))
            {
                return (false, payload);
            }
        }

        return (false, null);
    }

    private static void DrainQueue(ConnectionContext connection)
    {
        while (connection.State.SendQueue.Reader.TryRead(out _)) { }
    }

    // U22 — behavior level: draining the bucket does not block duplicate-id resends.
    [Fact]
    public void DuplicateId_AfterBucketDrained_StillAcked()
    {
        var (hub, coordinator) = NewHub();
        var sender = NewConnection();

        var now = DateTimeOffset.FromUnixTimeSeconds(1760000000);

        // Drain the burst (default 10) with distinct clips; time does not advance so refill stays 0.
        for (var index = 0; index < 10; index++)
        {
            hub.ApplyClip(Clip($"id-{index}", $"payload-{index}"), sender, now);
        }
        DrainQueue(sender);

        // 11th distinct clip must be rate limited.
        hub.ApplyClip(Clip("id-overflow", "payload-overflow"), sender, now);
        Assert.False(DequeueAck(sender).Acked, "11th distinct clip should be rate limited");

        // A duplicate of the very first clip must still be acked without consuming a token.
        hub.ApplyClip(Clip("id-0", "payload-0"), sender, now);
        var duplicate = DequeueAck(sender);
        Assert.True(duplicate.Acked, "duplicate id should bypass the token bucket");

        using var ack = JsonDocument.Parse(duplicate.Payload!);
        // The duplicate ack replays the version id-0 originally received (the 1st distinct clip), and
        // the hub version must not have advanced past the drained burst.
        Assert.Equal(1UL, ack.RootElement.GetProperty("version").GetUInt64());
        Assert.Equal(10UL, hub.Version);
    }

    // U23
    [Fact]
    public void DuplicateId_NewContent_IsTreatedAsFreshMessage()
    {
        var (hub, coordinator) = NewHub();
        var sender = NewConnection();
        var now = DateTimeOffset.FromUnixTimeSeconds(1760000000);

        hub.ApplyClip(Clip("same-id", "first"), sender, now);
        Assert.Equal(1UL, hub.Version);
        DrainQueue(sender);

        coordinator.Warnings.Clear();
        hub.ApplyClip(Clip("same-id", "second", "h2"), sender, now);

        // Fresh message: consumes a token, generates a new version, logs the reuse warning.
        var ack = DequeueAck(sender);
        Assert.True(ack.Acked, "fresh-clip path should succeed while tokens remain");
        Assert.Equal(2UL, hub.Version);
        Assert.Contains(coordinator.Warnings, entry => entry.Contains("Replacing reused clip id", StringComparison.Ordinal));
    }

    // U24 — documented dead-branch behavior of the ring itself.
    [Fact]
    public void IsUnchangedDuplicate_ForUnknownId_ReturnsFalse()
    {
        var ring = new SeenIdRing(4);
        Assert.False(ring.IsUnchangedDuplicate("missing", "payload", "hash", false, out var latest));
        Assert.Null(latest);
    }

    // U25 — bucket edge cases beyond the existing refill test.
    [Fact]
    public void TryAcquire_BoundaryCases()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1760000000);

        // Burst boundary: burst acquisitions pass, the next one at the same instant fails.
        var bucket = new TokenBucket(3, 2.0, now);
        Assert.True(bucket.TryAcquire(now));
        Assert.True(bucket.TryAcquire(now));
        Assert.True(bucket.TryAcquire(now));
        Assert.False(bucket.TryAcquire(now));

        // Half-second refill (2 tokens/sec) grants one token.
        Assert.True(bucket.TryAcquire(now.AddMilliseconds(500)));

        // Clock moving backwards is rejected outright.
        Assert.False(bucket.TryAcquire(now));
    }

    [Fact]
    public void DuplicateId_RateLimitedError_CarriesReferenceId()
    {
        var (hub, _) = NewHub();
        var sender = NewConnection();
        var now = DateTimeOffset.FromUnixTimeSeconds(1760000000);

        for (var index = 0; index < 10; index++)
        {
            hub.ApplyClip(Clip($"id-{index}", $"payload-{index}"), sender, now);
        }
        DrainQueue(sender);

        hub.ApplyClip(Clip("id-overflow", "payload-overflow"), sender, now);
        var (acked, payload) = DequeueAck(sender);
        Assert.False(acked);
        Assert.NotNull(payload);

        using var error = JsonDocument.Parse(payload!);
        Assert.Equal("rate_limited", error.RootElement.GetProperty("code").GetString());
        Assert.Equal("id-overflow", error.RootElement.GetProperty("referenceId").GetString());
    }
}