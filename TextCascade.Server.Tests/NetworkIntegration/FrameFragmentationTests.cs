using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TextCascade.Server;

namespace TextCascade.Server.Tests.NetworkIntegration;

[Trait("Category", "NetworkIntegration")]
public class FrameFragmentationTests
{
    private static async Task<ClientWebSocket> ConnectAndHelloAsync(string wssUrl, string token, string clientId)
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
            lastServerVersion = 0,
            snapshot = (object?)null,
        });
        await ws.SendAsync(hello, WebSocketMessageType.Text, true, cts.Token);
        await ReceiveOneAsync(ws); // welcome
        return ws;
    }

    private static async Task SendFragmentedAsync(ClientWebSocket ws, byte[][] fragments)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var index = 0; index < fragments.Length; index++)
        {
            var endOfMessage = index == fragments.Length - 1;
            await ws.SendAsync(fragments[index], WebSocketMessageType.Text, endOfMessage, cts.Token);
        }
    }

    private static async Task<byte[]> ReceiveOneAsync(ClientWebSocket ws)
    {
        var buffer = new byte[1024 * 1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            return [];
        }

        return buffer.AsMemory(0, result.Count).ToArray();
    }

    private static async Task<(string Type, byte[] Payload)> ReceiveMessageAsync(ClientWebSocket ws)
    {
        // Frames may arrive fragmented from the server as well; loop until a full message.
        using var message = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return ("close", []);
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                var payload = message.ToArray();
                return (JsonDocument.Parse(payload).RootElement.GetProperty("type").GetString()!, payload);
            }
        }
    }

    private static async Task CloseAsync(ClientWebSocket ws)
    {
        if (ws.State == WebSocketState.Open)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
            }
            catch { }
        }

        ws.Dispose();
    }

    // N6
    [Fact]
    public async Task FragmentedClip_Reassembles_AndBroadcasts()
    {
        await using var fixture = NetworkTestFixture.Create();
        var server = await fixture.StartAsync();
        try
        {
            var (tokenA, tokenB) = await LoginTwoAsync(server.Authority);
            var wsA = await ConnectAndHelloAsync(server.WssUrl, tokenA, "frag-A");
            var wsB = await ConnectAndHelloAsync(server.WssUrl, tokenB, "frag-B");
            try
            {
                // ~300KB payload split into three fragments below single-MSS scale boundaries.
                var payload = new string('x', 300_000);
                var clip = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "clip",
                    id = "frag-clip-1",
                    payload,
                    encrypted = false,
                    hash = "frag-hash",
                });
                var fragments = new[]
                {
                    clip.AsMemory(0, 100_000).ToArray(),
                    clip.AsMemory(100_000, 100_000).ToArray(),
                    clip.AsMemory(200_000).ToArray(),
                };

                await SendFragmentedAsync(wsA, fragments);

                var ack = await ReceiveMessageAsync(wsA);
                Assert.Equal("clip_ack", ack.Type);

                var broadcast = await ReceiveMessageAsync(wsB);
                Assert.Equal("clip", broadcast.Type);
                using var doc = JsonDocument.Parse(broadcast.Payload);
                Assert.Equal(payload, doc.RootElement.GetProperty("payload").GetString());
            }
            finally
            {
                await CloseAsync(wsA);
                await CloseAsync(wsB);
            }
        }
        finally
        {
            await server.StopAsync();
        }
    }

    // N7
    [Fact]
    public async Task OversizeFrame_Closes1009()
    {
        await using var fixture = NetworkTestFixture.Create();
        var server = await fixture.StartAsync();
        try
        {
            var token = await LoginAsync(server.Authority);
            var ws = await ConnectAndHelloAsync(server.WssUrl, token, "oversize-A");
            try
            {
                // Total frame exceeds max_frame_bytes (589824) via three fragments.
                var oversize = new string('y', 600_000);
                var clip = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "clip",
                    id = "oversize-1",
                    payload = oversize,
                    encrypted = false,
                    hash = "h",
                });
                await SendFragmentedAsync(ws, [clip.AsMemory(0, 300_000).ToArray(), clip.AsMemory(300_000).ToArray()]);

                // Server sends the frame_too_large error then closes with 1009 (MessageTooBig).
                var closeSeen = false;
                for (var attempt = 0; attempt < 2 && !closeSeen; attempt++)
                {
                    var buffer = new byte[64 * 1024];
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        closeSeen = true;
                        Assert.Equal(WebSocketCloseStatus.MessageTooBig, result.CloseStatus);
                    }
                }

                Assert.True(closeSeen, "server should close the connection with 1009 after an oversize frame");
            }
            finally
            {
                ws.Dispose();
            }
        }
        finally
        {
            await server.StopAsync();
        }
    }

    // N8
    [Fact]
    public async Task ZeroLengthFrame_TreatedAsFrameTooLarge()
    {
        await using var fixture = NetworkTestFixture.Create();
        var server = await fixture.StartAsync();
        try
        {
            var token = await LoginAsync(server.Authority);
            var ws = await ConnectAndHelloAsync(server.WssUrl, token, "zerolen-A");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await ws.SendAsync(Array.Empty<byte>(), WebSocketMessageType.Text, endOfMessage: true, cts.Token);

                var closeSeen = false;
                for (var attempt = 0; attempt < 2 && !closeSeen; attempt++)
                {
                    var buffer = new byte[64 * 1024];
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        closeSeen = true;
                        // Current implementation classifies empty frames as frame_too_large -> 1009.
                        Assert.Equal(WebSocketCloseStatus.MessageTooBig, result.CloseStatus);
                    }
                }

                Assert.True(closeSeen, "zero-length frame should terminate the connection");
            }
            finally
            {
                ws.Dispose();
            }
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static async Task<string> LoginAsync(string authority)
    {
        using var https = NewHttpsClient();
        var response = await https.PostAsJsonAsync($"https://{authority}/api/v1/login", new { username = "alice", password = "password123" });
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("token").GetString()!;
    }

    private static async Task<(string, string)> LoginTwoAsync(string authority) => (await LoginAsync(authority), await LoginAsync(authority));

    private static HttpClient NewHttpsClient() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });
}