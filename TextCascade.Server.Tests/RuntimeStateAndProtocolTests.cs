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
            var first = new RuntimeStateStore(path);
            first.SaveVersion("alice", 7UL);

            var second = new RuntimeStateStore(path);
            second.SaveVersion("alice", 5UL);

            Assert.Equal(7UL, second.GetVersion("alice"));
            Assert.Equal(7UL, new RuntimeStateStore(path).GetVersion("alice"));
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
            Assert.Throws<InvalidOperationException>(() => new RuntimeStateStore(path));
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
            new RuntimeStateStore(path).SaveVersion("alice", 7UL);
            var config = TextCascade.Server.Config.CreateDefaultConfig();
            var server = new SyncServer(
                config,
                new UsersFile(),
                new RuntimeStateStore(path),
                new Argon2PasswordHasher(),
                new SystemClock(),
                NullLogger<SyncServer>.Instance);
            var hub = new UserHub("alice", config, TestStartTime, server, 7UL);
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
            new RuntimeStateStore(path).SaveVersion("alice", 7UL);
            var config = TextCascade.Server.Config.CreateDefaultConfig();
            var server = new SyncServer(
                config,
                new UsersFile(),
                new RuntimeStateStore(path),
                new Argon2PasswordHasher(),
                new SystemClock(),
                NullLogger<SyncServer>.Instance);
            var hub = new UserHub("alice", config, TestStartTime, server, 7UL);
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
