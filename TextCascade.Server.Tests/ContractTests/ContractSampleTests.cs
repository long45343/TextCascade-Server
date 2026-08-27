using System.Text;
using System.Text.Json;
using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class ContractSampleTests
{
    private static readonly string SamplesRoot = FindSamplesRoot();

    public static IEnumerable<object[]> InvalidSamples =>
        Directory.GetFiles(Path.Combine(SamplesRoot, "invalid"), "*.*", SearchOption.AllDirectories)
            .Select(path => new object[] { path });

    public static IEnumerable<object[]> ValidSamples =>
        Directory.GetFiles(Path.Combine(SamplesRoot, "valid"), "*.json", SearchOption.AllDirectories)
            .Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(InvalidSamples))]
    public void AllInvalidSamples_AreRejected_WithExpectedCode(string path)
    {
        var frame = File.ReadAllBytes(path);
        var config = TextCascade.Server.Config.CreateDefaultConfig();

        // Non-UTF8 samples may also throw while transcoding a string field; either outcome is a rejection.
        try
        {
            var result = Protocol.ParseClientMessage(frame, config);
            Assert.NotNull(result.Error);
            Assert.Equal(ExpectedCode(path), result.Error!.CodeName);
        }
        catch (Exception exception) when (exception is DecoderFallbackException or InvalidOperationException)
        {
            // Transcoding failure is itself the documented rejection path for invalid UTF-8.
        }
    }

    [Theory]
    [MemberData(nameof(ValidSamples))]
    public void AllValidSamples_Parse_WithExpectedKind(string path)
    {
        var frame = File.ReadAllBytes(path);
        var config = TextCascade.Server.Config.CreateDefaultConfig();

        var result = Protocol.ParseClientMessage(frame, config);

        Assert.True(result.Error is null, $"Sample should parse: {path} error={result.Error?.Message}");
        var expectedKind = Path.GetFileName(path) switch
        {
            string n when n.StartsWith("hello.", StringComparison.Ordinal) => MessageKind.Hello,
            string n when n.StartsWith("clip.", StringComparison.Ordinal) => MessageKind.Clip,
            string n when n.StartsWith("pong.", StringComparison.Ordinal) => MessageKind.Pong,
            _ => throw new InvalidOperationException($"Cannot infer kind from {path}"),
        };
        Assert.Equal(expectedKind, result.Kind);
    }

    [Fact]
    public void ValidHello_Full_ParsesAllFields()
    {
        var frame = File.ReadAllBytes(Path.Combine(SamplesRoot, "valid", "hello.full.json"));
        var result = Protocol.ParseClientMessage(frame, TextCascade.Server.Config.CreateDefaultConfig());

        Assert.True(result.Error is null);
        var hello = Assert.IsType<ClientHello>(result.Message);
        Assert.Equal("windows-a", hello.ClientId);
        Assert.Equal("Windows Desktop", hello.ClientName);
        Assert.Equal(128UL, hello.LastServerVersion);

        var snapshot = hello.Snapshot;
        Assert.NotNull(snapshot);
        Assert.Equal("clipboard text", snapshot!.Payload);
        Assert.True(snapshot.Encrypted);
        Assert.Equal("sha256-hex", snapshot.Hash);
        Assert.Equal(new DateTimeOffset(2026, 8, 18, 8, 0, 0, TimeSpan.Zero), snapshot.LocalModifiedAtUtc);
    }

    [Fact]
    public void ValidHello_Minimal_HasNoSnapshot()
    {
        var frame = File.ReadAllBytes(Path.Combine(SamplesRoot, "valid", "hello.minimal.json"));
        var result = Protocol.ParseClientMessage(frame, TextCascade.Server.Config.CreateDefaultConfig());

        Assert.True(result.Error is null, result.Error?.Message);
        var hello = Assert.IsType<ClientHello>(result.Message);
        Assert.Null(hello.Snapshot);
        Assert.Equal(0UL, hello.LastServerVersion);
        Assert.Equal(string.Empty, hello.ClientName);
    }

    [Fact]
    public void ValidHello_NullSnapshot_IsExplicitNull()
    {
        var frame = File.ReadAllBytes(Path.Combine(SamplesRoot, "valid", "hello.null-snapshot.json"));
        var result = Protocol.ParseClientMessage(frame, TextCascade.Server.Config.CreateDefaultConfig());

        Assert.True(result.Error is null, result.Error?.Message);
        var hello = Assert.IsType<ClientHello>(result.Message);
        Assert.Null(hello.Snapshot);
    }

    [Fact]
    public void ValidClip_ParsesAllFields()
    {
        var frame = File.ReadAllBytes(Path.Combine(SamplesRoot, "valid", "clip.basic.json"));
        var result = Protocol.ParseClientMessage(frame, TextCascade.Server.Config.CreateDefaultConfig());

        var clip = Assert.IsType<ClientClip>(result.Message);
        Assert.Equal("clip-20260818-001", clip.Id);
        Assert.Equal("shared clipboard content", clip.Payload);
        Assert.False(clip.Encrypted);
        Assert.Equal("sha256-hex", clip.Hash);
    }

    [Fact]
    public void ValidPong_ParsesTimestamp()
    {
        var frame = File.ReadAllBytes(Path.Combine(SamplesRoot, "valid", "pong.ok.json"));
        var result = Protocol.ParseClientMessage(frame, TextCascade.Server.Config.CreateDefaultConfig());

        var pong = Assert.IsType<ClientPong>(result.Message);
        Assert.Equal(new DateTimeOffset(2026, 8, 18, 8, 2, 0, TimeSpan.Zero), pong.ClientTimeUtc);
    }

    [Fact]
    public void ValidHello_RoundTripTimestampFormat_IsAccepted()
    {
        var frame = File.ReadAllBytes(Path.Combine(SamplesRoot, "valid", "hello.snapshot-roundtrip-timestamp.json"));
        var result = Protocol.ParseClientMessage(frame, TextCascade.Server.Config.CreateDefaultConfig());

        Assert.True(result.Error is null, result.Error?.Message);
        var hello = Assert.IsType<ClientHello>(result.Message);
        Assert.Equal(TimeSpan.Zero, hello.Snapshot!.LocalModifiedAtUtc.Offset);
    }

    private static string ExpectedCode(string path)
    {
        var relative = Path.GetRelativePath(Path.Combine(SamplesRoot, "invalid"), path);
        var category = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return category switch
        {
            "frame_too_large" => "frame_too_large",
            _ => "invalid_message",
        };
    }

    private static string FindSamplesRoot()
    {
        // AppContext.BaseDirectory works for dotnet test with CopyToOutputDirectory; the
        // upward walk covers runs where content files were not copied.
        var candidate = Path.Combine(AppContext.BaseDirectory, "ContractSamples");
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            candidate = Path.Combine(directory.FullName, "ContractSamples");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent!;
        }

        throw new InvalidOperationException("ContractSamples directory not found.");
    }
}