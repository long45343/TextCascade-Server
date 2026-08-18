using System.Text;
using System.Text.Json;
using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class ProtocolSerializationTests
{
    [Fact]
    public void WelcomeLatestTimeUsesUtcSecondFormat()
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1760000000123).ToUniversalTime();
        var latest = new LatestText("payload", 7, "hash", true, "client", "name", timestamp);

        var json = Encoding.UTF8.GetString(Protocol.SerializeWelcome(latest));

        using var document = JsonDocument.Parse(json);
        var actual = document.RootElement.GetProperty("latest").GetProperty("updatedAtUtc").GetString();
        Assert.Equal("2025-10-09T08:53:20Z", actual);
    }

    [Fact]
    public void ClipTimeUsesUtcSecondFormat()
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1760000000999).ToUniversalTime();
        var latest = new LatestText("payload", 7, "hash", true, "client", "name", timestamp);

        var json = Encoding.UTF8.GetString(Protocol.SerializeClip("clip-1", latest));

        using var document = JsonDocument.Parse(json);
        var actual = document.RootElement.GetProperty("updatedAtUtc").GetString();
        Assert.Equal("2025-10-09T08:53:20Z", actual);
    }

    [Fact]
    public void ClipAckTimeUsesUtcSecondFormat()
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1760000000500).ToUniversalTime();
        var latest = new LatestText("payload", 7, "hash", true, "client", "name", timestamp);

        var json = Encoding.UTF8.GetString(Protocol.SerializeClipAck("clip-1", latest));

        using var document = JsonDocument.Parse(json);
        var actual = document.RootElement.GetProperty("updatedAtUtc").GetString();
        Assert.Equal("2025-10-09T08:53:20Z", actual);
    }
}
