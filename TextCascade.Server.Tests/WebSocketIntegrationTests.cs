using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class WebSocketIntegrationTests
{
    private const string ValidHash = "$argon2id$v=19$m=19456,t=2,p=1$c2FsdA$aG9zdA";

    private sealed class TestLogCollector : ILoggerProvider, ILogger
    {
        public List<string> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add(formatter(state, exception));
            }
        }

        public void Dispose() { }
    }

    private sealed class FastPasswordHasher : IPasswordHasher
    {
        public string Hash(string password, Isopoh.Cryptography.Argon2.Argon2Config config) => ValidHash;

        public bool Verify(string password, string encodedHash)
        {
            return (password == "password123" && encodedHash == ValidHash);
        }

        public bool NeedsRehash(string encodedHash, Isopoh.Cryptography.Argon2.Argon2Config config) => false;
    }

    private sealed class IntegrationTestFixture : IAsyncDisposable
    {
        public WebApplication Application { get; }
        public HttpClient Client { get; }
        public string BaseUrl { get; }
        public string WebSocketUrl { get; }
        public string TempDir { get; }
        public RuntimeConfig Config { get; }
        public TestLogCollector Logs { get; } = new();

        public static async Task<IntegrationTestFixture> CreateAsync(
            Func<RuntimeConfig, RuntimeConfig>? configModifier = null,
            Action<UsersFile>? usersOverride = null,
            Action<RuntimeStateStore>? stateOverride = null)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var usersPath = Path.Combine(tempDir, "users.json");
            var statePath = Path.Combine(tempDir, "state.json");

            var users = new UsersFile
            {
                Users =
                [
                    new UserRecord("alice", ValidHash, 1),
                    new UserRecord("bob", ValidHash, 1),
                ],
                NextTokenVersion = 2,
            };
            usersOverride?.Invoke(users);
            UsersFile.SaveUsers(usersPath, users);

            var stateStore = new RuntimeStateStore(statePath);
            stateOverride?.Invoke(stateStore);

            var config = TextCascade.Server.Config.CreateDefaultConfig() with
            {
                TokenSecret = Encoding.UTF8.GetBytes("12345678901234567890123456789012"),
                Files = new FilesConfig(usersPath, statePath),
                Server = new ServerConfig("127.0.0.1", 0, "dummy.pem"),
                Limits = TextCascade.Server.Config.CreateDefaultConfig().Limits with { SnapshotWindowSeconds = 0 },
            };
            if (configModifier is not null)
            {
                config = configModifier(config);
            }

            var app = ServerHost.CreateApp(
                [],
                config,
                users,
                stateStore,
                hasher: new FastPasswordHasher(),
                clock: new SystemClock(),
                certificate: null);

            var logs = new TestLogCollector();
            app.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);

            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();

            var boundUrl = app.Urls.First();
            var uri = new Uri(boundUrl);
            var wsUrl = $"ws://{uri.Authority}/api/v1/sync";

            var client = new HttpClient { BaseAddress = uri };

            return new IntegrationTestFixture(app, client, boundUrl, wsUrl, tempDir, config, logs);
        }

        private IntegrationTestFixture(
            WebApplication app,
            HttpClient client,
            string baseUrl,
            string wsUrl,
            string tempDir,
            RuntimeConfig config,
            TestLogCollector logs)
        {
            Application = app;
            Client = client;
            BaseUrl = baseUrl;
            WebSocketUrl = wsUrl;
            TempDir = tempDir;
            Config = config;
            Logs = logs;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                await Application.StopAsync(cts.Token);
            }
            catch { }

            await Application.DisposeAsync();

            if (Directory.Exists(TempDir))
            {
                try { Directory.Delete(TempDir, true); } catch { }
            }
        }
    }

    private static async Task<string> LoginAsync(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/login", new { username, password });
        Assert.True(response.IsSuccessStatusCode, $"Login failed: {response.StatusCode}");

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("protocolVersion").GetInt32());
        var token = doc.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));
        return token!;
    }

    private static async Task<ClientWebSocket> ConnectWebSocketAsync(string wsUrl, string token)
    {
        var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("textcascade.v1");
        ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ws.ConnectAsync(new Uri(wsUrl), cts.Token);
        Assert.Equal(WebSocketState.Open, ws.State);
        Assert.Equal("textcascade.v1", ws.SubProtocol);
        return ws;
    }

    private static async Task SendJsonAsync(ClientWebSocket ws, object msg)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count));
    }

    private static async Task CloseWsAsync(ClientWebSocket? ws)
    {
        if (ws is not null && ws.State == WebSocketState.Open)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
            }
            catch { }
        }
        ws?.Dispose();
    }

    [Fact]
    public async Task LoginAndWebSocketHandshakeRoundTrips()
    {
        await using var fixture = await IntegrationTestFixture.CreateAsync();

        var token = await LoginAsync(fixture.Client, "alice", "password123");
        var ws = await ConnectWebSocketAsync(fixture.WebSocketUrl, token);
        try
        {
            // Send hello
            await SendJsonAsync(ws, new
            {
                type = "hello",
                clientId = "client-1",
                clientName = "Device 1",
                lastServerVersion = 0,
                snapshot = (object?)null,
            });

            // Receive welcome
            using var welcomeDoc = await ReceiveJsonAsync(ws);
            var root = welcomeDoc.RootElement;
            Assert.Equal("welcome", root.GetProperty("type").GetString());
            Assert.Equal(1, root.GetProperty("protocolVersion").GetInt32());
            Assert.False(root.TryGetProperty("latest", out var latest) && latest.ValueKind == JsonValueKind.Object);
        }
        finally
        {
            await CloseWsAsync(ws);
        }
    }

    [Fact]
    public async Task ClipBroadcastsToSecondClient()
    {
        await using var fixture = await IntegrationTestFixture.CreateAsync();

        var tokenA = await LoginAsync(fixture.Client, "alice", "password123");
        var tokenB = await LoginAsync(fixture.Client, "alice", "password123");

        var wsA = await ConnectWebSocketAsync(fixture.WebSocketUrl, tokenA);
        var wsB = await ConnectWebSocketAsync(fixture.WebSocketUrl, tokenB);
        try
        {
            // Hello from A
            await SendJsonAsync(wsA, new
            {
                type = "hello",
                clientId = "client-A",
                clientName = "Device A",
                lastServerVersion = 0,
                snapshot = (object?)null,
            });
            using var welcomeA = await ReceiveJsonAsync(wsA);

            // Hello from B
            await SendJsonAsync(wsB, new
            {
                type = "hello",
                clientId = "client-B",
                clientName = "Device B",
                lastServerVersion = 0,
                snapshot = (object?)null,
            });
            using var welcomeB = await ReceiveJsonAsync(wsB);

            // A sends clip
            await SendJsonAsync(wsA, new
            {
                type = "clip",
                id = "clip-msg-1",
                payload = "Hello World Broadcast",
                encrypted = false,
                hash = "h1",
            });

            // A receives clip_ack
            using var ackA = await ReceiveJsonAsync(wsA);
            Assert.Equal("clip_ack", ackA.RootElement.GetProperty("type").GetString());
            Assert.Equal("clip-msg-1", ackA.RootElement.GetProperty("id").GetString());
            Assert.Equal(1UL, ackA.RootElement.GetProperty("version").GetUInt64());

            // B receives broadcast clip
            using var clipB = await ReceiveJsonAsync(wsB);
            Assert.Equal("clip", clipB.RootElement.GetProperty("type").GetString());
            Assert.Equal("clip-msg-1", clipB.RootElement.GetProperty("id").GetString());
            Assert.Equal("Hello World Broadcast", clipB.RootElement.GetProperty("payload").GetString());
            Assert.Equal(1UL, clipB.RootElement.GetProperty("version").GetUInt64());

            // B sends same clip (id & payload duplicate)
            await SendJsonAsync(wsB, new
            {
                type = "clip",
                id = "clip-msg-1",
                payload = "Hello World Broadcast",
                encrypted = false,
                hash = "h1",
            });

            // B receives duplicate ack with same version
            using var ackB = await ReceiveJsonAsync(wsB);
            Assert.Equal("clip_ack", ackB.RootElement.GetProperty("type").GetString());
            Assert.Equal("clip-msg-1", ackB.RootElement.GetProperty("id").GetString());
            Assert.Equal(1UL, ackB.RootElement.GetProperty("version").GetUInt64());
        }
        finally
        {
            await CloseWsAsync(wsA);
            await CloseWsAsync(wsB);
        }
    }

    [Fact]
    public async Task InvalidTokenDoesNotUpgradeWebSocket()
    {
        await using var fixture = await IntegrationTestFixture.CreateAsync();

        var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("textcascade.v1");
        ws.Options.SetRequestHeader("Authorization", "Bearer invalid-signature-token-here");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<WebSocketException>(() => ws.ConnectAsync(new Uri(fixture.WebSocketUrl), cts.Token));
    }

    [Fact]
    public async Task ReconnectRestoresHighestSnapshot()
    {
        await using var fixture = await IntegrationTestFixture.CreateAsync(
            configModifier: cfg => cfg with { Limits = cfg.Limits with { SnapshotWindowSeconds = 10 } },
            stateOverride: store =>
            {
                store.SaveVersion("alice", 7UL);
            });

        var token = await LoginAsync(fixture.Client, "alice", "password123");

        var wsA = await ConnectWebSocketAsync(fixture.WebSocketUrl, token);
        var wsB = await ConnectWebSocketAsync(fixture.WebSocketUrl, token);
        try
        {
            var modifiedTime = DateTimeOffset.UtcNow;

            // A sends hello with version 7
            await SendJsonAsync(wsA, new
            {
                type = "hello",
                clientId = "client-A",
                clientName = "Device A",
                lastServerVersion = 7,
                snapshot = new
                {
                    payload = "snapshot-v7",
                    encrypted = false,
                    hash = "hash7",
                    localModifiedAtUtc = modifiedTime.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
                },
            });

            // B sends hello with version 8
            await SendJsonAsync(wsB, new
            {
                type = "hello",
                clientId = "client-B",
                clientName = "Device B",
                lastServerVersion = 8,
                snapshot = new
                {
                    payload = "snapshot-v8",
                    encrypted = false,
                    hash = "hash8",
                    localModifiedAtUtc = modifiedTime.AddSeconds(1).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
                },
            });

            // Wait a moment for jobs to be processed in user channel
            await Task.Delay(50);

            // Explicitly close recovery window to trigger immediate election and broadcast
            var syncServer = fixture.Application.Services.GetRequiredService<SyncServer>();
            var hub = syncServer.GetOrCreateHub("alice", fixture.Config);
            hub.CloseRecoveryWindow(DateTimeOffset.UtcNow.AddMinutes(1));

            // Both receives welcome with winning version 8
            using var welcomeA = await ReceiveJsonAsync(wsA);
            using var welcomeB = await ReceiveJsonAsync(wsB);

            Assert.Equal("welcome", welcomeA.RootElement.GetProperty("type").GetString());
            var latestA = welcomeA.RootElement.GetProperty("latest");
            Assert.Equal(8UL, latestA.GetProperty("version").GetUInt64());
            Assert.Equal("snapshot-v8", latestA.GetProperty("payload").GetString());

            Assert.Equal("welcome", welcomeB.RootElement.GetProperty("type").GetString());
            var latestB = welcomeB.RootElement.GetProperty("latest");
            Assert.Equal(8UL, latestB.GetProperty("version").GetUInt64());
            Assert.Equal("snapshot-v8", latestB.GetProperty("payload").GetString());
        }
        finally
        {
            await CloseWsAsync(wsA);
            await CloseWsAsync(wsB);
        }
    }

    [Fact]
    public async Task AbruptDisconnectIsLoggedAndServerContinues()
    {
        await using var fixture = await IntegrationTestFixture.CreateAsync();

        var token = await LoginAsync(fixture.Client, "alice", "password123");

        var wsA = await ConnectWebSocketAsync(fixture.WebSocketUrl, token);
        var wsB = await ConnectWebSocketAsync(fixture.WebSocketUrl, token);
        try
        {
            // Hello from A
            await SendJsonAsync(wsA, new
            {
                type = "hello",
                clientId = "client-A",
                clientName = "Device A",
                lastServerVersion = 0,
                snapshot = (object?)null,
            });
            using var welcomeA = await ReceiveJsonAsync(wsA);

            // Hello from B
            await SendJsonAsync(wsB, new
            {
                type = "hello",
                clientId = "client-B",
                clientName = "Device B",
                lastServerVersion = 0,
                snapshot = (object?)null,
            });
            using var welcomeB = await ReceiveJsonAsync(wsB);

            // Abruptly abort client A socket
            wsA.Abort();
            wsA.Dispose();

            // B sends clip - server continues properly
            await SendJsonAsync(wsB, new
            {
                type = "clip",
                id = "clip-after-abort",
                payload = "Still works",
                encrypted = false,
                hash = "h2",
            });

            using var ackB = await ReceiveJsonAsync(wsB);
            Assert.Equal("clip_ack", ackB.RootElement.GetProperty("type").GetString());
            Assert.Equal("clip-after-abort", ackB.RootElement.GetProperty("id").GetString());

            // Check structured logs for connect/disconnect
            lock (fixture.Logs.Entries)
            {
                Assert.NotEmpty(fixture.Logs.Entries);
                foreach (var log in fixture.Logs.Entries)
                {
                    Assert.DoesNotContain("password123", log, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("12345678901234567890123456789012", log, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        finally
        {
            await CloseWsAsync(wsB);
        }
    }
}


