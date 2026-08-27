using System.Text.RegularExpressions;
using Isopoh.Cryptography.Argon2;
using TextCascade.Server;

namespace TextCascade.Server.Tests;

[Trait("Category", "SlowHash")]
public class SlowHashSmokeTests
{
    private static Argon2Config ProductionConfig() => Cli.CreateArgon2Config(TextCascade.Server.Config.CreateDefaultConfig());

    private static (int Memory, int Time, int Threads) ParseEncodedParameters(string encoded)
    {
        var match = Regex.Match(encoded, @"m=(\d+),t=(\d+),p=(\d+)");
        Assert.True(match.Success, $"Not an Argon2 PHC string: {encoded}");
        return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
    }

    // U26
    [Fact]
    public void Hash_Then_Verify_RoundTrip()
    {
        var hasher = new Argon2PasswordHasher();
        var encoded = hasher.Hash("correct horse battery staple", ProductionConfig());

        Assert.StartsWith("$argon2id$", encoded, StringComparison.Ordinal);
        Assert.True(hasher.Verify("correct horse battery staple", encoded));
        Assert.False(hasher.Verify("wrong password", encoded));
    }

    // U27 — NeedsRehash is false when the encoded parameters match themselves exactly.
    // (The Isopoh encoder may write a lane count that differs from the configured
    // Argon2Parallelism, so the test reads back the actual m/t/p instead of assuming.)
    [Fact]
    public void NeedsRehash_MatchingParams_ReturnsFalse()
    {
        var hasher = new Argon2PasswordHasher();
        var encoded = hasher.Hash("some-password", ProductionConfig());
        var (memory, time, threads) = ParseEncodedParameters(encoded);

        Assert.False(Argon2PasswordHasher.NeedsRehash(encoded, memory, time, threads));
        Assert.True(Argon2PasswordHasher.NeedsRehash(encoded, memory + 1024, time, threads));
        Assert.True(Argon2PasswordHasher.NeedsRehash(encoded, memory, time + 1, threads));
        Assert.True(Argon2PasswordHasher.NeedsRehash(encoded, memory, time, threads + 1));
    }

    // U28 — rewriting the stored parameter segment to stale values flags a rehash.
    [Fact]
    public void NeedsRehash_StaleParams_ReturnsTrue()
    {
        var hasher = new Argon2PasswordHasher();
        var encoded = hasher.Hash("some-password", ProductionConfig());
        var (memory, time, threads) = ParseEncodedParameters(encoded);

        var stale = encoded
            .Replace($"m={memory}", $"m={Math.Max(1024, memory / 8)}", StringComparison.Ordinal)
            .Replace($"t={time}", "t=1", StringComparison.Ordinal)
            .Replace($"p={threads}", "p=1", StringComparison.Ordinal);
        Assert.NotEqual(encoded, stale);

        Assert.True(Argon2PasswordHasher.NeedsRehash(stale, memory, time, threads));
    }
}
