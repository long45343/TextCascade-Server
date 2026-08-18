using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class LoginLimiterTests
{
    private static RuntimeConfig NewConfig() => TextCascade.Server.Config.CreateDefaultConfig() with
    {
        RateLimit = new RateLimitConfig(2, 2, 3, 10, 2),
    };

    [Fact]
    public void IpLimitBlocksAfterExceeding()
    {
        var limiter = new SlidingWindowLoginLimiter();
        var now = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        var config = NewConfig();

        Assert.True(limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, config));
        Assert.True(limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, config));
        Assert.False(limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, config));
    }

    [Fact]
    public void UserLimitBlocksAcrossIps()
    {
        var limiter = new SlidingWindowLoginLimiter();
        var now = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        var config = NewConfig();

        Assert.True(limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, config));
        Assert.True(limiter.TryConsumeLoginLimit("2.2.2.2", "alice", now, config));
        Assert.False(limiter.TryConsumeLoginLimit("3.3.3.3", "alice", now, config));
    }

    [Fact]
    public void SuccessResetsUserWindowButNotIpWindow()
    {
        var limiter = new SlidingWindowLoginLimiter();
        var now = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        var config = NewConfig();

        Assert.True(limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, config));
        Assert.True(limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, config));
        limiter.ResetUserLoginLimit("alice");
        Assert.False(limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, config));
    }

    [Fact]
    public void MaxKeysRejectsNewKeyWhenFull()
    {
        var limiter = new SlidingWindowLoginLimiter();
        var now = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        var config = NewConfig() with { RateLimit = new RateLimitConfig(2, 2, 4, 10, 2) };

        Assert.True(limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, config));
        Assert.True(limiter.TryConsumeLoginLimit("2.2.2.2", "bob", now, config));
        Assert.False(limiter.TryConsumeLoginLimit("3.3.3.3", "carol", now, config));
    }

    [Fact]
    public void ExpiredEntriesAreLazilyRemoved()
    {
        var limiter = new SlidingWindowLoginLimiter();
        var now = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        var later = now.AddMinutes(2);
        var config = NewConfig();

        Assert.True(limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, config));
        Assert.True(limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, config));
        Assert.False(limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, config));
        Assert.True(limiter.TryConsumeLoginLimit("1.1.1.1", "alice", later, config));
    }
}
