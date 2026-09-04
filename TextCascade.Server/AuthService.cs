using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace TextCascade.Server;

public sealed class AuthService
{
    public static async Task HandleLoginAsync(HttpContext context, RuntimeConfig config, SyncServer syncServer, ILogger? logger = null)
    {
        var limiter = syncServer.LoginLimiter;
        var clock = syncServer.Clock;
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = clock.GetUtcNow();

        LoginRequest? request;
        try
        {
            request = await ParseLoginRequest(context);
        }
        catch (Exception exception) when (exception is LoginParseException or JsonException)
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
        var passwordHash = found && user is not null
            ? user.PasswordHash
            : syncServer.LoginDummyHash;
        var passwordOk = syncServer.Hasher.Verify(request.Password, passwordHash);
        if (!found || !passwordOk || user is null || user.Disabled)
        {
            logger?.LogSecurityEvent("login", ("username", request.Username), ("ip", ip), ("success", false), ("reason", "invalid_credentials"));
            await WriteError(context, 401, "invalid_credentials", "Invalid username or password.");
            return;
        }

        var authenticatedUser = user;
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

    private const int MaxLoginBodyBytes = 16384;

    // Login contract (spec §4.1): unknown fields, duplicate fields and nesting beyond
    // depth 3 are rejected; names are exact lowercase via JsonPropertyName on LoginRequest.
    private static readonly JsonSerializerOptions StrictLoginOptions = new()
    {
        MaxDepth = 3,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static async Task<LoginRequest> ParseLoginRequest(HttpContext context)
    {
        if (context.Request.ContentLength is > MaxLoginBodyBytes)
        {
            throw new LoginParseException("Request body too large.");
        }

        var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is not null
            && (bodySize.MaxRequestBodySize is null || bodySize.MaxRequestBodySize > MaxLoginBodyBytes))
        {
            bodySize.MaxRequestBodySize = MaxLoginBodyBytes;
        }

        LoginRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<LoginRequest>(context.Request.Body, StrictLoginOptions, context.RequestAborted);
        }
        catch (BadHttpRequestException)
        {
            throw new LoginParseException("Request body too large.");
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

// Exact lowercase member names: case variants must fail as unknown fields (spec §4.1).
public sealed record LoginRequest(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password);

public sealed class LoginParseException(string message) : Exception(message);
