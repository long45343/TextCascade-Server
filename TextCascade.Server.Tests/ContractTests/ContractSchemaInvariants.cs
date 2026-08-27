using System.Text;
using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class ContractSchemaInvariants
{
    [Fact]
    public void C1_Welcome_NoLatest_OmitsKey()
    {
        var bytes = Protocol.SerializeWelcome(null, TextCascade.Server.Config.CreateDefaultConfig().Limits);
        var json = Encoding.UTF8.GetString(bytes);

        Assert.Equal("""{"type":"welcome","protocolVersion":1}""", json);
        Assert.DoesNotContain("latest", json, StringComparison.Ordinal);
    }

    [Fact]
    public void C2_Welcome_WithLatest_FixedFieldOrder()
    {
        var latest = new LatestText(
            "payload-text", 128, "hash", true, "android-a", "android",
            new DateTimeOffset(2026, 8, 18, 7, 59, 58, TimeSpan.Zero));

        var bytes = Protocol.SerializeWelcome(latest, TextCascade.Server.Config.CreateDefaultConfig().Limits);
        var json = Encoding.UTF8.GetString(bytes);

        Assert.Equal(
            """{"type":"welcome","protocolVersion":1,"latest":{"payload":"payload-text","version":128,"hash":"hash","encrypted":true,"fromClientId":"android-a","fromClientName":"android","updatedAtUtc":"2026-08-18T07:59:58Z"}}""",
            json);
    }

    [Fact]
    public void C3_BroadcastClip_ContainsAllEightFields()
    {
        var latest = new LatestText(
            "payload-text", 129, "hash", false, "windows-a", "Windows Desktop",
            new DateTimeOffset(2026, 8, 18, 8, 1, 0, TimeSpan.Zero));

        var bytes = Protocol.SerializeClip("clip-id-1", latest);
        var json = Encoding.UTF8.GetString(bytes);

        Assert.Equal(
            """{"type":"clip","version":129,"id":"clip-id-1","payload":"payload-text","encrypted":false,"hash":"hash","fromClientId":"windows-a","fromClientName":"Windows Desktop","updatedAtUtc":"2026-08-18T08:01:00Z"}""",
            json);
    }

    [Fact]
    public void C4_TokenPayload_MinimalFixedOrder()
    {
        var payload = new TokenPayload("alice", 1, 1760000000, 1762592000);
        var compact = TokenService.SignToken(payload, new byte[32]);
        var payloadSegment = compact.Split('.')[0];
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(payloadSegment));

        Assert.Equal("""{"sub":"alice","ver":1,"iat":1760000000,"exp":1762592000}""", payloadJson);
    }

    [Fact]
    public void C5_ErrorResponse_IncludesReferenceId_WhenNotNull()
    {
        var withReference = Encoding.UTF8.GetString(Protocol.SerializeProtocolError(
            new ProtocolError(ProtocolErrorCode.TextTooLarge, "Text exceeds maxTextBytes.", "clip-1")));
        Assert.Equal(
            """{"type":"error","code":"text_too_large","message":"Text exceeds maxTextBytes.","referenceId":"clip-1"}""",
            withReference);

        var withoutReference = Encoding.UTF8.GetString(Protocol.SerializeProtocolError(
            new ProtocolError(ProtocolErrorCode.InvalidMessage, "Invalid JSON.", null)));
        Assert.Equal(
            """{"type":"error","code":"invalid_message","message":"Invalid JSON."}""",
            withoutReference);
    }

    [Fact]
    public void C6_Ping_Timestamps_UtcZ_SecondPrecision()
    {
        var now = new DateTimeOffset(2026, 8, 18, 8, 2, 0, TimeSpan.Zero).AddMilliseconds(456);
        var json = Encoding.UTF8.GetString(Protocol.SerializePing(now));

        Assert.Equal("""{"type":"ping","serverTimeUtc":"2026-08-18T08:02:00Z"}""", json);
    }

    [Fact]
    public void ClipAck_Shape_MatchesContract()
    {
        var latest = new LatestText(
            "payload", 129, "hash", false, "windows-a", "Windows Desktop",
            new DateTimeOffset(2026, 8, 18, 8, 1, 0, TimeSpan.Zero));

        var json = Encoding.UTF8.GetString(Protocol.SerializeClipAck("clip-id-1", latest));

        Assert.Equal(
            """{"type":"clip_ack","id":"clip-id-1","version":129,"updatedAtUtc":"2026-08-18T08:01:00Z"}""",
            json);
    }

    [Fact]
    public void Bye_Shape_MatchesContract()
    {
        var json = Encoding.UTF8.GetString(Protocol.SerializeBye("server_shutdown"));

        Assert.Equal("""{"type":"bye","reason":"server_shutdown"}""", json);
    }

    private static byte[] Base64UrlDecode(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        var remainder = padded.Length % 4;
        if (remainder > 0) { padded += new string('=', 4 - remainder); }
        return Convert.FromBase64String(padded);
    }
}