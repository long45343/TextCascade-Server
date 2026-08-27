using System.Security.Cryptography;
using System.Text;
using TextCascade.Server;

namespace TextCascade.Server.Tests;

public class AuthDeepTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes(new string('k', 32));

    private static UserRecord User(string username, long version) =>
        new(username, "$argon2id$v=19$m=19456,t=2,p=1$c2FsdA$hash", version);

    private static IReadOnlyDictionary<string, UserRecord> Lookup(params UserRecord[] users) =>
        users.ToDictionary(u => u.Username, u => u, StringComparer.Ordinal);

    private static string CompactFromPayloadJson(string payloadJson)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        var signature = HMACSHA256.HashData(Secret, payloadBytes);
        return Base64Url(payloadBytes) + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool Verify(string payloadJson, TokenPayload? expected = null)
    {
        var token = CompactFromPayloadJson(payloadJson);
        var now = DateTimeOffset.FromUnixTimeSeconds(1760000001);
        return new TokenService(Secret).TryVerifyToken(token, now, Lookup(User("alice", 1)), out var actual)
            && (expected is null || (actual.Subject == expected.Subject
                && actual.Version == expected.Version
                && actual.IssuedAtUnix == expected.IssuedAtUnix
                && actual.ExpiresAtUnix == expected.ExpiresAtUnix));
    }

    // U1
    [Fact]
    public void SignToken_FieldOrder_And_MinimalJson()
    {
        var payload = new TokenPayload("alice", 1, 1760000000, 1762592000);
        var compact = TokenService.SignToken(payload, Secret);
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(compact.Split('.')[0]));

        Assert.Equal("""{"sub":"alice","ver":1,"iat":1760000000,"exp":1762592000}""", payloadJson);
    }

    // U2
    [Fact]
    public void VerifyToken_Rejects_DuplicateFields()
    {
        Assert.False(Verify("""{"sub":"alice","ver":1,"ver":1,"iat":1760000000,"exp":1762592000}"""));
        Assert.False(Verify("""{"sub":"alice","sub":"alice","ver":1,"iat":1760000000,"exp":1762592000}"""));
    }

    // U3
    [Fact]
    public void VerifyToken_Rejects_UnknownField()
    {
        Assert.False(Verify("""{"sub":"alice","ver":1,"iat":1760000000,"exp":1762592000,"aud":"x"}"""));
    }

    // U4
    [Fact]
    public void VerifyToken_Rejects_FractionNumber()
    {
        Assert.False(Verify("""{"sub":"alice","ver":1,"iat":1760000000.0,"exp":1762592000}"""));
        Assert.False(Verify("""{"sub":"alice","ver":1.0,"iat":1760000000,"exp":1762592000}"""));
    }

    // U5
    [Fact]
    public void VerifyToken_Rejects_StringNumber()
    {
        Assert.False(Verify("""{"sub":"alice","ver":1,"iat":1760000000,"exp":"1762592000"}"""));
    }

    // U6
    [Fact]
    public void VerifyToken_Rejects_NegativeValue()
    {
        Assert.False(Verify("""{"sub":"alice","ver":-1,"iat":1760000000,"exp":1762592000}"""));
        Assert.False(Verify("""{"sub":"alice","ver":1,"iat":-1760000000,"exp":1762592000}"""));
    }

    // U7
    [Fact]
    public void VerifyToken_Rejects_ExpNotAfterIat()
    {
        Assert.False(Verify("""{"sub":"alice","ver":1,"iat":1762592000,"exp":1760000000}"""));
        Assert.False(Verify("""{"sub":"alice","ver":1,"iat":1760000000,"exp":1760000000}"""));
    }

    // U8
    [Fact]
    public void VerifyToken_Rejects_ZeroIat()
    {
        Assert.False(Verify("""{"sub":"alice","ver":1,"iat":0,"exp":1762592000}"""));
    }

    // U9
    [Fact]
    public void VerifyToken_RoundTrip_InstanceOverload()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        var service = new TokenService(Secret);
        var token = service.CreateToken(User("alice", 1), now, TimeSpan.FromDays(30));

        Assert.True(service.TryVerifyToken(token.CompactToken, now, Lookup(User("alice", 1)), out var payload));
        Assert.Equal("alice", payload.Subject);
        Assert.Equal(1, payload.Version);
        Assert.Equal(1760000000, payload.IssuedAtUnix);
        Assert.Equal(1762592000, payload.ExpiresAtUnix);
    }

    // U10
    [Fact]
    public void NeedsRehash_ParameterParsing()
    {
        var encoded = "$argon2id$v=19$m=19456,t=2,p=1$c2FsdA$aG9zdA";

        Assert.False(Argon2PasswordHasher.NeedsRehash(encoded, 19456, 2, 1));
        Assert.True(Argon2PasswordHasher.NeedsRehash(encoded, 1024, 2, 1));
        Assert.True(Argon2PasswordHasher.NeedsRehash(encoded, 19456, 3, 1));
        Assert.True(Argon2PasswordHasher.NeedsRehash(encoded, 19456, 2, 4));
        Assert.True(Argon2PasswordHasher.NeedsRehash("", 19456, 2, 1));
        Assert.True(Argon2PasswordHasher.NeedsRehash("$argon2i$v=19$m=19456,t=2,p=1$c2FsdA$aG9zdA", 19456, 2, 1));
        Assert.True(Argon2PasswordHasher.NeedsRehash("not-a-hash", 19456, 2, 1));
    }

    // U11
    [Fact]
    public void WithVersion_Produces_NewImmutableRecord()
    {
        var original = new LatestText("payload", 7, "hash", true, "client", "name", new DateTimeOffset(2026, 8, 18, 8, 0, 0, TimeSpan.Zero));

        var updated = CoreLogic.WithVersion(original, 8);
        Assert.Equal(8UL, updated.Version);
        Assert.Equal(original.Payload, updated.Payload);
        Assert.Equal(original.Hash, updated.Hash);
        Assert.Equal(original.Encrypted, updated.Encrypted);
        Assert.Equal(original.FromClientId, updated.FromClientId);
        Assert.Equal(original.FromClientName, updated.FromClientName);
        Assert.Equal(original.UpdatedAtUtc, updated.UpdatedAtUtc);
        Assert.Equal(7UL, original.Version);
        Assert.NotSame(original, updated);

        var withTime = CoreLogic.WithVersion(original, 9, new DateTimeOffset(2026, 8, 18, 9, 0, 0, TimeSpan.Zero));
        Assert.Equal(9UL, withTime.Version);
        Assert.Equal(new DateTimeOffset(2026, 8, 18, 9, 0, 0, TimeSpan.Zero), withTime.UpdatedAtUtc);
    }

    private static byte[] Base64UrlDecode(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        var remainder = padded.Length % 4;
        if (remainder > 0) { padded += new string('=', 4 - remainder); }
        return Convert.FromBase64String(padded);
    }
}