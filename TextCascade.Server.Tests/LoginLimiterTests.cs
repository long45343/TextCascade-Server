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

    [Fact]
    public void RemoveExpiredKeepsUnexpiredRetryAfterOlderEntry()
    {
        var limiter = new SlidingWindowLoginLimiter();
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        var config = TextCascade.Server.Config.CreateDefaultConfig() with
        {
            RateLimit = new RateLimitConfig(3, 3, 10, 10, 2),
        };

        // Consume twice at t0
        Assert.True(limiter.TryConsumeLoginLimit("1.1.1.1", "alice", t0, config));
        Assert.True(limiter.TryConsumeLoginLimit("1.1.1.1", "alice", t0, config));

        // Consume once at t0 + 70s (this purges entries <= t0 + 10s, but window here is at t0+70s, cutoff t0+10s)
        var t70 = t0.AddSeconds(70);
        Assert.True(limiter.TryConsumeLoginLimit("1.1.1.1", "alice", t70, config));

        // In a non-monotonic test or another check: at t0 + 65s, let's test specific cutoff
        // Let's test by direct consumption with another limiter to follow spec exactly:
        var limiter2 = new SlidingWindowLoginLimiter();
        // limit=3; 同一 IP 在 t0 消费两次，t0+70s 消费一次。
        Assert.True(limiter2.TryConsumeLoginLimit("1.1.1.1", "alice", t0, config));
        Assert.True(limiter2.TryConsumeLoginLimit("1.1.1.1", "alice", t0, config));
        // Enqueue at t0+70s
        Assert.True(limiter2.TryConsumeLoginLimit("1.1.1.1", "alice", t0.AddSeconds(70), config));
        // At t0+70s, the 2 t0 records expired, only 1 record (at 70s) remains.
        // So we can consume 2 more times at t0+70s:
        Assert.True(limiter2.TryConsumeLoginLimit("1.1.1.1", "alice", t0.AddSeconds(70), config));
        Assert.True(limiter2.TryConsumeLoginLimit("1.1.1.1", "alice", t0.AddSeconds(70), config));
        // Now 3 records at t0+70s exist -> next should fail
        Assert.False(limiter2.TryConsumeLoginLimit("1.1.1.1", "alice", t0.AddSeconds(70), config));
    }

    [Fact]
    public void RemoveExpiredDeletesKeyOnlyWhenQueueIsEmpty()
    {
        var limiter = new SlidingWindowLoginLimiter();
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1760000000);

        limiter.EnqueueForTest("ip:1.1.1.1", t0);
        limiter.EnqueueForTest("ip:1.1.1.1", t0.AddSeconds(40));

        // Cutoff at t0 + 10s: first item expired, second item unexpired
        limiter.RemoveExpiredForTest(t0.AddSeconds(10));
        Assert.True(limiter.HasWindowKey("ip:1.1.1.1"));
        Assert.Equal(1, limiter.GetWindowCount("ip:1.1.1.1"));

        // Cutoff at t0 + 50s: all expired
        limiter.RemoveExpiredForTest(t0.AddSeconds(50));
        Assert.False(limiter.HasWindowKey("ip:1.1.1.1"));
    }

    [Fact]
    public void LoginLimitsMustBePositive()
    {
        var baseConfig = TextCascade.Server.Config.CreateDefaultConfig() with { TokenSecret = new byte[32] };

        var configZeroIp = baseConfig with
        {
            RateLimit = baseConfig.RateLimit with { LoginIpPerMinute = 0 },
        };
        Assert.Throws<InvalidOperationException>(() => TextCascade.Server.Config.ValidateConfig(configZeroIp));

        var configZeroUser = baseConfig with
        {
            RateLimit = baseConfig.RateLimit with { LoginUserPerMinute = 0 },
        };
        Assert.Throws<InvalidOperationException>(() => TextCascade.Server.Config.ValidateConfig(configZeroUser));
    }
}
