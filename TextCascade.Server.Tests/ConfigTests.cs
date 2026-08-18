using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class ConfigTests
{
    [Fact]
    public void CreateDefaultConfigHasExpectedValues()
    {
        var config = TextCascade.Server.Config.CreateDefaultConfig();
        Assert.Equal(8443, config.Server.Port);
        Assert.Equal(524288, config.Limits.MaxTextBytes);
        Assert.Equal(589824, config.Limits.MaxFrameBytes);
        Assert.Equal(30, config.Auth.TokenTtlDays);
    }

    [Fact]
    public void ValidateRejectsFrameBytesNotGreaterThanTextBytes()
    {
        var config = TextCascade.Server.Config.CreateDefaultConfig() with { Limits = new LimitsConfig(100, 100, 16, 64, 5, 30, 60, 3, 4194304, 16) };
        Assert.Throws<InvalidOperationException>(() => TextCascade.Server.Config.ValidateConfig(config));
    }

    [Fact]
    public void ValidateRejectsHeartbeatTimeoutNotGreaterThanInterval()
    {
        var config = TextCascade.Server.Config.CreateDefaultConfig() with { Limits = new LimitsConfig(524288, 589824, 16, 64, 5, 30, 30, 3, 4194304, 16) };
        Assert.Throws<InvalidOperationException>(() => TextCascade.Server.Config.ValidateConfig(config));
    }

    [Fact]
    public void ValidateRejectsMissingTokenSecret()
    {
        var config = TextCascade.Server.Config.CreateDefaultConfig();
        Assert.Throws<InvalidOperationException>(() => TextCascade.Server.Config.ValidateConfig(config));
    }

    [Fact]
    public void ValidateAcceptsFullConfig()
    {
        var config = TextCascade.Server.Config.CreateDefaultConfig() with { TokenSecret = new byte[32] };
        TextCascade.Server.Config.ValidateConfig(config);
    }

    [Fact]
    public void ApplyEnvironmentOverridesReadsTokenSecret()
    {
        Environment.SetEnvironmentVariable("TEXTCASCADE_TOKEN_SECRET", new string('s', 40));
        try
        {
            var config = TextCascade.Server.Config.ApplyEnvironmentOverrides(TextCascade.Server.Config.CreateDefaultConfig());
            Assert.NotNull(config.TokenSecret);
            Assert.True(config.TokenSecret!.Length >= 32);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEXTCASCADE_TOKEN_SECRET", null);
        }
    }
}
