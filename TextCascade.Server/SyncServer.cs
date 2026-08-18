using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TextCascade.Server;

public sealed class ConnectionContext
{
    public string ConnectionId { get; }
    public string Username { get; }
    public string ClientId { get; }
    public string ClientName { get; }
    public WebSocket Socket { get; }
    public UserHub? Hub { get; internal set; }
    public ConnectionStateBag State { get; }

    public ConnectionContext(string connectionId, string username, string clientId, string clientName, WebSocket socket, UserHub? hub, RuntimeConfig config)
    {
        ConnectionId = connectionId;
        Username = username;
        ClientId = clientId;
        ClientName = clientName;
        Socket = socket;
        Hub = hub;
        State = new ConnectionStateBag(config);
    }
}

public sealed class ConnectionStateBag
{
    private readonly object gate = new();
    private DateTimeOffset lastSeen;
    private DateTimeOffset lastPingAt;
    private bool closed;
    private bool helloTimeoutStarted;
    private bool pongAwaited;
    public Channel<byte[]> SendQueue { get; }
    public CancellationTokenSource Cts { get; }
    public bool HelloReceived { get; internal set; }
    public DateTimeOffset? HelloDeadline { get; internal set; }

    public DateTimeOffset LastSeen
    {
        get { lock (gate) { return lastSeen; } }
        internal set { lock (gate) { lastSeen = value; } }
    }

    public DateTimeOffset LastPingAt
    {
        get { lock (gate) { return lastPingAt; } }
        internal set { lock (gate) { lastPingAt = value; } }
    }

    public void MarkPingAwaitingPong()
    {
        lock (gate) { pongAwaited = true; }
    }

    public bool TryTakePongAwaiting()
    {
        lock (gate)
        {
            if (!pongAwaited) return false;
            pongAwaited = false;
            return true;
        }
    }

    public bool IsClosed
    {
        get { lock (gate) { return closed; } }
    }

    public bool MarkClosed()
    {
        lock (gate)
        {
            if (closed) return false;
            closed = true;
            return true;
        }
    }

    public bool TryStartHelloTimeout()
    {
        lock (gate)
        {
            if (helloTimeoutStarted || closed)
            {
                return false;
            }

            helloTimeoutStarted = true;
            return true;
        }
    }

    public ConnectionStateBag(RuntimeConfig config)
    {
        lastSeen = DateTimeOffset.UtcNow;
        lastPingAt = lastSeen;
        SendQueue = Channel.CreateBounded<byte[]>(config.Limits.SendQueueCapacity);
        Cts = new CancellationTokenSource();
        HelloDeadline = DateTimeOffset.UtcNow.AddSeconds(config.Limits.HelloTimeoutSeconds);
    }

    public bool TryEnqueueSend(byte[] payload)
    {
        return SendQueue.Writer.TryWrite(payload);
    }
}

public sealed class UserHub
{
    public string Username { get; }
    public LatestText? Latest { get; private set; }
    public ulong Version { get; private set; }
    public Channel<UserJob> UserChannel { get; }
    public TokenBucket ClipBucket { get; }
    public SeenIdRing SeenIds { get; }
    public DateTimeOffset ProcessStartTime { get; }

    private readonly object connectionsGate = new();
    private readonly List<ConnectionContext> connections = new();
    private readonly RuntimeConfig config;
    private Task? userLoop;

    private readonly object snapshotGate = new();
    private readonly List<ClientHello> snapshotCandidates = new();
    private int snapshotBytes;
    private readonly List<RecoveryClip> recoveryQueue = new();
    private bool recoveryWindowClosed;

    public UserHub(string username, RuntimeConfig config, DateTimeOffset processStart)
    {
        Username = username;
        this.config = config;
        ProcessStartTime = processStart;
        UserChannel = Channel.CreateUnbounded<UserJob>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        ClipBucket = new TokenBucket(config.RateLimit.ClipBurst, config.RateLimit.ClipTokensPerSecond, processStart);
        SeenIds = new SeenIdRing(config.Limits.SeenIdCapacity);
        Version = 0;
    }

    public IReadOnlyList<ConnectionContext> Connections
    {
        get { lock (connectionsGate) { return connections.ToArray(); } }
    }

    public bool IsEmpty
    {
        get { lock (connectionsGate) { return connections.Count == 0; } }
    }

    public void AddConnection(ConnectionContext connection)
    {
        lock (connectionsGate) { connections.Add(connection); }
        var nowUtc = DateTimeOffset.UtcNow;
        if (recoveryWindowClosed)
        {
            BroadcastToConnection(connection, Protocol.SerializeWelcome(Latest));
            return;
        }

        EnsureRecoveryWindowClosed(nowUtc);
        if (recoveryWindowClosed)
        {
            return;
        }
    }

    public bool RemoveConnection(ConnectionContext connection)
    {
        lock (connectionsGate) { return connections.Remove(connection); }
    }

    public void StartIfIdle(Func<UserHub, Task> processor)
    {
        if (userLoop is null || userLoop.IsCompleted)
        {
            userLoop = Task.Run(async () =>
            {
                try { await processor(this); }
                catch (OperationCanceledException) { }
                catch (Exception) { }
            });
        }
    }

    public bool TryWriteJob(UserJob job) => UserChannel.Writer.TryWrite(job);

    public async Task RunUserLoopAsync(CancellationToken cancellationToken = default)
    {
        var reader = UserChannel.Reader;
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var job))
            {
                ProcessJob(job, DateTimeOffset.UtcNow);
            }
        }
    }

    private void ProcessJob(UserJob job, DateTimeOffset nowUtc)
    {
        switch (job)
        {
            case ClipJob clipJob:
                ApplyClip(clipJob.Clip, clipJob.Sender, nowUtc);
                break;
            case PongJob pongJob:
                pongJob.Connection.State.LastSeen = nowUtc;
                break;
            case HelloJob helloJob:
                helloJob.Connection.State.HelloReceived = true;
                if (helloJob.Hello.Snapshot is not null)
                {
                    AcceptSnapshot(helloJob.Hello);
                }
                break;
            case DisconnectJob disconnectJob:
                SyncServer.Instance.CancelConnection(disconnectJob.Connection, disconnectJob.Reason);
                break;
        }
    }

    public void AcceptSnapshot(ClientHello hello)
    {
        lock (snapshotGate)
        {
            if (recoveryWindowClosed) return;
            if (hello.Snapshot is null) return;
            var bytes = Encoding.UTF8.GetByteCount(hello.Snapshot.Payload);
            if (snapshotBytes + bytes > config.Limits.SnapshotTotalBytes) return;
            snapshotCandidates.Add(hello);
            snapshotBytes += bytes;
        }
    }

    public RecoveryDecision ClassifyClip(ClientClip clip, ConnectionContext connection)
    {
        lock (snapshotGate)
        {
            if (recoveryWindowClosed)
            {
                return RecoveryDecision.ProcessNow;
            }

            if (recoveryQueue.Count >= config.Limits.RecoveryClipQueueCapacity)
            {
                return RecoveryDecision.QueueFull;
            }

            recoveryQueue.Add(new RecoveryClip(clip, connection));
            return RecoveryDecision.Queued;
        }
    }

    public void CloseRecoveryWindow(DateTimeOffset nowUtc)
    {
        List<RecoveryClip> clips;
        SnapshotWinner? winner;
        lock (snapshotGate)
        {
            if (recoveryWindowClosed) return;
            recoveryWindowClosed = true;
            winner = CoreLogic.SelectSnapshotWinner(snapshotCandidates);
            if (winner is not null)
            {
                Version = winner.Version;
                Latest = new LatestText(winner.Snapshot.Payload, winner.Version, winner.Snapshot.Hash, winner.Snapshot.Encrypted, winner.ClientId, winner.ClientName, winner.Snapshot.LocalModifiedAtUtc);
            }
            clips = recoveryQueue.ToList();
            recoveryQueue.Clear();
        }

        foreach (var recovery in clips)
        {
            if (recovery.Connection.State.IsClosed) continue;
            ApplyClip(recovery.Clip, recovery.Connection, nowUtc);
        }

        BroadcastWelcome(nowUtc);

        // Spec §6.2: empty hubs that survived until the recovery window closes are now removed.
        SyncServer.Instance.Registry.RemoveIfEmpty(this, allowDuringRecovery: true);
    }

    private void BroadcastWelcome(DateTimeOffset nowUtc)
    {
        var bytes = Protocol.SerializeWelcome(Latest);
        foreach (var connection in Connections)
        {
            if (!connection.State.TryEnqueueSend(bytes) && connection.State.MarkClosed())
            {
                connection.State.Cts.Cancel();
            }
        }
    }

    public bool IsRecoveryWindowOpen(DateTimeOffset nowUtc)
    {
        return !recoveryWindowClosed && nowUtc < ProcessStartTime.AddSeconds(config.Limits.SnapshotWindowSeconds);
    }

    public void EnsureRecoveryWindowClosed(DateTimeOffset nowUtc)
    {
        if (!recoveryWindowClosed && nowUtc >= ProcessStartTime.AddSeconds(config.Limits.SnapshotWindowSeconds))
        {
            CloseRecoveryWindow(nowUtc);
        }
    }

    public void ApplyClip(ClientClip clip, ConnectionContext sender, DateTimeOffset nowUtc)
    {
        if (SeenIds.TryGetResult(clip.Id, out var duplicateLatest))
        {
            var ackBytes = Protocol.SerializeClipAck(clip.Id, duplicateLatest ?? Latest ?? new LatestText(string.Empty, Version, string.Empty, false, sender.ClientId, sender.ClientName, nowUtc));
            if (!sender.State.TryEnqueueSend(ackBytes) && sender.State.MarkClosed())
            {
                sender.State.Cts.Cancel();
            }
            return;
        }

        if (!ClipBucket.TryAcquire(nowUtc))
        {
            SyncServer.Instance.Logger?.LogSecurityEvent("reject",
                ("username", Username),
                ("code", "rate_limited"),
                ("bytes", Encoding.UTF8.GetByteCount(clip.Payload)));
            var error = Protocol.SerializeProtocolError(new ProtocolError(ProtocolErrorCode.RateLimited, "Clip rate limited.", clip.Id));
            if (!sender.State.TryEnqueueSend(error) && sender.State.MarkClosed())
            {
                sender.State.Cts.Cancel();
            }
            return;
        }

        var next = CoreLogic.NextVersion(Version);
        Version = next;
        var latest = new LatestText(clip.Payload, next, clip.Hash, clip.Encrypted, sender.ClientId, sender.ClientName, nowUtc);
        Latest = latest;
        SeenIds.RememberId(clip.Id, latest);
        SyncServer.Instance.Logger?.LogSecurityEvent("clip",
            ("username", Username),
            ("version", latest.Version),
            ("bytes", Encoding.UTF8.GetByteCount(clip.Payload)),
            ("fromClientId", sender.ClientId),
            ("encrypted", clip.Encrypted));

        var broadcastBytes = Protocol.SerializeClip(clip.Id, latest);
        foreach (var connection in Connections)
        {
            if (ReferenceEquals(connection, sender)) continue;
            if (!connection.State.TryEnqueueSend(broadcastBytes) && connection.State.MarkClosed())
            {
                connection.State.Cts.Cancel();
            }
        }

        var ackBytesFinal = Protocol.SerializeClipAck(clip.Id, latest);
        if (!sender.State.TryEnqueueSend(ackBytesFinal) && sender.State.MarkClosed())
        {
            sender.State.Cts.Cancel();
        }
    }

    public void EnqueuePing(DateTimeOffset nowUtc)
    {
        var bytes = Protocol.SerializePing(nowUtc);
        foreach (var connection in Connections)
        {
            if (!connection.State.HelloReceived
                || nowUtc - connection.State.LastPingAt < TimeSpan.FromSeconds(config.Limits.HeartbeatIntervalSeconds))
            {
                continue;
            }

            connection.State.LastPingAt = nowUtc;
            connection.State.MarkPingAwaitingPong();
            if (!connection.State.TryEnqueueSend(bytes) && connection.State.MarkClosed())
            {
                connection.State.Cts.Cancel();
            }
        }
    }

    private static void BroadcastToConnection(ConnectionContext connection, byte[] payload)
    {
        if (!connection.State.TryEnqueueSend(payload) && connection.State.MarkClosed())
        {
            try { connection.State.Cts.Cancel(); } catch (Exception) { }
        }
    }

    public void BroadcastAsync(byte[] payload)
    {
        foreach (var connection in Connections)
        {
            if (connection.State.IsClosed) continue;
            if (!connection.State.TryEnqueueSend(payload) && connection.State.MarkClosed())
            {
                try { connection.State.Cts.Cancel(); } catch (Exception) { }
            }
        }
    }
}

public readonly record struct RecoveryClip(ClientClip Clip, ConnectionContext Connection);

public enum RecoveryDecision
{
    Queued,
    ProcessNow,
    QueueFull,
}

public abstract record UserJob;

public sealed record ClipJob(ConnectionContext Sender, ClientClip Clip) : UserJob;

public sealed record HelloJob(ConnectionContext Connection, ClientHello Hello) : UserJob;

public sealed record PongJob(ConnectionContext Connection, ClientPong Pong) : UserJob;

public sealed record DisconnectJob(ConnectionContext Connection, string Reason) : UserJob;

public sealed class UserRegistry
{
    private readonly ConcurrentDictionary<string, UserHub> hubs = new(StringComparer.Ordinal);
    public IEnumerable<KeyValuePair<string, UserHub>> All => hubs;

    public UserHub GetOrAdd(string username, Func<string, UserHub> factory)
    {
        return hubs.GetOrAdd(username, factory);
    }

    public bool TryGetValue(string username, out UserHub hub) => hubs.TryGetValue(username, out hub!);

    public void RemoveIfEmpty(UserHub hub, bool allowDuringRecovery)
    {
        if (!hub.IsEmpty) return;
        if (!allowDuringRecovery && hub.IsRecoveryWindowOpen(DateTimeOffset.UtcNow)) return;
        hubs.TryRemove(hub.Username, out _);
    }
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class SyncServer
{
    public static SyncServer Instance { get; } = new();

    private UserRegistry registry = new();
    private readonly List<ConnectionContext> pendingHellos = new();
    private readonly object pendingGate = new();

    public UserRegistry Registry => registry;
    public IPasswordHasher Hasher { get; set; } = new Argon2PasswordHasher();
    public SlidingWindowLoginLimiter LoginLimiter { get; } = new();
    public IClock Clock { get; set; } = new SystemClock();
    public ILogger? Logger { get; set; }
    public IReadOnlyDictionary<string, UserRecord> UserLookup { get; set; } = new Dictionary<string, UserRecord>(StringComparer.Ordinal);
    public DateTimeOffset ProcessStartTime { get; set; } = DateTimeOffset.UtcNow;
    public RuntimeConfig Config { get; private set; } = TextCascade.Server.Config.CreateDefaultConfig();

    public void Initialize(RuntimeConfig config, UsersFile users)
    {
        Config = config;
        registry = new UserRegistry();
        UserLookup = UsersFile.BuildUserLookup(users);
        ProcessStartTime = DateTimeOffset.UtcNow;
    }

    public UserHub GetOrCreateHub(string username, RuntimeConfig runtimeConfig)
    {
        var hub = registry.GetOrAdd(username, name => new UserHub(name, runtimeConfig, ProcessStartTime));
        hub.StartIfIdle(hub => hub.RunUserLoopAsync());
        return hub;
    }

    public void ScanHeartbeats(DateTimeOffset nowUtc)
    {
        var timeout = RuntimeConfigAccessor.Current?.Limits.HeartbeatTimeoutSeconds ?? 60;
        foreach (var pair in registry.All)
        {
            pair.Value.EnqueuePing(nowUtc);
            foreach (var connection in pair.Value.Connections)
            {
                if (!connection.State.HelloReceived && connection.State.HelloDeadline is { } deadline && nowUtc >= deadline)
                {
                    EnqueueHelloTimeout(connection);
                    continue;
                }
                var elapsed = nowUtc - connection.State.LastSeen;
                if (elapsed.TotalSeconds >= timeout)
                {
                    CancelConnection(connection, "heartbeat_timeout");
                }
            }
        }

        List<ConnectionContext> expired = new();
        lock (pendingGate)
        {
            foreach (var pending in pendingHellos)
            {
                if (pending.State.HelloReceived) continue;
                if (pending.State.HelloDeadline is { } deadline && nowUtc >= deadline)
                {
                    expired.Add(pending);
                }
            }
        }
        foreach (var connection in expired)
        {
            EnqueueHelloTimeout(connection);
        }
    }

    public void RegisterPendingHello(ConnectionContext connection)
    {
        lock (pendingGate) { pendingHellos.Add(connection); }
    }

    public void UnregisterPendingHello(ConnectionContext connection)
    {
        lock (pendingGate) { pendingHellos.Remove(connection); }
    }

    private void EnqueueHelloTimeout(ConnectionContext connection)
    {
        if (!connection.State.TryStartHelloTimeout())
        {
            return;
        }

        UnregisterPendingHello(connection);
        _ = CloseAfterHelloTimeoutAsync(connection);
    }

    private static async Task CloseAfterHelloTimeoutAsync(ConnectionContext connection)
    {
        try
        {
            if (connection.Socket.State == WebSocketState.Open)
            {
                var error = Protocol.SerializeProtocolError(new ProtocolError(ProtocolErrorCode.HelloTimeout, "Hello timeout.", null));
                await connection.Socket.SendAsync(error, WebSocketMessageType.Text, true, CancellationToken.None);
                await Task.Delay(100);
                await connection.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "hello_timeout", CancellationToken.None);
            }
        }
        catch (Exception)
        {
            Instance.EnqueueImmediateClose(connection, "server_busy");
        }
        finally
        {
            Instance.CancelConnection(connection, "hello_timeout");
        }
    }

    public void CancelConnection(ConnectionContext connection, string reason)
    {
        UnregisterPendingHello(connection);
        if (!connection.State.MarkClosed()) return;
        try { connection.State.Cts.Cancel(); } catch (Exception) { }
        connection.Hub?.RemoveConnection(connection);
        if (connection.Hub is not null)
        {
            Logger?.LogSecurityEvent("disconnect",
                ("username", connection.Username),
                ("clientId", connection.ClientId),
                ("connectionId", connection.ConnectionId),
                ("reason", reason));
        }
    }

    public void EnqueueImmediateClose(ConnectionContext connection, string reason)
    {
        // Sending queue is congested: do not flush an error or a close frame.
        // Abort/dispose the underlying socket and funnel into the unified cancellation path.
        if (!connection.State.MarkClosed()) return;
        try { connection.State.Cts.Cancel(); } catch (Exception) { }
        try { connection.Socket.Abort(); } catch (Exception) { }
        connection.Hub?.RemoveConnection(connection);
    }

    public async Task ShutdownAsync(TimeSpan drain, DateTimeOffset nowUtc)
    {
        var bye = Protocol.SerializeBye("server_shutdown");
        var closeTasks = new List<Task>();
        foreach (var pair in registry.All)
        {
            foreach (var connection in pair.Value.Connections)
            {
                if (connection.State.IsClosed) continue;
                if (connection.State.TryEnqueueSend(bye))
                {
                    closeTasks.Add(CloseConnectionAsync(connection, WebSocketCloseStatus.EndpointUnavailable, "server_shutdown"));
                }
            }
        }

        // Spec §7: after broadcasting bye, close with 1001 first, then wait the short drain.
        await Task.WhenAll(closeTasks);
        await Task.Delay(drain);
        foreach (var pair in registry.All)
        {
            foreach (var connection in pair.Value.Connections)
            {
                CancelConnection(connection, "server_shutdown");
            }
        }
    }

    private static async Task CloseConnectionAsync(ConnectionContext connection, WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (connection.Socket.State == WebSocketState.Open)
            {
                await connection.Socket.CloseAsync(status, reason, CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // The cancellation path below still aborts/disposes the socket.
        }
    }
}

public static class SyncEndpoint
{
    public static async Task HandleAsync(HttpContext context, RuntimeConfig config)
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
        if (!tokenService.TryVerifyToken(compactToken, now, SyncServer.Instance.UserLookup, out var payload))
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
        await ConnectionHandler.RunAsync(provisional, payload, config);
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

public static class ConnectionHandler
{
    public static async Task RunAsync(ConnectionContext provisional, TokenPayload payload, RuntimeConfig config)
    {
        SyncServer.Instance.RegisterPendingHello(provisional);
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
                SyncServer.Instance.CancelConnection(provisional, "closed");
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
                    "invalid_hello");
                return;
            }

            hello = (ClientHello)parse.Message!;
        }
        catch (FrameTooLargeException)
        {
            var error = Protocol.SerializeProtocolError(new ProtocolError(ProtocolErrorCode.FrameTooLarge, "frame_too_large", null));
            await SendAndClosePreHelloAsync(provisional, error, WebSocketCloseStatus.MessageTooBig, "frame_too_large");
            return;
        }
        catch (OperationCanceledException)
        {
            // Hello timeout is owned by the unified heartbeat scanner; here the socket was
            // cancelled for another reason (e.g. shutdown). Fall through to unified cleanup.
            SyncServer.Instance.CancelConnection(provisional, "cancelled");
            return;
        }
        catch (WebSocketException)
        {
            SyncServer.Instance.CancelConnection(provisional, "socket_error");
            return;
        }

        var hub = SyncServer.Instance.GetOrCreateHub(payload.Subject, config);
        var connection = new ConnectionContext(
            provisional.ConnectionId,
            payload.Subject,
            hello.ClientId,
            hello.ClientName,
            provisional.Socket,
            hub,
            config);
        hub.AddConnection(connection);
        SyncServer.Instance.Logger?.LogSecurityEvent("connect",
            ("username", connection.Username),
            ("clientId", connection.ClientId),
            ("connectionId", connection.ConnectionId));
        connection.State.HelloReceived = true;
        connection.State.LastSeen = DateTimeOffset.UtcNow;
        SyncServer.Instance.UnregisterPendingHello(provisional);
        if (!hub.TryWriteJob(new HelloJob(connection, hello)))
        {
            SyncServer.Instance.CancelConnection(connection, "user_loop_unavailable");
            return;
        }

        var sendTask = ConnectionSendLoopAsync(connection);
        var readTask = ReadLoopAsync(connection, config);
        await Task.WhenAll(sendTask, readTask);
        SyncServer.Instance.CancelConnection(connection, "disconnected");
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
        string reason)
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
            SyncServer.Instance.EnqueueImmediateClose(connection, "server_busy");
        }
        finally
        {
            SyncServer.Instance.CancelConnection(connection, reason);
        }
    }

    private static async Task CloseAfterProtocolErrorAsync(
        ConnectionContext connection,
        WebSocketCloseStatus status,
        string reason)
    {
        // Give the send loop a short opportunity to flush the queued error frame.
        await Task.Delay(100);
        try
        {
            if (connection.Socket.State == WebSocketState.Open)
            {
                await connection.Socket.CloseAsync(status, reason, CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // Cancellation below is still the unified cleanup path.
        }
        finally
        {
            SyncServer.Instance.CancelConnection(connection, reason);
        }
    }

    private static async Task ReadLoopAsync(ConnectionContext connection, RuntimeConfig config)
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
                    await SendSafeAsync(connection, oversized);
                    await Task.Delay(100, connection.State.Cts.Token);
                    if (connection.Socket.State == WebSocketState.Open)
                    {
                        await connection.Socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame_too_large", CancellationToken.None);
                    }
                    SyncServer.Instance.CancelConnection(connection, "frame_too_large");
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
                    await SendSafeAsync(connection, error);
                    await connection.Socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame_too_large", CancellationToken.None);
                    SyncServer.Instance.CancelConnection(connection, "frame_too_large");
                    break;
                }

                var parse = Protocol.ParseClientMessage(received.Payload, config);
                if (!parse.IsSuccess)
                {
                    SyncServer.Instance.Logger?.LogSecurityEvent("reject",
                        ("username", connection.Username),
                        ("code", parse.Error?.CodeName ?? "invalid_message"),
                        ("bytes", received.Payload.Length));
                    var error = Protocol.SerializeProtocolError(parse.Error!);
                    await SendSafeAsync(connection, error);
                    continue;
                }

                switch (parse.Kind)
                {
                    case MessageKind.Clip:
                        var clip = (ClientClip)parse.Message!;
                        var hub = connection.Hub;
                        if (hub is null)
                        {
                            SyncServer.Instance.CancelConnection(connection, "user_loop_unavailable");
                            break;
                        }

                        var decision = hub.ClassifyClip(clip, connection);
                        if (decision == RecoveryDecision.QueueFull)
                        {
                            SyncServer.Instance.CancelConnection(connection, "recovery_queue_full");
                        }
                        else if (decision == RecoveryDecision.ProcessNow
                                 && !hub.TryWriteJob(new ClipJob(connection, clip)))
                        {
                            SyncServer.Instance.CancelConnection(connection, "user_loop_unavailable");
                        }
                        break;
                    case MessageKind.Pong:
                        if (!connection.State.TryTakePongAwaiting())
                        {
                            var unsolicitedPong = Protocol.SerializeProtocolError(new ProtocolError(
                                ProtocolErrorCode.InvalidMessage,
                                "Pong received without an outstanding ping.",
                                null));
                            await SendSafeAsync(connection, unsolicitedPong);
                            continue;
                        }

                        if (connection.Hub is null || !connection.Hub.TryWriteJob(new PongJob(connection, (ClientPong)parse.Message!)))
                        {
                            SyncServer.Instance.CancelConnection(connection, "user_loop_unavailable");
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

    private static async Task SendSafeAsync(ConnectionContext connection, byte[] payload)
    {
        if (!connection.State.TryEnqueueSend(payload))
        {
            SyncServer.Instance.EnqueueImmediateClose(connection, "server_busy");
            return;
        }
    }
}

internal sealed class FrameTooLargeException : Exception;

internal sealed record ReceivedMessage(WebSocketMessageType MessageType, byte[] Payload);
