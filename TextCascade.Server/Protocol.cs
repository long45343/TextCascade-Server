using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace TextCascade.Server;

public enum MessageKind
{
    Unknown,
    Hello,
    Clip,
    Pong,
}

public enum ProtocolErrorCode
{
    InvalidMessage,
    TextTooLarge,
    FrameTooLarge,
    EmptyText,
    RateLimited,
    HelloTimeout,
    ServerBusy,
}

public sealed record ProtocolError(ProtocolErrorCode Code, string Message, string? ReferenceId = null)
{
    public string CodeName => Code switch
    {
        ProtocolErrorCode.TextTooLarge => "text_too_large",
        ProtocolErrorCode.FrameTooLarge => "frame_too_large",
        ProtocolErrorCode.EmptyText => "empty_text",
        ProtocolErrorCode.RateLimited => "rate_limited",
        ProtocolErrorCode.HelloTimeout => "hello_timeout",
        ProtocolErrorCode.ServerBusy => "server_busy",
        _ => "invalid_message",
    };
}

public readonly record struct Result<T>
{
    public Result(T? value, ProtocolError? error = null)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public ProtocolError? Error { get; }

    public bool IsSuccess => Error is null;

    public static Result<T> Success(T value) => new(value, null);

    public static Result<T> Failure(ProtocolErrorCode code, string message, string? referenceId = null) =>
        new(default, new ProtocolError(code, message, referenceId));
}

public sealed record ClipSnapshot(
    string Payload,
    bool Encrypted,
    string Hash,
    DateTimeOffset LocalModifiedAtUtc);

public sealed record ClientHello(
    string ClientId,
    string ClientName,
    ulong LastServerVersion,
    ClipSnapshot? Snapshot);

public sealed record ClientClip(
    string Id,
    string Payload,
    bool Encrypted,
    string Hash);

public sealed record ClientPong(DateTimeOffset ClientTimeUtc);

public sealed record ClientMessage(MessageKind Kind, object Message);

public sealed record LatestText(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("version")] ulong Version,
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("encrypted")] bool Encrypted,
    [property: JsonPropertyName("fromClientId")] string FromClientId,
    [property: JsonPropertyName("fromClientName")] string FromClientName,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc)
{
    public static LatestText From(ClipSnapshot snapshot, ulong version, string clientId, string clientName)
    {
        return new LatestText(
            snapshot.Payload,
            version,
            snapshot.Hash,
            snapshot.Encrypted,
            clientId,
            clientName,
            snapshot.LocalModifiedAtUtc);
    }
}

[JsonSerializable(typeof(WelcomeMessage))]
[JsonSerializable(typeof(ClipMessage))]
[JsonSerializable(typeof(ClipAckMessage))]
[JsonSerializable(typeof(PingMessage))]
[JsonSerializable(typeof(ByeMessage))]
[JsonSerializable(typeof(ProtocolErrorMessage))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class ServerJsonContext : JsonSerializerContext
{
    public static ServerJsonContext Configured { get; }

    public static JsonSerializerOptions SerializationOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new UtcSecondDateTimeConverter() },
    };

    static ServerJsonContext() => Configured = new ServerJsonContext(SerializationOptions);
}

public sealed record WelcomeMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("latest")] LatestText? Latest);

public sealed record ClipMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("version")] ulong Version,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("encrypted")] bool Encrypted,
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("fromClientId")] string FromClientId,
    [property: JsonPropertyName("fromClientName")] string FromClientName,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc);

public sealed record ClipAckMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] ulong Version,
    [property: JsonPropertyName("updatedAtUtc")] DateTimeOffset UpdatedAtUtc);

public sealed record PingMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("serverTimeUtc")] DateTimeOffset ServerTimeUtc);

public sealed record ByeMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record ProtocolErrorMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("referenceId")] string? ReferenceId);

internal sealed class UtcSecondDateTimeConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String && DateTimeOffset.TryParse(reader.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.ToUniversalTime()
            : throw new JsonException("Invalid date/time value.");

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
}

public static class Protocol
{
    public const int ProtocolVersion = 1;

    public const int MaxNameBytes = 128;

    public const int MaxIdBytes = 128;

    public const int MaxHashBytes = 4096;

    public static byte[] SerializeMessage<T>(T message, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);

    public static byte[] SerializeWelcome(LatestText? latest, LimitsConfig _)
    {
        var message = new WelcomeMessage("welcome", ProtocolVersion, latest);
        return SerializeMessage(message, ServerJsonContext.Configured.WelcomeMessage);
    }

    public static byte[] SerializeClip(string id, LatestText latest)
    {
        var message = new ClipMessage(
            "clip",
            latest.Version,
            id,
            latest.Payload,
            latest.Encrypted,
            latest.Hash,
            latest.FromClientId,
            latest.FromClientName,
            latest.UpdatedAtUtc);
        return SerializeMessage(message, ServerJsonContext.Configured.ClipMessage);
    }

    public static byte[] SerializeClipAck(string id, LatestText latest)
    {
        var message = new ClipAckMessage("clip_ack", id, latest.Version, latest.UpdatedAtUtc);
        return SerializeMessage(message, ServerJsonContext.Configured.ClipAckMessage);
    }

    public static byte[] SerializePing(DateTimeOffset nowUtc) =>
        SerializeMessage(new PingMessage("ping", nowUtc), ServerJsonContext.Configured.PingMessage);

    public static byte[] SerializeBye(string reason = "server_shutdown") =>
        SerializeMessage(new ByeMessage("bye", reason), ServerJsonContext.Configured.ByeMessage);

    public static byte[] SerializeProtocolError(ProtocolError error) =>
        SerializeMessage(
            new ProtocolErrorMessage("error", error.CodeName, error.Message, error.ReferenceId),
            ServerJsonContext.Configured.ProtocolErrorMessage);

    public static byte[] SerializeLoginResponse(AuthToken token, RuntimeConfig config, bool needsRehash = false)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("token", token.CompactToken);
        writer.WriteString("expiresAtUtc", DateTimeOffset.FromUnixTimeSeconds(token.Payload.ExpiresAtUnix).ToUniversalTime().ToString("O"));
        writer.WriteNumber("protocolVersion", ProtocolVersion);
        writer.WriteNumber("maxTextBytes", config.Limits.MaxTextBytes);
        writer.WriteNumber("helloTimeoutSeconds", config.Limits.HelloTimeoutSeconds);
        writer.WriteNumber("heartbeatIntervalSeconds", config.Limits.HeartbeatIntervalSeconds);
        writer.WriteNumber("heartbeatTimeoutSeconds", config.Limits.HeartbeatTimeoutSeconds);
        if (needsRehash)
        {
            writer.WriteBoolean("needsRehash", true);
        }
        writer.WriteEndObject();
        writer.Flush();
        return stream.ToArray();
    }

    public static ParseResult ParseClientMessage(ReadOnlySpan<byte> frame, RuntimeConfig config)
    {
        using var document = TryParseJson(frame, out var parseError);
        if (parseError is not null)
        {
            return ParseResult.Failure(parseError);
        }

        var root = document!.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ParseResult.Failure(Invalid("Root must be an object."));
        }

        if (!TryGetString(root, "type", null, out var type))
        {
            return ParseResult.Failure(Invalid("Missing or invalid type.", GetReferenceId(root)));
        }

        if (!HasUniqueKnownProperties(root, type switch
        {
            "hello" => new[] { "type", "clientId", "clientName", "lastServerVersion", "snapshot" },
            "clip" => new[] { "type", "id", "payload", "encrypted", "hash" },
            "pong" => new[] { "type", "clientTimeUtc" },
            _ => Array.Empty<string>(),
        }, out var propertyName))
        {
            return ParseResult.Failure(Invalid($"Unknown or duplicate field: {propertyName}.", GetReferenceId(root)));
        }

        return type switch
        {
            "hello" => ParseHello(root, config),
            "clip" => ParseClip(root, config),
            "pong" => ParsePong(root),
            _ => ParseResult.Failure(Invalid("Unknown message type.", GetReferenceId(root))),
        };
    }

    private static ParseResult ParseHello(JsonElement root, RuntimeConfig config)
    {
        if (!TryGetString(root, "clientId", MaxNameBytes, out var clientId) || clientId.Length == 0)
        {
            return ParseResult.Failure(Invalid("clientId must be 1-128 bytes."));
        }

        TryGetString(root, "clientName", MaxNameBytes, out var clientName);

        if (!TryGetUInt64(root, "lastServerVersion", out var lastVersion))
        {
            return ParseResult.Failure(Invalid("lastServerVersion must be a non-negative integer."));
        }

        if (root.TryGetProperty("snapshot", out var snapshotElement))
        {
            if (snapshotElement.ValueKind != JsonValueKind.Object && snapshotElement.ValueKind != JsonValueKind.Null)
            {
                return ParseResult.Failure(Invalid("snapshot must be an object or null."));
            }

            if (snapshotElement.ValueKind == JsonValueKind.Object)
            {
                if (!HasUniqueKnownProperties(snapshotElement,
                    new[] { "payload", "encrypted", "hash", "localModifiedAtUtc" },
                    out var propertyName))
                {
                    return ParseResult.Failure(Invalid($"Unknown or duplicate field: {propertyName}."));
                }

                if (!TryGetSnapshot(snapshotElement, config, out var snapshot, out var snapshotError))
                {
                    return ParseResult.Failure(snapshotError!);
                }

                var hello = new ClientHello(clientId, clientName, lastVersion, snapshot);
                return ValidateHello(hello, config) ? ParseResult.Success(MessageKind.Hello, hello)
                    : ParseResult.Failure(Invalid("hello validation failed."));
            }
        }

        var plainHello = new ClientHello(clientId, clientName, lastVersion, null);
        return ValidateHello(plainHello, config) ? ParseResult.Success(MessageKind.Hello, plainHello)
            : ParseResult.Failure(Invalid("hello validation failed."));
    }

    private static bool TryGetSnapshot(JsonElement element, RuntimeConfig config, out ClipSnapshot? snapshot, out ProtocolError? error)
    {
        snapshot = null;
        if (!TryGetString(element, "payload", null, out var payload) || string.IsNullOrEmpty(payload))
        {
            error = Invalid("snapshot.payload is required and must not be empty.");
            return false;
        }

        if (!ValidatePayloadSize(payload, config, out var payloadError))
        {
            error = payloadError;
            return false;
        }

        if (!TryGetBoolean(element, "encrypted", out var encrypted))
        {
            error = Invalid("snapshot.encrypted must be a boolean.");
            return false;
        }

        if (!TryGetString(element, "hash", MaxHashBytes, out var hash) || hash.Length == 0)
        {
            error = Invalid("snapshot.hash is required.");
            return false;
        }

        if (!TryGetUtcDateTime(element, "localModifiedAtUtc", out var modified))
        {
            error = Invalid("snapshot.localModifiedAtUtc must be UTC RFC3339.");
            return false;
        }

        snapshot = new ClipSnapshot(payload, encrypted, hash, modified);
        error = null;
        return true;
    }

    private static ParseResult ParseClip(JsonElement root, RuntimeConfig config)
    {
        if (!TryGetString(root, "id", MaxIdBytes, out var id) || id.Length == 0)
        {
            return ParseResult.Failure(Invalid("id must be 1-128 bytes."));
        }

        if (!TryGetString(root, "payload", null, out var payload) || string.IsNullOrEmpty(payload))
        {
            return ParseResult.Failure(new ProtocolError(ProtocolErrorCode.EmptyText, "payload must not be empty.", id));
        }

        if (!ValidatePayloadSize(payload, config, out var error))
        {
            return ParseResult.Failure(new ProtocolError(error!.Code, error.Message, id));
        }

        if (!TryGetBoolean(root, "encrypted", out var encrypted))
        {
            return ParseResult.Failure(Invalid("encrypted must be a boolean.", id));
        }

        if (!TryGetString(root, "hash", MaxHashBytes, out var hash) || hash.Length == 0)
        {
            return ParseResult.Failure(Invalid("hash is required.", id));
        }

        var clip = new ClientClip(id, payload, encrypted, hash);
        return ValidateClipMessage(clip, config)
            ? ParseResult.Success(MessageKind.Clip, clip)
            : ParseResult.Failure(Invalid("clip validation failed.", id));
    }

    private static ParseResult ParsePong(JsonElement root)
    {
        if (!TryGetUtcDateTime(root, "clientTimeUtc", out var clientTime))
        {
            return ParseResult.Failure(Invalid("clientTimeUtc must be UTC RFC3339."));
        }

        var pong = new ClientPong(clientTime);
        return ParseResult.Success(MessageKind.Pong, pong);
    }

    public static bool ValidateHello(ClientHello hello, RuntimeConfig config)
    {
        return Encoding.UTF8.GetByteCount(hello.ClientId) is > 0 and <= MaxNameBytes
            && Encoding.UTF8.GetByteCount(hello.ClientName) <= MaxNameBytes
            && (hello.Snapshot is null || ValidateClipSnapshot(hello.Snapshot, config));
    }

    private static bool ValidateClipSnapshot(ClipSnapshot snapshot, RuntimeConfig config)
    {
        return snapshot.Payload.Length > 0
            && Encoding.UTF8.GetByteCount(snapshot.Payload) <= config.Limits.MaxTextBytes
            && Encoding.UTF8.GetByteCount(snapshot.Hash) <= MaxHashBytes
            && snapshot.LocalModifiedAtUtc.Offset == TimeSpan.Zero;
    }

    public static bool ValidateClipMessage(ClientClip message, RuntimeConfig config)
    {
        return message.Id.Length > 0
            && Encoding.UTF8.GetByteCount(message.Id) <= MaxIdBytes
            && message.Payload.Length > 0
            && Encoding.UTF8.GetByteCount(message.Payload) <= config.Limits.MaxTextBytes
            && message.Hash.Length > 0
            && Encoding.UTF8.GetByteCount(message.Hash) <= MaxHashBytes;
    }

    public static bool CheckFrameSize(int frameLength, RuntimeConfig config)
    {
        return frameLength is > 0 && frameLength <= config.Limits.MaxFrameBytes;
    }

    public static bool CheckPayloadSize(string payload, RuntimeConfig config)
    {
        return Encoding.UTF8.GetByteCount(payload) <= config.Limits.MaxTextBytes;
    }

    private static bool ValidatePayloadSize(string payload, RuntimeConfig config, out ProtocolError? error)
    {
        if (Encoding.UTF8.GetByteCount(payload) > config.Limits.MaxTextBytes)
        {
            error = new ProtocolError(ProtocolErrorCode.TextTooLarge, "Text exceeds maxTextBytes.");
            return false;
        }

        error = null;
        return true;
    }

    private static JsonDocument? TryParseJson(ReadOnlySpan<byte> frame, out ProtocolError? error)
    {
        try
        {
            var document = JsonDocument.Parse(frame.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 3,
            });
            error = null;
            return document;
        }
        catch (JsonException exception)
        {
            error = Invalid($"Invalid JSON: {exception.Message}");
            return null;
        }
    }

    private static string? GetReferenceId(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
    }

    private static bool HasUniqueKnownProperties(JsonElement root, string[] known, out string? invalidName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!known.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
            {
                invalidName = property.Name;
                return false;
            }
        }

        invalidName = null;
        return true;
    }

    private static bool TryGetString(JsonElement root, string name, int? maxBytes, out string value)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            value = string.Empty;
            return false;
        }

        value = element.GetString()!;
        return maxBytes is null || Encoding.UTF8.GetByteCount(value) <= maxBytes.Value;
    }

    private static bool TryGetBoolean(JsonElement root, string name, out bool value)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False)
        {
            value = false;
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool TryGetUInt64(JsonElement root, string name, out ulong value)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number)
        {
            value = 0;
            return false;
        }

        var raw = element.GetRawText().AsSpan();
        if (raw.StartsWith('-') || raw.Contains('.') || raw.Contains('e') || raw.Contains('E') || !ulong.TryParse(raw, out value))
        {
            value = 0;
            return false;
        }

        return true;
    }

    private static bool TryGetUtcDateTime(JsonElement root, string name, out DateTimeOffset value)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            value = default;
            return false;
        }

        var text = element.GetString()!;
        if (!text.EndsWith('Z'))
        {
            value = default;
            return false;
        }

        if (DateTimeOffset.TryParseExact(text, "O", CultureInfoInvariant, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value)
            || DateTimeOffset.TryParseExact(text, "yyyy-MM-ddTHH:mm:ssZ", CultureInfoInvariant, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static ProtocolError Invalid(string message, string? referenceId = null) =>
        new(ProtocolErrorCode.InvalidMessage, message, referenceId);

    private static CultureInfo CultureInfoInvariant => CultureInfo.InvariantCulture;
}

public readonly record struct ParseResult
{
    private ParseResult(MessageKind kind, object? message, ProtocolError? error)
    {
        Kind = kind;
        Message = message;
        Error = error;
    }

    public MessageKind Kind { get; }

    public object? Message { get; }

    public ProtocolError? Error { get; }

    public bool IsSuccess => Error is null;

    public static ParseResult Success(MessageKind kind, object message) => new(kind, message, null);

    public static ParseResult Failure(ProtocolError error) => new(MessageKind.Unknown, null, error);
}
