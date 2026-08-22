using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TextCascade.Server;

public static class ConnectionHandler
{
    public static async Task RunAsync(ConnectionContext provisional, TokenPayload payload, RuntimeConfig config, SyncServer server)
    {
        server.RegisterPendingHello(provisional);
        ClientHello hello;
        try
        {
            var received = await ReceiveFrameAsync(provisional, config.Limits.MaxFrameBytes, provisional.State.Cts.Token);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                await provisional.Socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "client_closed",
                    CancellationToken.None);
                server.CancelConnection(provisional, "closed");
                return;
            }

            var parse = Protocol.ParseClientMessage(received.Payload, config);
            if (!parse.IsSuccess || parse.Kind != MessageKind.Hello)
            {
                var error = Protocol.SerializeProtocolError(new ProtocolError(
                    ProtocolErrorCode.InvalidMessage,
                    "Expected a valid hello message.",
                    parse.Error?.ReferenceId));
                await SendAndClosePreHelloAsync(
                    provisional,
                    error,
                    WebSocketCloseStatus.PolicyViolation,
                    "invalid_hello",
                    server);
                return;
            }

            hello = (ClientHello)parse.Message!;
        }
        catch (FrameTooLargeException)
        {
            var error = Protocol.SerializeProtocolError(new ProtocolError(ProtocolErrorCode.FrameTooLarge, "frame_too_large", null));
            await SendAndClosePreHelloAsync(provisional, error, WebSocketCloseStatus.MessageTooBig, "frame_too_large", server);
            return;
        }
        catch (OperationCanceledException)
        {
            // Hello timeout is owned by the unified heartbeat scanner; here the socket was
            // cancelled for another reason (e.g. shutdown). Fall through to unified cleanup.
            server.CancelConnection(provisional, "cancelled");
            return;
        }
        catch (WebSocketException)
        {
            server.CancelConnection(provisional, "socket_error");
            return;
        }

        var hub = server.GetOrCreateHub(payload.Subject, config);
        var connection = new ConnectionContext(
            provisional.ConnectionId,
            payload.Subject,
            hello.ClientId,
            hello.ClientName,
            provisional.Socket,
            hub,
            config);
        hub.AddConnection(connection);
        server.Logger.LogSecurityEvent("connect",
            ("username", connection.Username),
            ("clientId", connection.ClientId),
            ("connectionId", connection.ConnectionId));
        connection.State.HelloReceived = true;
        connection.State.LastSeen = DateTimeOffset.UtcNow;
        server.UnregisterPendingHello(provisional);
        if (!hub.TryWriteJob(new HelloJob(connection, hello)))
        {
            server.CancelConnection(connection, "user_loop_unavailable");
            return;
        }

        var sendTask = ConnectionSendLoopAsync(connection);
        var readTask = ReadLoopAsync(connection, config, server);
        await Task.WhenAll(sendTask, readTask);
        server.CancelConnection(connection, "disconnected");
    }

    private static async Task<ReceivedMessage> ReceiveFrameAsync(
        ConnectionContext connection,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(Math.Min(maxBytes, 16 * 1024));
        var buffer = new byte[Math.Min(maxBytes, 16 * 1024)];
        while (true)
        {
            var received = await connection.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (received.Count > maxBytes - stream.Length)
            {
                throw new FrameTooLargeException();
            }

            stream.Write(buffer, 0, received.Count);
            if (received.EndOfMessage)
            {
                return new ReceivedMessage(received.MessageType, stream.ToArray());
            }
        }
    }

    private static async Task SendAndClosePreHelloAsync(
        ConnectionContext connection,
        byte[] error,
        WebSocketCloseStatus status,
        string reason,
        SyncServer server)
    {
        try
        {
            if (connection.Socket.State == WebSocketState.Open)
            {
                await connection.Socket.SendAsync(error, WebSocketMessageType.Text, true, CancellationToken.None);
                await connection.Socket.CloseAsync(status, reason, CancellationToken.None);
            }
        }
        catch (Exception)
        {
            server.EnqueueImmediateClose(connection, "server_busy");
        }
        finally
        {
            server.CancelConnection(connection, reason);
        }
    }

    private static async Task ReadLoopAsync(ConnectionContext connection, RuntimeConfig config, SyncServer server)
    {
        try
        {
            while (connection.State.Cts.IsCancellationRequested == false && connection.Socket.State == WebSocketState.Open)
            {
                ReceivedMessage received;
                try
                {
                    received = await ReceiveFrameAsync(connection, config.Limits.MaxFrameBytes, connection.State.Cts.Token);
                }
                catch (FrameTooLargeException)
                {
                    var oversized = Protocol.SerializeProtocolError(new ProtocolError(ProtocolErrorCode.FrameTooLarge, "frame_too_large", null));
                    await SendSafeAsync(connection, oversized, server);
                    await Task.Delay(100, connection.State.Cts.Token);
                    if (connection.Socket.State == WebSocketState.Open)
                    {
                        await connection.Socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame_too_large", CancellationToken.None);
                    }
                    server.CancelConnection(connection, "frame_too_large");
                    break;
                }

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    try
                    {
                        await connection.Socket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "client_closed",
                            CancellationToken.None);
                    }
                    catch (WebSocketException) { }
                    break;
                }

                if (!Protocol.CheckFrameSize(received.Payload.Length, config))
                {
                    var error = Protocol.SerializeProtocolError(new ProtocolError(ProtocolErrorCode.FrameTooLarge, "frame_too_large", null));
                    await SendSafeAsync(connection, error, server);
                    await connection.Socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame_too_large", CancellationToken.None);
                    server.CancelConnection(connection, "frame_too_large");
                    break;
                }

                var parse = Protocol.ParseClientMessage(received.Payload, config);
                if (!parse.IsSuccess)
                {
                    server.Logger.LogSecurityEvent("reject",
                        ("username", connection.Username),
                        ("code", parse.Error?.CodeName ?? "invalid_message"),
                        ("bytes", received.Payload.Length));
                    var error = Protocol.SerializeProtocolError(parse.Error!);
                    await SendSafeAsync(connection, error, server);
                    continue;
                }

                switch (parse.Kind)
                {
                    case MessageKind.Clip:
                        var clip = (ClientClip)parse.Message!;
                        var hub = connection.Hub;
                        if (hub is null)
                        {
                            server.CancelConnection(connection, "user_loop_unavailable");
                            break;
                        }

                        var decision = hub.ClassifyClip(clip, connection);
                        if (decision == RecoveryDecision.QueueFull)
                        {
                            server.CancelConnection(connection, "recovery_queue_full");
                        }
                        else if (decision == RecoveryDecision.ProcessNow
                                 && !hub.TryWriteJob(new ClipJob(connection, clip)))
                        {
                            server.CancelConnection(connection, "user_loop_unavailable");
                        }
                        break;
                    case MessageKind.Pong:
                        if (!connection.State.TryTakePongAwaiting())
                        {
                            var unsolicitedPong = Protocol.SerializeProtocolError(new ProtocolError(
                                ProtocolErrorCode.InvalidMessage,
                                "Pong received without an outstanding ping.",
                                null));
                            await SendSafeAsync(connection, unsolicitedPong, server);
                            continue;
                        }

                        if (connection.Hub is null || !connection.Hub.TryWriteJob(new PongJob(connection, (ClientPong)parse.Message!)))
                        {
                            server.CancelConnection(connection, "user_loop_unavailable");
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private static async Task ConnectionSendLoopAsync(ConnectionContext connection)
    {
        try
        {
            await foreach (var payload in connection.State.SendQueue.Reader.ReadAllAsync(connection.State.Cts.Token))
            {
                await connection.Socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, connection.State.Cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private static async Task SendSafeAsync(ConnectionContext connection, byte[] payload, SyncServer server)
    {
        if (!connection.State.TryEnqueueSend(payload))
        {
            server.EnqueueImmediateClose(connection, "server_busy");
            return;
        }
    }
}



internal sealed class FrameTooLargeException : Exception;
