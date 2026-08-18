using System.Text;
using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class ProtocolParseTests
{
    private static RuntimeConfig NewConfig() => TextCascade.Server.Config.CreateDefaultConfig();

    private static ParseResult Parse(string json) =>
        Protocol.ParseClientMessage(Encoding.UTF8.GetBytes(json), NewConfig());

    [Fact]
    public void ParsesHello()
    {
        var result = Parse("{\"type\":\"hello\",\"clientId\":\"a\",\"clientName\":\"n\",\"lastServerVersion\":5,\"snapshot\":{\"payload\":\"p\",\"encrypted\":true,\"hash\":\"h\",\"localModifiedAtUtc\":\"2026-08-18T08:00:00Z\"}}");

        Assert.True(result.IsSuccess);
        Assert.Equal(MessageKind.Hello, result.Kind);
        var hello = Assert.IsType<ClientHello>(result.Message);
        Assert.Equal("a", hello.ClientId);
        Assert.Equal(5UL, hello.LastServerVersion);
        Assert.NotNull(hello.Snapshot);
    }

    [Fact]
    public void ParsesClip()
    {
        var result = Parse("{\"type\":\"clip\",\"id\":\"id1\",\"payload\":\"p\",\"encrypted\":true,\"hash\":\"h\"}");

        Assert.True(result.IsSuccess);
        Assert.Equal(MessageKind.Clip, result.Kind);
        var clip = Assert.IsType<ClientClip>(result.Message);
        Assert.Equal("id1", clip.Id);
    }

    [Fact]
    public void ParsesPong()
    {
        var result = Parse("{\"type\":\"pong\",\"clientTimeUtc\":\"2026-08-18T08:02:00Z\"}");

        Assert.True(result.IsSuccess);
        Assert.Equal(MessageKind.Pong, result.Kind);
    }

    [Fact]
    public void RejectsUnknownType()
    {
        var result = Parse("{\"type\":\"bogus\"}");

        Assert.False(result.IsSuccess);
        Assert.Equal(ProtocolErrorCode.InvalidMessage, result.Error!.Code);
    }

    [Fact]
    public void RejectsMalformedJson()
    {
        Assert.False(Parse("{not json").IsSuccess);
    }

    [Fact]
    public void RejectsUnknownField()
    {
        var result = Parse("{\"type\":\"clip\",\"id\":\"id1\",\"payload\":\"p\",\"encrypted\":true,\"hash\":\"h\",\"extra\":1}");

        Assert.False(result.IsSuccess);
        Assert.Equal(ProtocolErrorCode.InvalidMessage, result.Error!.Code);
    }

    [Fact]
    public void RejectsDuplicateField()
    {
        var result = Parse("{\"type\":\"clip\",\"type\":\"clip\",\"id\":\"id1\",\"payload\":\"p\",\"encrypted\":true,\"hash\":\"h\"}");

        Assert.False(result.IsSuccess);
        Assert.Equal(ProtocolErrorCode.InvalidMessage, result.Error!.Code);
    }

    [Fact]
    public void RejectsEmptyClipPayload()
    {
        var result = Parse("{\"type\":\"clip\",\"id\":\"id1\",\"payload\":\"\",\"encrypted\":true,\"hash\":\"h\"}");

        Assert.False(result.IsSuccess);
        Assert.Equal(ProtocolErrorCode.EmptyText, result.Error!.Code);
    }

    [Fact]
    public void RejectsOversizedPayload()
    {
        var config = NewConfig();
        var payload = new string('x', config.Limits.MaxTextBytes + 1);
        var json = $"{{\"type\":\"clip\",\"id\":\"id1\",\"payload\":\"{payload}\",\"encrypted\":true,\"hash\":\"h\"}}";

        var result = Protocol.ParseClientMessage(Encoding.UTF8.GetBytes(json), config);

        Assert.False(result.IsSuccess);
        Assert.Equal(ProtocolErrorCode.TextTooLarge, result.Error!.Code);
    }
}
