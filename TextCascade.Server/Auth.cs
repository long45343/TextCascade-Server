using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Isopoh.Cryptography.Argon2;
using Isopoh.Cryptography.SecureArray;

namespace TextCascade.Server;

public interface IPasswordHasher
{
    string Hash(string password, Argon2Config config);

    bool Verify(string password, string encodedHash);

    bool NeedsRehash(string encodedHash, Argon2Config config);
}

public sealed class Argon2PasswordHasher : IPasswordHasher
{
    public bool NeedsRehash(string encodedHash, Argon2Config config)
    {
        return NeedsRehash(encodedHash, config.MemoryCost, config.TimeCost, config.Threads);
    }

    public static bool NeedsRehash(string encodedHash, int memoryKiB, int timeCost, int threads)
    {
        // Parse the stored Argon2id PHC string ($argon2id$v=N$m=M,t=T,p=P$...).
        // If the stored m/t/p differ from the current configured values, signal a rehash.
        if (string.IsNullOrEmpty(encodedHash) || !encodedHash.StartsWith("$argon2id$", StringComparison.Ordinal))
        {
            return true;
        }

        var segments = encodedHash.Split('$');
        if (segments.Length < 4)
        {
            return true;
        }

        // segments[0] is empty (leading $), [1] is "argon2id", [2] is "v=N", [3] is "m=M,t=T,p=P".
        var parameters = segments[3].Split(',');
        var storedMemory = -1;
        var storedTime = -1;
        var storedThreads = -1;
        foreach (var parameter in parameters)
        {
            var pair = parameter.Split('=');
            if (pair.Length != 2) continue;
            if (pair[0] == "m" && int.TryParse(pair[1], CultureInfo.InvariantCulture, out var m)) storedMemory = m;
            else if (pair[0] == "t" && int.TryParse(pair[1], CultureInfo.InvariantCulture, out var t)) storedTime = t;
            else if (pair[0] == "p" && int.TryParse(pair[1], CultureInfo.InvariantCulture, out var p)) storedThreads = p;
        }

        return storedMemory != memoryKiB || storedTime != timeCost || storedThreads != threads;
    }

    public string Hash(string password, Argon2Config config)
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);
        var useConfig = new Argon2Config
        {
            Type = Argon2Type.HybridAddressing,
            Version = Argon2Version.Nineteen,
            MemoryCost = config.MemoryCost,
            TimeCost = config.TimeCost,
            Threads = config.Threads,
            Password = Encoding.UTF8.GetBytes(password),
            Salt = salt,
        };
        return Argon2.Hash(useConfig);
    }

    public bool Verify(string password, string encodedHash)
    {
        try
        {
            var secret = Encoding.UTF8.GetBytes(password);
            return Argon2.Verify(encodedHash, secret);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

}

public sealed record TokenPayload(string Subject, long Version, long IssuedAtUnix, long ExpiresAtUnix);

public sealed record AuthToken(TokenPayload Payload, string CompactToken);

public sealed class TokenService
{
    private readonly byte[] secret;

    public TokenService(byte[] tokenSecret)
    {
        if (tokenSecret is null || tokenSecret.Length < 32) throw new ArgumentException("Token secret must be at least 32 bytes.");
        secret = (byte[])tokenSecret.Clone();
    }

    public AuthToken CreateToken(UserRecord user, DateTimeOffset nowUtc, TimeSpan timeToLive)
    {
        var payload = CreateTokenPayload(user, nowUtc, timeToLive);
        return new AuthToken(payload, SignToken(payload, secret));
    }

    public static TokenPayload CreateTokenPayload(UserRecord user, DateTimeOffset nowUtc, TimeSpan timeToLive)
    {
        if (timeToLive <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeToLive));
        return new TokenPayload(
            user.Username,
            user.TokenVersion,
            nowUtc.ToUnixTimeSeconds(),
            (nowUtc + timeToLive).ToUnixTimeSeconds());
    }

    public static string SignToken(TokenPayload payload, byte[] secret)
    {
        if (secret is null || secret.Length < 32) throw new ArgumentException("Token secret must be at least 32 bytes.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("sub", payload.Subject);
            writer.WriteNumber("ver", payload.Version);
            writer.WriteNumber("iat", payload.IssuedAtUnix);
            writer.WriteNumber("exp", payload.ExpiresAtUnix);
            writer.WriteEndObject();
        }

        var payloadBytes = stream.ToArray();
        var payloadSegment = Base64UrlEncode(payloadBytes);
        var signature = HMACSHA256.HashData(secret, payloadBytes);
        return $"{payloadSegment}.{Base64UrlEncode(signature)}";
    }

    public bool VerifyToken(string compactToken, DateTimeOffset nowUtc, IReadOnlyDictionary<string, UserRecord> userLookup)
    {
        return TryVerifyToken(compactToken, secret, nowUtc, userLookup, out _);
    }

    public bool TryVerifyToken(string compactToken, DateTimeOffset nowUtc, IReadOnlyDictionary<string, UserRecord> userLookup, out TokenPayload payload)
    {
        return TryVerifyTokenInternal(compactToken, secret, nowUtc, userLookup, out payload);
    }

    public static bool VerifyToken(string compactToken, byte[] secret, DateTimeOffset nowUtc, IReadOnlyDictionary<string, UserRecord> userLookup)
    {
        return TryVerifyToken(compactToken, secret, nowUtc, userLookup, out _);
    }

    public static bool TryVerifyToken(string compactToken, byte[] secret, DateTimeOffset nowUtc, IReadOnlyDictionary<string, UserRecord> userLookup, out TokenPayload payload)
    {
        if (secret is null || secret.Length < 32)
        {
            payload = null!;
            return false;
        }

        return TryVerifyTokenInternal(compactToken, secret, nowUtc, userLookup, out payload);
    }

    private static bool TryVerifyTokenInternal(string compactToken, byte[] secret, DateTimeOffset nowUtc, IReadOnlyDictionary<string, UserRecord> userLookup, out TokenPayload verified)
    {
        verified = null!;
        if (string.IsNullOrEmpty(compactToken) || compactToken.Length > 8192)
        {
            return false;
        }

        var parts = compactToken.Split('.');
        if (parts.Length != 2)
        {
            return false;
        }

        Span<byte> payloadBytes = stackalloc byte[1024];
        Span<byte> signatureBytes = stackalloc byte[32];
        byte[]? payloadRent = null;
        byte[]? signatureRent = null;
        try
        {
            if (!TryBase64UrlDecode(parts[0], ref payloadRent, payloadBytes, out var payloadLength)
                || !TryBase64UrlDecode(parts[1], ref signatureRent, signatureBytes, out var signatureLength)
                || signatureLength != 32)
            {
                return false;
            }

            var actualPayload = payloadRent is null ? payloadBytes[..payloadLength].ToArray() : payloadRent[..payloadLength];
            var expectedSignature = HMACSHA256.HashData(secret, actualPayload);
            var actualSignature = signatureRent is null ? signatureBytes[..signatureLength].ToArray() : signatureRent[..signatureLength];
            if (!CryptographicOperations.FixedTimeEquals(actualSignature, expectedSignature))
            {
                return false;
            }

            using var document = JsonDocument.Parse(actualPayload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 2,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4)
            {
                return false;
            }

            var properties = root.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
            if (!properties.ContainsKey("sub") || !properties.ContainsKey("ver") || !properties.ContainsKey("iat") || !properties.ContainsKey("exp"))
            {
                return false;
            }

            var subject = properties["sub"];
            if (subject.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(subject.GetString())
                || properties["ver"].ValueKind != JsonValueKind.Number || !TryParsePositiveInteger(properties["ver"].GetRawText(), out var version)
                || properties["iat"].ValueKind != JsonValueKind.Number || !TryParsePositiveInteger(properties["iat"].GetRawText(), out var issuedAt)
                || properties["exp"].ValueKind != JsonValueKind.Number || !TryParsePositiveInteger(properties["exp"].GetRawText(), out var expiresAt)
                || expiresAt <= issuedAt || nowUtc.ToUnixTimeSeconds() >= expiresAt)
            {
                return false;
            }

            var candidate = new TokenPayload(subject.GetString()!, version, issuedAt, expiresAt);
            if (!userLookup.TryGetValue(candidate.Subject, out var user) || user.Disabled || user.TokenVersion != candidate.Version)
            {
                return false;
            }

            verified = candidate;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParsePositiveInteger(string raw, out long value)
    {
        var span = raw.AsSpan();
        if (span.IsEmpty || span.StartsWith('-') || span.Contains('.') || span.Contains('e') || span.Contains('E'))
        {
            value = 0;
            return false;
        }

        return long.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryBase64UrlDecode(string text, ref byte[]? rented, Span<byte> buffer, out int length)
    {
        rented = null;
        if (string.IsNullOrEmpty(text) || text.Length % 4 == 1)
        {
            length = 0;
            return false;
        }

        foreach (var character in text)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                length = 0;
                return false;
            }
        }

        var paddedLength = text.Length + (4 - text.Length % 4) % 4;
        if (paddedLength > buffer.Length)
        {
            rented = new byte[paddedLength];
            buffer = rented;
        }

        var chars = new char[paddedLength];
        for (var i = 0; i < text.Length; i++)
        {
            chars[i] = text[i] switch { '-' => '+', '_' => '/', _ => text[i] };
        }

        for (var i = text.Length; i < paddedLength; i++)
        {
            chars[i] = '=';
        }
        return Convert.TryFromBase64Chars(chars, buffer, out length) && length > 0;
    }
}
