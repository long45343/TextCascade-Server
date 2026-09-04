using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class RuntimeStateAndProtocolTests
{
    private static readonly DateTimeOffset TestStartTime = DateTimeOffset.FromUnixTimeSeconds(1760000000);

    [Fact]
    public void StateStorePersistsHighestVersionAtomically()
    {
        var path = Path.Combine(Path.GetTempPath(), $"textcascade-state-{Guid.NewGuid():N}.json");
        try
        {
            using (var first = new RuntimeStateStore(path, TimeSpan.Zero))
            {
                first.SaveVersion("alice", 7UL);
                first.Flush();
            }

            using (var second = new RuntimeStateStore(path, TimeSpan.Zero))
            {
                second.SaveVersion("alice", 5UL);
                second.Flush();

                Assert.Equal(7UL, second.GetVersion("alice"));
            }

            using (var third = new RuntimeStateStore(path, TimeSpan.Zero))
            {
                Assert.Equal(7UL, third.GetVersion("alice"));
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task StateStoreFlushesPeriodicallyInBackground()
    {
        var path = Path.Combine(Path.GetTempPath(), $"textcascade-state-{Guid.NewGuid():N}.json");
        try
        {
            using (var store = new RuntimeStateStore(path, TimeSpan.FromMilliseconds(50)))
            {
                store.SaveVersion("alice", 12UL);
                Assert.Equal(12UL, store.GetVersion("alice"));

                // Wait for background timer tick
                await Task.Delay(150);

                using var reloaded = new RuntimeStateStore(path, TimeSpan.Zero);
                Assert.Equal(12UL, reloaded.GetVersion("alice"));
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void StateStoreConcurrentSaveVersionMaintainsHighestValue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"textcascade-state-{Guid.NewGuid():N}.json");
        try
        {
            using var store = new RuntimeStateStore(path, TimeSpan.Zero);
            Parallel.For(1, 100, i =>
            {
                store.SaveVersion("alice", (ulong)i);
                store.SaveVersion("bob", (ulong)(100 - i));
            });

            Assert.Equal(99UL, store.GetVersion("alice"));
            Assert.Equal(99UL, store.GetVersion("bob"));

            store.Flush();

            using var reloaded = new RuntimeStateStore(path, TimeSpan.Zero);
            Assert.Equal(99UL, reloaded.GetVersion("alice"));
            Assert.Equal(99UL, reloaded.GetVersion("bob"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void StateStoreRejectsInvalidFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"textcascade-state-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"entries":[{"username":"alice","version":0}]}""", Encoding.UTF8);
            Assert.Throws<InvalidOperationException>(() => new RuntimeStateStore(path, TimeSpan.Zero));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ClipValidationAcceptsClientLocalHash()
    {
        var config = TextCascade.Server.Config.CreateDefaultConfig();
        Assert.True(Protocol.ValidateClipMessage(new ClientClip("id", "payload", true, new string('a', 64)), config));
        Assert.True(Protocol.ValidateClipMessage(new ClientClip("id", "payload", true, "client-local-hash"), config));
        Assert.False(Protocol.ValidateClipMessage(new ClientClip("id", "payload", true, ""), config));
        Assert.False(Protocol.ValidateClipMessage(new ClientClip("id", "payload", true, new string('a', 4097)), config));
    }

    [Fact]
    public void ConfigParsesStateFilePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"textcascade-config-{Guid.NewGuid():N}.toml");
        try
        {
            File.WriteAllText(path, "[files]\nstate_file = \"/tmp/state.json\"\n");
            var config = TextCascade.Server.Config.LoadTomlConfig(path);
            Assert.Equal("/tmp/state.json", config.Files.StateFile);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RecoveryWindowRestoresSnapshotAtPersistedVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"textcascade-state-{Guid.NewGuid():N}.json");
        try
        {
            using (var initialStore = new RuntimeStateStore(path, TimeSpan.Zero))
            {
                initialStore.SaveVersion("alice", 7UL);
                initialStore.Flush();
            }

            var config = TextCascade.Server.Config.CreateDefaultConfig();
            using var stateStore = new RuntimeStateStore(path, TimeSpan.Zero);
            var server = new SyncServer(
                config,
                new UsersFile(),
                stateStore,
                new Argon2PasswordHasher(),
                TimeProvider.System,
                NullLogger<SyncServer>.Instance);
            var hub = new UserHub("alice", config, TestStartTime, server, server.RuntimeStateStore, 7UL);
            var modified = DateTimeOffset.FromUnixTimeSeconds(1759999990);
            hub.AcceptSnapshot(new ClientHello(
                "client-a",
                "Client A",
                7UL,
                new ClipSnapshot("restored", false, "client-local-hash", modified)));

            hub.CloseRecoveryWindow(TestStartTime.AddSeconds(3));

            Assert.NotNull(hub.Latest);
            Assert.Equal(7UL, hub.Version);
            Assert.Equal("restored", hub.Latest!.Payload);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RecoveryWindowIgnoresStaleSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"textcascade-state-{Guid.NewGuid():N}.json");
        try
        {
            using (var initialStore = new RuntimeStateStore(path, TimeSpan.Zero))
            {
                initialStore.SaveVersion("alice", 7UL);
                initialStore.Flush();
            }

            var config = TextCascade.Server.Config.CreateDefaultConfig();
            using var stateStore = new RuntimeStateStore(path, TimeSpan.Zero);
            var server = new SyncServer(
                config,
                new UsersFile(),
                stateStore,
                new Argon2PasswordHasher(),
                TimeProvider.System,
                NullLogger<SyncServer>.Instance);
            var hub = new UserHub("alice", config, TestStartTime, server, server.RuntimeStateStore, 7UL);
            hub.AcceptSnapshot(new ClientHello(
                "client-a",
                "Client A",
                6UL,
                new ClipSnapshot("stale", false, "client-local-hash", TestStartTime)));

            hub.CloseRecoveryWindow(TestStartTime.AddSeconds(3));

            Assert.Null(hub.Latest);
            Assert.Equal(7UL, hub.Version);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}