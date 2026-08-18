using Microsoft.Extensions.Logging;

namespace TextCascade.Server;

public static class SecurityLogger
{
    public static void LogSecurityEvent(
        this ILogger logger,
        string eventName,
        params (string Key, object? Value)[] fields)
    {
        var pairs = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            pairs[key] = value;
        }

        using var scope = logger.BeginScope(pairs);
        logger.LogInformation("security_event {EventName} {Fields}", eventName, RedactFields(pairs));
    }

    private static string RedactFields(Dictionary<string, object?> pairs)
    {
        var parts = new List<string>();
        foreach (var pair in pairs)
        {
            var value = pair.Key switch
            {
                "password" or "token" or "secret" => "<redacted>",
                "authorization" => "<redacted>",
                "passwordHash" => "<redacted>",
                "payload" or "hash" => pair.Value is string s ? $"<{s.Length} chars>" : "<redacted>",
                _ when pair.Value is string text => RedactSensitive(pair.Key, text),
                _ => pair.Value?.ToString() ?? "<null>",
            };
            parts.Add($"{pair.Key}={value}");
        }

        return string.Join(" ", parts);
    }

    public static string RedactSensitive(string key, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (key is "password" or "token" or "secret" or "authorization" or "passwordHash" or "payload" or "hash")
        {
            return "<redacted>";
        }

        return value;
    }

    public static string TokenPrefix(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return string.Empty;
        }

        return token.Length <= 8 ? new string('*', token.Length) : token[..8];
    }
}
