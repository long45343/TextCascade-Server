using System.Text;
using System.Text.Json;
using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class TokenServiceTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes(new string('k', 32));

    private static UserRecord User(string username, long version) => new(username, "$argon2id$v=19$m=19456,t=2,p=1$c2FsdA$hash", version);

    private static IReadOnlyDictionary<string, UserRecord> Lookup(params UserRecord[] users) =>
        users.ToDictionary(u => u.Username, u => u, StringComparer.Ordinal);

    [Fact]
    public void RoundTrip()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        var service = new TokenService(Secret);
        var token = service.CreateToken(User("alice", 1), now, TimeSpan.FromDays(30));
        Assert.True(service.TryVerifyToken(token.CompactToken, now, Lookup(User("alice", 1)), out var payload));
        Assert.Equal("alice", payload.Subject);
        Assert.Equal(1, payload.Version);
    }

    [Fact]
    public void RejectsExpiredToken()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        var service = new TokenService(Secret);
        var token = service.CreateToken(User("alice", 1), now, TimeSpan.FromSeconds(10));
        var later = now.AddSeconds(20);
        Assert.False(service.TryVerifyToken(token.CompactToken, later, Lookup(User("alice", 1)), out _));
    }

    [Fact]
    public void RejectsWhenTokenVersionChanged()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        var service = new TokenService(Secret);
        var token = service.CreateToken(User("alice", 1), now, TimeSpan.FromDays(30));
        Assert.False(service.TryVerifyToken(token.CompactToken, now, Lookup(User("alice", 2)), out _));
    }

    [Fact]
    public void RejectsWhenUserDisabled()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        var service = new TokenService(Secret);
        var token = service.CreateToken(User("alice", 1), now, TimeSpan.FromDays(30));
        var disabled = User("alice", 1) with { Disabled = true };
        Assert.False(service.TryVerifyToken(token.CompactToken, now, Lookup(disabled), out _));
    }

    [Fact]
    public void RejectsWhenUserMissing()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        var service = new TokenService(Secret);
        var token = service.CreateToken(User("alice", 1), now, TimeSpan.FromDays(30));
        Assert.False(service.TryVerifyToken(token.CompactToken, now, Lookup(), out _));
    }

    [Fact]
    public void RejectsTamperedSignature()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        var service = new TokenService(Secret);
        var token = service.CreateToken(User("alice", 1), now, TimeSpan.FromDays(30));
        var tampered = token.CompactToken[..^1] + (token.CompactToken[^1] == 'a' ? 'b' : 'a');
        Assert.False(service.TryVerifyToken(tampered, now, Lookup(User("alice", 1)), out _));
    }

    [Fact]
    public void RejectsUnknownFields()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        var payloadJson = "{\"sub\":\"alice\",\"ver\":1,\"iat\":1760000000,\"exp\":1762592000,\"extra\":5}";
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        var signature = System.Security.Cryptography.HMACSHA256.HashData(Secret, payloadBytes);
        var token = Base64Url(payloadBytes) + "." + Base64Url(signature);
        Assert.False(new TokenService(Secret).TryVerifyToken(token, now, Lookup(User("alice", 1)), out _));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
