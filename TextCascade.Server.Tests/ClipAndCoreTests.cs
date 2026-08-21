using System.Text;
using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class ClipAndCoreTests
{
    private static RuntimeConfig NewConfig() => TextCascade.Server.Config.CreateDefaultConfig();

    [Fact]
    public void NextVersionIncrements()
    {
        Assert.Equal(1UL, CoreLogic.NextVersion(0));
        Assert.Equal(42UL, CoreLogic.NextVersion(41));
        Assert.Throws<InvalidOperationException>(() => CoreLogic.NextVersion(ulong.MaxValue));
    }

    [Fact]
    public void SeenIdRingDetectsDuplicates()
    {
        var ring = new SeenIdRing(4);
        Assert.False(ring.TryDuplicate("a"));
        Assert.True(ring.TryDuplicate("a"));
        Assert.False(ring.TryDuplicate("b"));
        Assert.True(ring.TryDuplicate("b"));
    }

    [Fact]
    public void SeenIdRingEvictsOldEntries()
    {
        var ring = new SeenIdRing(2);
        ring.RememberId("a");
        ring.RememberId("b");
        Assert.False(ring.TryDuplicate("c"));
        Assert.False(ring.TryDuplicate("a"));
    }

    [Fact]
    public void SeenIdRingRetainsOriginalResultForDuplicateAck()
    {
        var ring = new SeenIdRing(2);
        var original = new LatestText("first", 7, "hash", true, "client", "name", DateTimeOffset.UtcNow);

        ring.RememberId("clip-1", original);

        Assert.True(ring.TryGetResult("clip-1", out var result));
        Assert.Equal(original, result);
    }

    [Fact]
    public void SeenIdRingTreatsSameIdWithChangedContentAsNewClip()
    {
        var ring = new SeenIdRing(4);
        var original = new LatestText("first", 1, "hash-1", false, "client", "name", DateTimeOffset.UtcNow);
        ring.RememberId("same-id", original);

        Assert.False(ring.IsUnchangedDuplicate("same-id", "second", "hash-2", false, out _));
        Assert.True(ring.IsUnchangedDuplicate("same-id", "first", "hash-1", false, out var unchanged));
        Assert.Equal(original, unchanged);
    }

    [Fact]
    public void TokenBucketRefillsOverTime()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        var bucket = new TokenBucket(2, 2.0, now);
        Assert.True(bucket.TryAcquire(now));
        Assert.True(bucket.TryAcquire(now));
        Assert.False(bucket.TryAcquire(now));
        Assert.True(bucket.TryAcquire(now.AddSeconds(1)));
    }

    [Fact]
    public void CheckFrameSizeRespectsLimits()
    {
        var config = NewConfig();
        Assert.True(Protocol.CheckFrameSize(config.Limits.MaxFrameBytes, config));
        Assert.False(Protocol.CheckFrameSize(config.Limits.MaxFrameBytes + 1, config));
        Assert.False(Protocol.CheckFrameSize(0, config));
    }

    [Fact]
    public void CheckPayloadSizeRespectsMaxTextBytes()
    {
        var config = NewConfig();
        var within = new string('x', config.Limits.MaxTextBytes);
        var over = new string('x', config.Limits.MaxTextBytes + 1);
        Assert.True(Encoding.UTF8.GetByteCount(within) <= config.Limits.MaxTextBytes);
        Assert.True(Protocol.CheckPayloadSize(within, config));
        Assert.False(Protocol.CheckPayloadSize(over, config));
    }

    [Fact]
    public void ValidateClipMessageRejectsEmptyPayload()
    {
        var config = NewConfig();
        var clip = new ClientClip("id", "", true, "hash");
        Assert.False(Protocol.ValidateClipMessage(clip, config));
    }

    [Fact]
    public void ValidateClipMessageRejectsOversizedId()
    {
        var config = NewConfig();
        var id = new string('i', Protocol.MaxIdBytes + 1);
        var clip = new ClientClip(id, "payload", true, "hash");
        Assert.False(Protocol.ValidateClipMessage(clip, config));
    }

    [Fact]
    public void SelectSnapshotWinnerIgnoresZeroVersion()
    {
        var t = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        var helloZero = new ClientHello("a", "n", 0, new ClipSnapshot("p", true, "h", t));
        var helloOne = new ClientHello("b", "n", 1, new ClipSnapshot("p2", true, "h2", t));
        var winner = CoreLogic.SelectSnapshotWinner(new[] { helloZero, helloOne });
        Assert.NotNull(winner);
        Assert.Equal("p2", winner!.Snapshot.Payload);
        Assert.Equal(1UL, winner.Version);
    }

    [Fact]
    public void SelectSnapshotWinnerReturnsNullWhenNoPositiveVersion()
    {
        var t = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        var helloZero = new ClientHello("a", "n", 0, new ClipSnapshot("p", true, "h", t));
        Assert.Null(CoreLogic.SelectSnapshotWinner(new[] { helloZero }));
    }

    [Fact]
    public void SelectSnapshotWinnerBreaksTiesByTimeThenClientId()
    {
        var earlier = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        var later = DateTimeOffset.FromUnixTimeSeconds(1760000010);
        var sameVersion = 5UL;
        var a = new ClientHello("aaa", "n", sameVersion, new ClipSnapshot("pa", true, "h", earlier));
        var b = new ClientHello("zzz", "n", sameVersion, new ClipSnapshot("pb", true, "h", later));
        var winner = CoreLogic.SelectSnapshotWinner(new[] { a, b });
        Assert.Equal("pb", winner!.Snapshot.Payload);

        var sameTime = later;
        var a2 = new ClientHello("aaa", "n", sameVersion, new ClipSnapshot("pa", true, "h", sameTime));
        var b2 = new ClientHello("zzz", "n", sameVersion, new ClipSnapshot("pb", true, "h", sameTime));
        var winner2 = CoreLogic.SelectSnapshotWinner(new[] { a2, b2 });
        Assert.Equal("pb", winner2!.Snapshot.Payload);
    }
}
