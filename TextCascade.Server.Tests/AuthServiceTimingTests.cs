using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Isopoh.Cryptography.Argon2;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class AuthServiceTimingTests
{
    private sealed class RecordingHasher : IPasswordHasher
    {
        public List<(string Password, Argon2Config Config)> HashCalls { get; } = new();
        public List<(string Password, string EncodedHash)> VerifyCalls { get; } = new();

        public string DummyHashReturn { get; set; } = "$argon2id$v=19$m=19456,t=2,p=1$dummy";

        public string Hash(string password, Argon2Config config)
        {
            HashCalls.Add((password, config));
            return DummyHashReturn;
        }

        public bool Verify(string password, string encodedHash)
        {
            VerifyCalls.Add((password, encodedHash));
            return string.Equals(password, "correct-password", StringComparison.Ordinal)
                   && string.Equals(encodedHash, "valid-hash", StringComparison.Ordinal);
        }

        public bool NeedsRehash(string encodedHash, Argon2Config config) => false;
    }

    private sealed class TestLogger : ILogger
    {
        public List<string> LoggedMessages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            LoggedMessages.Add(formatter(state, exception));
        }
    }

    private static (DefaultHttpContext Context, MemoryStream ResponseBody) CreateHttpContext(string username, string password)
    {
        var context = new DefaultHttpContext();
        var json = JsonSerializer.Serialize(new { username, password });
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Request.ContentType = "application/json";

        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        return (context, responseBody);
    }

    [Fact]
    public async Task LoginVerificationRunsForMissingUser()
    {
        var hasher = new RecordingHasher();
        var config = TextCascade.Server.Config.CreateDefaultConfig() with { TokenSecret = new byte[32] };
        var users = new UsersFile
        {
            Users = [new UserRecord("alice", "valid-hash", 1)],
            NextTokenVersion = 2,
        };
        var tempState = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var stateStore = new RuntimeStateStore(tempState);
            var server = new SyncServer(config, users, stateStore, hasher, new SystemClock(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncServer>.Instance);

            Assert.Equal(server.LoginDummyHash, hasher.DummyHashReturn);

            // 1. Missing user
            var (missingContext, _) = CreateHttpContext("missing", "any-password");
            await AuthService.HandleLoginAsync(missingContext, config, server);
            Assert.Equal(401, missingContext.Response.StatusCode);
            Assert.Single(hasher.VerifyCalls);
            Assert.Equal("missing", "missing");
            Assert.Equal(server.LoginDummyHash, hasher.VerifyCalls[0].EncodedHash);
            Assert.Equal("any-password", hasher.VerifyCalls[0].Password);

            // 2. Existing user alice with wrong password
            var (aliceContext, _) = CreateHttpContext("alice", "wrong-password");
            await AuthService.HandleLoginAsync(aliceContext, config, server);
            Assert.Equal(401, aliceContext.Response.StatusCode);
            Assert.Equal(2, hasher.VerifyCalls.Count);
            Assert.Equal("valid-hash", hasher.VerifyCalls[1].EncodedHash);
            Assert.Equal("wrong-password", hasher.VerifyCalls[1].Password);
        }
        finally
        {
            if (File.Exists(tempState)) File.Delete(tempState);
        }
    }

    [Fact]
    public void LoginDummyHashUsesConfiguredArgon2Parameters()
    {
        var hasher = new RecordingHasher();
        var config = TextCascade.Server.Config.CreateDefaultConfig() with
        {
            TokenSecret = new byte[32],
            Auth = new AuthConfig(30, "TEST_SECRET", 32768, 4, 2),
        };
        var users = new UsersFile();
        var tempState = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var stateStore = new RuntimeStateStore(tempState);
            var server = new SyncServer(config, users, stateStore, hasher, new SystemClock(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncServer>.Instance);

            Assert.Single(hasher.HashCalls);
            var call = hasher.HashCalls[0];
            Assert.Equal("textcascade-login-timing-dummy", call.Password);
            Assert.Equal(32768, call.Config.MemoryCost);
            Assert.Equal(4, call.Config.TimeCost);
            Assert.Equal(2, call.Config.Threads);
            Assert.Equal(server.LoginDummyHash, hasher.DummyHashReturn);
        }
        finally
        {
            if (File.Exists(tempState)) File.Delete(tempState);
        }
    }

    [Fact]
    public async Task DisabledUserStillReturnsUnifiedInvalidCredentials()
    {
        var hasher = new RecordingHasher();
        var config = TextCascade.Server.Config.CreateDefaultConfig() with { TokenSecret = new byte[32] };
        var users = new UsersFile
        {
            Users = [new UserRecord("dave", "valid-hash", 1, Disabled: true)],
            NextTokenVersion = 2,
        };
        var tempState = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        var logger = new TestLogger();
        try
        {
            var stateStore = new RuntimeStateStore(tempState);
            var server = new SyncServer(config, users, stateStore, hasher, new SystemClock(), Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncServer>.Instance);

            var (context, responseBody) = CreateHttpContext("dave", "correct-password");
            await AuthService.HandleLoginAsync(context, config, server, logger);

            Assert.Equal(401, context.Response.StatusCode);
            var json = Encoding.UTF8.GetString(responseBody.ToArray());
            Assert.Contains("invalid_credentials", json);

            // Ensure logs do not mention "disabled"
            Assert.NotEmpty(logger.LoggedMessages);
            foreach (var log in logger.LoggedMessages)
            {
                Assert.DoesNotContain("reason=disabled", log, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("disabled", log, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            if (File.Exists(tempState)) File.Delete(tempState);
        }
    }
}


