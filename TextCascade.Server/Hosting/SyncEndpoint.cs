using Microsoft.AspNetCore.Http;

namespace TextCascade.Server;

public static class SyncEndpoint
{
    public static async Task HandleAsync(HttpContext context, RuntimeConfig config, SyncServer server)
    {
        var tokenHeader = context.Request.Headers.Authorization.ToString();
        if (!tokenHeader.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            context.Response.StatusCode = 401;
            return;
        }

        var compactToken = tokenHeader["Bearer ".Length..];
        var now = DateTimeOffset.UtcNow;
        var tokenService = new TokenService(config.TokenSecret!);
        if (!tokenService.TryVerifyToken(compactToken, now, server.UserLookup, out var payload))
        {
            context.Response.StatusCode = 401;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }

        var subProtocol = SelectSubProtocol(context.WebSockets.WebSocketRequestedProtocols);
        if (subProtocol is null)
        {
            context.Response.StatusCode = 400;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(subProtocol);
        var connectionId = Guid.NewGuid().ToString("N");
        var provisional = new ConnectionContext(connectionId, payload.Subject, "pending", "pending", socket, null!, config);
        await ConnectionHandler.RunAsync(provisional, payload, config, server);
    }

    internal static string? SelectSubProtocol(IList<string> requested)
    {
        foreach (var protocol in requested)
        {
            if (string.Equals(protocol, "textcascade.v1", StringComparison.Ordinal)) return protocol;
        }
        return null;
    }
}

