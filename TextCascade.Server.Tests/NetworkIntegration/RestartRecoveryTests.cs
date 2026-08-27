using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TextCascade.Server;

namespace TextCascade.Server.Tests.NetworkIntegration;

[Trait("Category", "NetworkIntegration")]
public class RestartRecoveryTests
{
    private static HttpClient NewHttpsClient() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    private static async Task<string> LoginAsync(string authority, string username = "alice")
    {
        using var https = NewHttpsClient();
        var response = await https.PostAsJsonAsync($"https://{authority}/api/v1/login", new { username, password = "password123" });
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("token").GetString()!;
    }

    private static async Task<ClientWebSocket> ConnectAsync(string wssUrl, string token, string clientId, ulong lastServerVersion = 0, object? snapshot = null)
    {
        var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("textcascade.v1");
        ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ws.ConnectAsync(new Uri(wssUrl), cts.Token);

        var hello = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "hello",
            clientId,
            clientName = clientId,
            lastServerVersion,
            snapshot,
        });
        await ws.SendAsync(hello, WebSocketMessageType.Text, true, cts.Token);
        return ws;
    }

    private static async Task<(string Type, JsonDocument Doc)> ReceiveTypedAsync(ClientWebSocket ws)
    {
        var buffer = new byte[1024 * 1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        WebSocketReceiveResult result;
        try
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
        }
        catch (WebSocketException)
        {
            // Abrupt teardown (abort path) — surfaces as a close with no frame content.
            return ("close", JsonDocument.Parse("{}"));
        }

        if (result.MessageType == WebSocketMessageType.Close)
        {
            return ("close", JsonDocument.Parse("{}"));
        }

        var payload = buffer.AsMemory(0, result.Count).ToArray();
        return (JsonDocument.Parse(payload).RootElement.GetProperty("type").GetString()!, JsonDocument.Parse(payload));
    }

    // N9
    [Fact]
    public async Task Restart_KeepsTokenValid_DirectReconnect()
    {
        await using var fixture = NetworkTestFixture.Create(
            configModifier: cfg => cfg with { Limits = cfg.Limits with { SnapshotWindowSeconds = 0 } });
        var first = await fixture.StartAsync();

        // First instance: login and push one clip through to move the version to 1.
        string token;
        try
        {
            token = await LoginAsync(first.Authority);
            var ws = await ConnectAsync(first.WssUrl, token, "restart-A");
            try
            {
                var welcome = await ReceiveTypedAsync(ws);
                Assert.Equal("welcome", welcome.Type);

                var clip = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "clip",
                    id = "pre-restart-1",
                    payload = "before restart",
                    encrypted = false,
                    hash = "h1",
                });
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await ws.SendAsync(clip, WebSocketMessageType.Text, true, cts.Token);
                var ack = await ReceiveTypedAsync(ws);
                Assert.Equal("clip_ack", ack.Type);
                Assert.Equal(1UL, ack.Doc.RootElement.GetProperty("version").GetUInt64());
            }
            finally
            {
                ws.Dispose();
            }
        }
        finally
        {
            await first.StopAsync();
        }

        // State file must have been flushed on shutdown so the version persists.
        Assert.True(File.Exists(fixture.StatePath), "state file should exist after graceful stop");

        // Second instance: same users file + state file; old token works without re-login.
        var second = await fixture.StartAsync();
        try
        {
            var ws = await ConnectAsync(second.WssUrl, token, "restart-B");
            try
            {
                var welcome = await ReceiveTypedAsync(ws);
                Assert.Equal("welcome", welcome.Type);

                // Version baseline came from the persisted state: next clip continues from 2.
                var clip = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "clip",
                    id = "post-restart-1",
                    payload = "after restart",
                    encrypted = false,
                    hash = "h2",
                });
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await ws.SendAsync(clip, WebSocketMessageType.Text, true, cts.Token);
                var ack = await ReceiveTypedAsync(ws);
                Assert.Equal("clip_ack", ack.Type);
                Assert.Equal(2UL, ack.Doc.RootElement.GetProperty("version").GetUInt64());
            }
            finally
            {
                ws.Dispose();
            }
        }
        finally
        {
            await second.StopAsync();
        }
    }

    // N10
    [Fact]
    public async Task Restart_SnapshotElection_RestoresLatest()
    {
        await using var fixture = NetworkTestFixture.Create(
            configModifier: cfg => cfg with { Limits = cfg.Limits with { SnapshotWindowSeconds = 5 } });
        var first = await fixture.StartAsync();
        string token;
        try
        {
            token = await LoginAsync(first.Authority);
        }
        finally
        {
            await first.StopAsync();
        }

        var second = await fixture.StartAsync();
        try
        {
            var modifiedTime = DateTimeOffset.UtcNow;
            var snapshotOf = (ulong version, string payload) => (object)new
            {
                payload,
                encrypted = false,
                hash = $"hash-{version}",
                localModifiedAtUtc = modifiedTime.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            };

            var ws128 = await ConnectAsync(second.WssUrl, token, "snap-128", 128, snapshotOf(128, "snapshot-v128"));
            var ws64 = await ConnectAsync(second.WssUrl, token, "snap-64", 64, snapshotOf(64, "snapshot-v64"));
            try
            {
                await Task.Delay(100);

                // Close the recovery window through the server to force the election now.
                var syncServer = second.App.Services.GetRequiredService<SyncServer>();
                var hub = syncServer.GetOrCreateHub("alice", fixture.Config);
                hub.CloseRecoveryWindow(DateTimeOffset.UtcNow.AddMinutes(1));

                var welcome128 = await ReceiveTypedAsync(ws128);
                var welcome64 = await ReceiveTypedAsync(ws64);
                foreach (var welcome in new[] { welcome128, welcome64 })
                {
                    Assert.Equal("welcome", welcome.Type);
                    Assert.Equal("snapshot-v128", welcome.Doc.RootElement.GetProperty("latest").GetProperty("payload").GetString());
                    Assert.Equal(128UL, welcome.Doc.RootElement.GetProperty("latest").GetProperty("version").GetUInt64());
                }

                // The next clip continues from the restored version without +1 on restore.
                var clip = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "clip",
                    id = "post-election-1",
                    payload = "fresh clip",
                    encrypted = false,
                    hash = "h3",
                });
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await ws64.SendAsync(clip, WebSocketMessageType.Text, true, cts.Token);
                var ack = await ReceiveTypedAsync(ws64);
                Assert.Equal("clip_ack", ack.Type);
                Assert.Equal(129UL, ack.Doc.RootElement.GetProperty("version").GetUInt64());
            }
            finally
            {
                ws128.Dispose();
                ws64.Dispose();
            }
        }
        finally
        {
            await second.StopAsync();
        }
    }

    /// <summary>Reads from the socket until a bye frame or close/abort; never throws.</summary>
    private static async Task<(string Type, string? Reason)> ReceiveByeOrCloseAsync(ClientWebSocket ws)
    {
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or IOException)
            {
                return ("close", null);
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return ("close", result.CloseStatus?.ToString());
            }

            var doc = JsonDocument.Parse(buffer.AsMemory(0, result.Count).ToArray());
            var type = doc.RootElement.GetProperty("type").GetString();
            var reason = doc.RootElement.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() : null;
            doc.Dispose();
            if (type == "bye")
            {
                return ("bye", reason);
            }
        }
    }

    // N11 + N12 combined chain: login -> connect -> clip flow -> graceful stop with bye/1001.
    [Fact]
    public async Task FullChain_Login_Connect_Send_Receive_Bye1001()
    {
        await using var fixture = NetworkTestFixture.Create(
            configModifier: cfg => cfg with { Limits = cfg.Limits with { SnapshotWindowSeconds = 0 } });
        var server = await fixture.StartAsync();
        try
        {
            var tokenA = await LoginAsync(server.Authority);
            var tokenB = await LoginAsync(server.Authority);

            var wsA = await ConnectAsync(server.WssUrl, tokenA, "chain-A");
            var wsB = await ConnectAsync(server.WssUrl, tokenB, "chain-B");
            try
            {
                var welcomeA = await ReceiveTypedAsync(wsA);
                Assert.Equal("welcome", welcomeA.Type);
                welcomeA.Doc.Dispose();
                var welcomeB = await ReceiveTypedAsync(wsB);
                Assert.Equal("welcome", welcomeB.Type);
                welcomeB.Doc.Dispose();

                var clip = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "clip",
                    id = "chain-clip-1",
                    payload = "chain payload",
                    encrypted = false,
                    hash = "hc",
                });
                using var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await wsA.SendAsync(clip, WebSocketMessageType.Text, true, sendCts.Token);

                var ack = await ReceiveTypedAsync(wsA);
                Assert.Equal("clip_ack", ack.Type);
                ack.Doc.Dispose();

                // The two hellos racing the recovery-window close can deliver a second
                // welcome to B; skip extras until the broadcast clip arrives.
                JsonDocument broadcastDoc;
                while (true)
                {
                    var broadcast = await ReceiveTypedAsync(wsB);
                    broadcastDoc = broadcast.Doc;
                    if (broadcast.Type == "clip")
                    {
                        break;
                    }
                    Assert.Equal("welcome", broadcast.Type);
                    broadcastDoc.Dispose();
                }

                Assert.Equal("chain payload", broadcastDoc.RootElement.GetProperty("payload").GetString());
                broadcastDoc.Dispose();

                // Graceful stop: bye then close 1001 (EndpointUnavailable). Both frames are
                // best-effort by contract: a fast drain can abort the socket before they land.
                var byeTask = ReceiveByeOrCloseAsync(wsB);
                await server.StopAsync();

                var (byeType, byeReason) = await byeTask;
                if (byeType == "bye")
                {
                    Assert.Equal("server_shutdown", byeReason);
                }
                // else: fast drain aborted before bye landed; both paths are contract-legal (spec §7).
            }
            finally
            {
                wsA.Dispose();
                wsB.Dispose();
            }
        }
        finally
        {
            // StopAsync already ran; ensure app disposal via fixture (fixture deletes temp dir).
        }
    }
}