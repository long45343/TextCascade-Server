using Microsoft.Extensions.Logging.Abstractions;
using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class UserFileWatcherTests
{
    private const string ValidHash = "$argon2id$v=19$m=19456,t=2,p=1$c2FsdA$aG9zdA";

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.True(condition(), "Condition was not met within timeout.");
    }

    [Fact]
    public async Task ReloadReplacesUserLookupAfterSave()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempUsers = Path.Combine(tempDir, "users.json");
        var tempState = Path.Combine(tempDir, "state.json");
        try
        {
            var initialUsers = new UsersFile
            {
                Users = [new UserRecord("alice", ValidHash, 1)],
                NextTokenVersion = 2,
            };
            UsersFile.SaveUsers(tempUsers, initialUsers);

            var config = TextCascade.Server.Config.CreateDefaultConfig() with
            {
                TokenSecret = new byte[32],
                Files = new FilesConfig(tempUsers, tempState),
            };
            var server = new SyncServer(
                config,
                initialUsers,
                new RuntimeStateStore(tempState),
                new Argon2PasswordHasher(),
                new SystemClock(),
                NullLogger<SyncServer>.Instance);

            using var watcher = new UserFileWatcher(
                tempUsers,
                server,
                NullLogger.Instance,
                debounce: TimeSpan.FromMilliseconds(20),
                pollFallback: TimeSpan.FromSeconds(1));
            watcher.Start();

            Assert.True(server.UserLookup.ContainsKey("alice"));
            Assert.False(server.UserLookup.ContainsKey("bob"));

            // Add bob and save
            var updatedUsers = new UsersFile
            {
                Users =
                [
                    new UserRecord("alice", ValidHash, 1),
                    new UserRecord("bob", ValidHash, 2),
                ],
                NextTokenVersion = 3,
            };
            UsersFile.SaveUsers(tempUsers, updatedUsers);

            await WaitUntilAsync(() => server.UserLookup.ContainsKey("bob"), TimeSpan.FromSeconds(3));
            Assert.True(server.UserLookup.ContainsKey("bob"));
            Assert.True(server.UserLookup.ContainsKey("alice"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task InvalidReloadRetainsPreviousLookup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempUsers = Path.Combine(tempDir, "users.json");
        var tempState = Path.Combine(tempDir, "state.json");
        try
        {
            var initialUsers = new UsersFile
            {
                Users = [new UserRecord("alice", ValidHash, 1)],
                NextTokenVersion = 2,
            };
            UsersFile.SaveUsers(tempUsers, initialUsers);

            var config = TextCascade.Server.Config.CreateDefaultConfig() with
            {
                TokenSecret = new byte[32],
                Files = new FilesConfig(tempUsers, tempState),
            };
            var server = new SyncServer(
                config,
                initialUsers,
                new RuntimeStateStore(tempState),
                new Argon2PasswordHasher(),
                new SystemClock(),
                NullLogger<SyncServer>.Instance);

            using var watcher = new UserFileWatcher(
                tempUsers,
                server,
                NullLogger.Instance,
                debounce: TimeSpan.FromMilliseconds(20),
                pollFallback: TimeSpan.FromSeconds(1));
            watcher.Start();

            // Write invalid JSON
            await File.WriteAllTextAsync(tempUsers, "invalid json content!@#$");

            // Wait a bit to ensure watcher event processed
            await Task.Delay(200);

            // Previous lookup must still be alice
            Assert.True(server.UserLookup.ContainsKey("alice"));

            // Now recover with valid file containing charlie
            var recoveredUsers = new UsersFile
            {
                Users = [new UserRecord("charlie", ValidHash, 3)],
                NextTokenVersion = 4,
            };
            UsersFile.SaveUsers(tempUsers, recoveredUsers);

            await WaitUntilAsync(() => server.UserLookup.ContainsKey("charlie"), TimeSpan.FromSeconds(3));
            Assert.True(server.UserLookup.ContainsKey("charlie"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ConcurrentReloadObserversAlwaysSeeCompleteDictionary()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempUsers = Path.Combine(tempDir, "users.json");
        var tempState = Path.Combine(tempDir, "state.json");
        try
        {
            var usersA = new UsersFile
            {
                Users = [new UserRecord("alice", ValidHash, 1), new UserRecord("bob", ValidHash, 2)],
                NextTokenVersion = 3,
            };
            var usersB = new UsersFile
            {
                Users = [new UserRecord("charlie", ValidHash, 3), new UserRecord("david", ValidHash, 4)],
                NextTokenVersion = 5,
            };

            var config = TextCascade.Server.Config.CreateDefaultConfig() with { TokenSecret = new byte[32] };
            var server = new SyncServer(
                config,
                usersA,
                new RuntimeStateStore(tempState),
                new Argon2PasswordHasher(),
                new SystemClock(),
                NullLogger<SyncServer>.Instance);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var token = cts.Token;

            // Reader task
            var readerTask = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    var lookup = server.UserLookup;
                    // It must be either set A (alice & bob) or set B (charlie & david)
                    var isA = lookup.ContainsKey("alice") && lookup.ContainsKey("bob") && lookup.Count == 2;
                    var isB = lookup.ContainsKey("charlie") && lookup.ContainsKey("david") && lookup.Count == 2;
                    Assert.True(isA || isB, "Observed incomplete or mixed dictionary.");
                }
            });

            // Writer loop replacing lookups
            var writerTask = Task.Run(async () =>
            {
                var toggle = false;
                while (!token.IsCancellationRequested)
                {
                    server.ReplaceUserLookup(toggle ? usersA : usersB);
                    toggle = !toggle;
                    await Task.Yield();
                }
            });

            await Task.WhenAll(readerTask, writerTask);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void WatcherDisposeIsIdempotent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempUsers = Path.Combine(tempDir, "users.json");
        var tempState = Path.Combine(tempDir, "state.json");
        try
        {
            var initialUsers = new UsersFile();
            var config = TextCascade.Server.Config.CreateDefaultConfig() with { TokenSecret = new byte[32] };
            var server = new SyncServer(
                config,
                initialUsers,
                new RuntimeStateStore(tempState),
                new Argon2PasswordHasher(),
                new SystemClock(),
                NullLogger<SyncServer>.Instance);

            var watcher = new UserFileWatcher(tempUsers, server, NullLogger.Instance);
            watcher.Start();
            watcher.Dispose();
            watcher.Dispose(); // Should not throw
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
