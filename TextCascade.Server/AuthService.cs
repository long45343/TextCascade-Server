using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TextCascade.Server;

public sealed class AuthService
{
    public static async Task HandleLoginAsync(HttpContext context, RuntimeConfig config, SyncServer syncServer, ILogger? logger = null)
    {
        var limiter = syncServer.LoginLimiter;
        var clock = syncServer.Clock;
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = clock.UtcNow;

        LoginRequest? request;
        try
        {
            request = await ParseLoginRequest(context);
        }
        catch (Exception exception) when (exception is LoginParseException or JsonException or DecoderFallbackException)
        {
            await WriteError(context, 400, "invalid_request", exception.Message);
            return;
        }

        if (!limiter.TryConsumeLoginLimit(ip, request.Username, now, config))
        {
            logger?.LogSecurityEvent("login", ("username", request.Username), ("ip", ip), ("success", false), ("reason", "rate_limited"));
            await WriteError(context, 429, "rate_limited", "Too many login attempts.");
            return;
        }

        var userLookup = syncServer.UserLookup;
        var found = userLookup.TryGetValue(request.Username, out var user);
        var passwordOk = found && user is not null && syncServer.Hasher.Verify(request.Password, user.PasswordHash);
        if (!passwordOk)
        {
            logger?.LogSecurityEvent("login", ("username", request.Username), ("ip", ip), ("success", false), ("reason", "invalid_credentials"));
            await WriteError(context, 401, "invalid_credentials", "Invalid username or password.");
            return;
        }

        var authenticatedUser = user!;
        if (authenticatedUser.Disabled)
        {
            logger?.LogSecurityEvent("login", ("username", request.Username), ("ip", ip), ("success", false), ("reason", "disabled"));
            await WriteError(context, 401, "invalid_credentials", "Invalid username or password.");
            return;
        }

        limiter.ResetUserLoginLimit(request.Username);
        logger?.LogSecurityEvent("login", ("username", request.Username), ("ip", ip), ("success", true));

        // Spec §4.1: on parameter drift, emit a structured rehash warning rather than rewriting users.json.
        bool needsRehash = false;
        if (syncServer.Hasher.NeedsRehash(authenticatedUser.PasswordHash, Cli.CreateArgon2Config(config)))
        {
            needsRehash = true;
            logger?.LogWarning("Argon2 password hash needs rehash for user {Username}; users.json was not rewritten.", authenticatedUser.Username);
        }

        var tokenService = new TokenService(config.TokenSecret!);
        var token = tokenService.CreateToken(authenticatedUser, now, TimeSpan.FromDays(config.Auth.TokenTtlDays));
        var bytes = Protocol.SerializeLoginResponse(token, config, needsRehash);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.BodyWriter.WriteAsync(bytes);
    }

    private static async Task<LoginRequest> ParseLoginRequest(HttpContext context)
    {
        if (context.Request.ContentLength is > 16384)
        {
            throw new LoginParseException("Request body too large.");
        }

        using var body = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(), context.RequestAborted);
            if (read == 0) break;
            if (body.Length + read > 16384)
            {
                throw new LoginParseException("Request body too large.");
            }

            body.Write(buffer, 0, read);
        }

        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(body.ToArray());

        using var document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 3,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !HasUniqueProperties(document.RootElement, "username", "password"))
        {
            throw new LoginParseException("Invalid login request.");
        }

        LoginRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<LoginRequest>(text, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = false,
                ReadCommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 3,
            });
        }
        catch (JsonException)
        {
            throw new LoginParseException("Invalid JSON.");
        }

        if (request is null || string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            throw new LoginParseException("Missing username or password.");
        }

        return request;
    }

    private static bool HasUniqueProperties(JsonElement element, params string[] known)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!known.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task WriteError(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { error = code, message }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await context.Response.BodyWriter.WriteAsync(payload);
    }

    public static string CreateLoginFailure() => "invalid_credentials";

    public static string CreateRateLimitResult() => "rate_limited";
}

public sealed record LoginRequest(string Username, string Password);

public sealed class LoginParseException(string message) : Exception(message);
