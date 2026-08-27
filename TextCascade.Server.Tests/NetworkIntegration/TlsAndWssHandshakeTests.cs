using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TextCascade.Server;

namespace TextCascade.Server.Tests.NetworkIntegration;

[Trait("Category", "NetworkIntegration")]
public class TlsAndWssHandshakeTests
{
    private static HttpClient NewHttpsClient()
    {
        return new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });
    }

    // .NET 10 removed ClientWebSocketOptions.SslProtocols: the negotiated TLS version follows
    // OS policy. Protocol-floor verification is done by direct SslStream probes (N2).
    private static async Task<ClientWebSocket> ConnectWssAsync(string wssUrl, string token)
    {
        var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("textcascade.v1");
        ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ws.ConnectAsync(new Uri(wssUrl), cts.Token);
        return ws;
    }

    private static async Task SendHelloAsync(ClientWebSocket ws, string clientId)
    {
        var hello = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "hello",
            clientId,
            clientName = clientId,
            lastServerVersion = 0,
            snapshot = (object?)null,
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ws.SendAsync(hello, WebSocketMessageType.Text, true, cts.Token);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count));
    }

    // N1
    [Fact]
    public async Task Connects_WithSelfSignedPfx_OverWss()
    {
        await using var fixture = NetworkTestFixture.Create();
        var server = await fixture.StartAsync();
        try
        {
            using var https = NewHttpsClient();
            var token = await LoginAsync(https, server.Authority);

            var ws = await ConnectWssAsync(server.WssUrl, token);
            try
            {
                Assert.Equal(WebSocketState.Open, ws.State);
                await SendHelloAsync(ws, "tls-client-1");
                using var welcome = await ReceiveJsonAsync(ws);
                Assert.Equal("welcome", welcome.RootElement.GetProperty("type").GetString());
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

    // N2 — the server accepts explicit TLS 1.2 and TLS 1.3 handshakes (SslStream probes;
    // ClientWebSocket can no longer pin a version on .NET 10).
    [Theory]
    [InlineData(SslProtocols.Tls12)]
    [InlineData(SslProtocols.Tls13)]
    public async Task ServerHandshakes_WithExplicitTlsVersion(SslProtocols protocolVersion)
    {
        await using var fixture = NetworkTestFixture.Create();
        var server = await fixture.StartAsync();
        try
        {
            using var tcp = new TcpClient();
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(IPAddress.Parse("127.0.0.1"), server.Port, connectCts.Token);

            await using var sslStream = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = protocolVersion,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }, handshakeCts.Token);

            Assert.True(sslStream.IsEncrypted);
            Assert.Equal(protocolVersion, sslStream.SslProtocol);
            // If a modern OS disables TLS 1.2 by policy the Tls12 inline case fails here;
            // that is an environment change, not a server regression.
        }
        finally
        {
            await server.StopAsync();
        }
    }

    // N3
    [Fact]
    public async Task HttpUpgrade_Succeeds_WithBearerAndSubProtocol()
    {
        await using var fixture = NetworkTestFixture.Create();
        var server = await fixture.StartAsync();
        try
        {
            using var https = NewHttpsClient();
            var token = await LoginAsync(https, server.Authority);

            var ws = await ConnectWssAsync(server.WssUrl, token);
            try
            {
                Assert.Equal("textcascade.v1", ws.SubProtocol);
                await SendHelloAsync(ws, "subproto-client");
                using var welcome = await ReceiveJsonAsync(ws);
                Assert.Equal(1, welcome.RootElement.GetProperty("protocolVersion").GetInt32());
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

    // N4
    [Fact]
    public async Task HttpsLogin_Endpoint_Works()
    {
        await using var fixture = NetworkTestFixture.Create();
        var server = await fixture.StartAsync();
        try
        {
            using var https = NewHttpsClient();
            var response = await https.PostAsJsonAsync($"https://{server.Authority}/api/v1/login", new { username = "alice", password = "password123" });
            Assert.True(response.IsSuccessStatusCode, $"Login failed: {response.StatusCode}");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            Assert.False(string.IsNullOrEmpty(root.GetProperty("token").GetString()));
            Assert.Equal(1, root.GetProperty("protocolVersion").GetInt32());
            Assert.True(root.TryGetProperty("expiresAtUtc", out _));
            Assert.True(root.TryGetProperty("maxTextBytes", out _));
            Assert.True(root.TryGetProperty("helloTimeoutSeconds", out _));
            Assert.True(root.TryGetProperty("heartbeatIntervalSeconds", out _));
            Assert.True(root.TryGetProperty("heartbeatTimeoutSeconds", out _));
        }
        finally
        {
            await server.StopAsync();
        }
    }

    // N5
    [Fact]
    public async Task RandomPortBinding_ActuallyBinds()
    {
        await using var fixture = NetworkTestFixture.Create();
        var server = await fixture.StartAsync();
        try
        {
            Assert.True(server.Port > 0, "Kestrel should bind an ephemeral port > 0");

            // The bound endpoint must accept TCP connections.
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(IPAddress.Parse("127.0.0.1"), server.Port, cts.Token);
            Assert.True(tcp.Connected);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static async Task<string> LoginAsync(HttpClient https, string authority)
    {
        var response = await https.PostAsJsonAsync($"https://{authority}/api/v1/login", new { username = "alice", password = "password123" });
        Assert.True(response.IsSuccessStatusCode, $"Login failed: {response.StatusCode}");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("token").GetString()!;
    }
}