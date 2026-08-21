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
    public DateTimeOffset LastActivityAt => new(new DateTime(Interlocked.Read(ref lastActivityTicks), DateTimeKind.Utc));

    private readonly object connectionsGate = new();
    private readonly List<ConnectionContext> connections = new();
    private readonly RuntimeConfig config;
    private Task? userLoop;

    private readonly object snapshotGate = new();
    private readonly List<ClientHello> snapshotCandidates = new();
    private int snapshotBytes;
    private readonly List<RecoveryClip> recoveryQueue = new();
    private bool recoveryWindowClosed;

    private readonly SyncServer server;
    private readonly RuntimeStateStore runtimeStateStore;
    private long lastActivityTicks;

    public UserHub(string username, RuntimeConfig config, DateTimeOffset processStart, SyncServer server, ulong initialVersion)
    {
        Username = username;
        this.config = config;
        this.server = server;
        this.runtimeStateStore = server.RuntimeStateStore;
        ProcessStartTime = processStart;
        UserChannel = Channel.CreateUnbounded<UserJob>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        ClipBucket = new TokenBucket(config.RateLimit.ClipBurst, config.RateLimit.ClipTokensPerSecond, processStart);
        SeenIds = new SeenIdRing(config.Limits.SeenIdCapacity);
        Version = initialVersion;
        lastActivityTicks = processStart.UtcTicks;
    }

    public IReadOnlyList<ConnectionContext> Connections
    {
        get { lock (connectionsGate) { return connections.ToArray(); } }
    }

    public bool IsEmpty
    {
        get { lock (connectionsGate) { return connections.Count == 0; } }
    }

    internal object ScanGate => connectionsGate;
    internal List<ConnectionContext> ConnectionList => connections;
    internal RuntimeConfig Config => config;

    public void AddConnection(ConnectionContext connection)
    {
        lock (connectionsGate) { connections.Add(connection); }
        MarkActivity(DateTimeOffset.UtcNow);
        var nowUtc = DateTimeOffset.UtcNow;
        if (recoveryWindowClosed)
        {
                BroadcastToConnection(connection, Protocol.SerializeWelcome(Latest, config.Limits));
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
        bool removed;
        lock (connectionsGate) { removed = connections.Remove(connection); }
        if (removed)
        {
            MarkActivity(DateTimeOffset.UtcNow);
        }

        return removed;
    }

    public void StartIfIdle()
    {
        if (userLoop is null || userLoop.IsCompleted)
        {
            userLoop = Task.Run(async () =>
            {
                try { await RunUserLoopAsync(); }
                catch (OperationCanceledException) { }
                catch (Exception exception)
                {
                    server.Logger.LogError(
                        exception,
                        "User loop failed; rebuilding hub. username={Username}",
                        Username);
                    server.RebuildHub(this);
                }
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
                server.CancelConnection(disconnectJob.Connection, disconnectJob.Reason);
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
                var canRestoreLatest = winner.Version > Version
                    || (winner.Version == Version && Latest is null);
                if (!canRestoreLatest)
                {
                    winner = null;
                }
                else
                {
                    if (winner.Version > Version)
                    {
                        runtimeStateStore.SaveVersion(Username, winner.Version);
                    }
                    Version = winner.Version;
                    Latest = new LatestText(winner.Snapshot.Payload, winner.Version, winner.Snapshot.Hash, winner.Snapshot.Encrypted, winner.ClientId, winner.ClientName, winner.Snapshot.LocalModifiedAtUtc);
                }
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
        server.Registry.RemoveIfEmpty(this, allowDuringRecovery: true);

        MarkActivity(nowUtc);
    }

    private void BroadcastWelcome(DateTimeOffset nowUtc)
    {
        var bytes = Protocol.SerializeWelcome(Latest, config.Limits);
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

    private void MarkActivity(DateTimeOffset nowUtc)
    {
        Interlocked.Exchange(ref lastActivityTicks, nowUtc.UtcTicks);
    }

    internal void MarkActivityForScan(DateTimeOffset nowUtc) => MarkActivity(nowUtc);

    public void ApplyClip(ClientClip clip, ConnectionContext sender, DateTimeOffset nowUtc)
    {
        if (SeenIds.IsUnchangedDuplicate(clip.Id, clip.Payload, clip.Hash, clip.Encrypted, out var duplicateLatest))
        {
            var ackBytes = Protocol.SerializeClipAck(clip.Id, duplicateLatest ?? Latest ?? new LatestText(string.Empty, Version, string.Empty, false, sender.ClientId, sender.ClientName, nowUtc));
            if (!sender.State.TryEnqueueSend(ackBytes) && sender.State.MarkClosed())
            {
                sender.State.Cts.Cancel();
            }
            return;
        }

        if (SeenIds.TryGetResult(clip.Id, out _))
        {
            server.Logger.LogWarning(
                "Replacing reused clip id. username={Username} clipId={ClipId} clientId={ClientId} previousVersion={PreviousVersion}",
                Username,
                clip.Id,
                sender.ClientId,
                Version);
        }

        if (!ClipBucket.TryAcquire(nowUtc))
        {
            server.Logger.LogSecurityEvent("reject",
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
        runtimeStateStore.SaveVersion(Username, next);
        Version = next;
        var latest = new LatestText(clip.Payload, next, clip.Hash, clip.Encrypted, sender.ClientId, sender.ClientName, nowUtc);
        Latest = latest;
        SeenIds.RememberId(clip.Id, latest);
            server.Logger.LogSecurityEvent("clip",
            ("username", Username),
            ("version", latest.Version),
            ("clipId", clip.Id),
            ("bytes", Encoding.UTF8.GetByteCount(clip.Payload)),
            ("fromClientId", sender.ClientId),
            ("encrypted", clip.Encrypted));

        var broadcastBytes = Protocol.SerializeClip(clip.Id, latest);
        var deliveries = new List<string>();
        foreach (var connection in Connections)
        {
            if (ReferenceEquals(connection, sender)) continue;
            var queued = connection.State.TryEnqueueSend(broadcastBytes);
            deliveries.Add($"{connection.ClientId}:{(queued ? "queued" : "full")}");
            if (!queued && connection.State.MarkClosed())
            {
                connection.State.Cts.Cancel();
            }
        }

        server.Logger.LogInformation(
            "Clip broadcast. username={Username} version={Version} clipId={ClipId} recipients=[{Recipients}]",
            Username,
            next,
            clip.Id,
            string.Join(",", deliveries));

        var ackBytesFinal = Protocol.SerializeClipAck(clip.Id, latest);
        if (!sender.State.TryEnqueueSend(ackBytesFinal) && sender.State.MarkClosed())
        {
            sender.State.Cts.Cancel();
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

    public bool Remove(UserHub hub)
    {
        return hubs.TryRemove(new KeyValuePair<string, UserHub>(hub.Username, hub));
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
    private readonly UserRegistry registry = new();
    private readonly List<ConnectionContext> pendingHellos = new();
    private readonly object pendingGate = new();
    private readonly IPasswordHasher hasher;
    private readonly IClock clock;
    private readonly RuntimeStateStore runtimeStateStore;
    private readonly IReadOnlyDictionary<string, UserRecord> userLookup;

    public UserRegistry Registry => registry;
    public IPasswordHasher Hasher => hasher;
    public SlidingWindowLoginLimiter LoginLimiter { get; } = new();
    public IClock Clock => clock;
    public ILogger<SyncServer> Logger { get; }
    public IReadOnlyDictionary<string, UserRecord> UserLookup => userLookup;
    public DateTimeOffset ProcessStartTime { get; }
    public RuntimeConfig Config { get; }
    public RuntimeStateStore RuntimeStateStore => runtimeStateStore;

    public SyncServer(
        RuntimeConfig config,
        UsersFile users,
        RuntimeStateStore runtimeStateStore,
        IPasswordHasher hasher,
        IClock clock,
        ILogger<SyncServer> logger)
    {
        Config = config;
        userLookup = UsersFile.BuildUserLookup(users);
        this.runtimeStateStore = runtimeStateStore;
        this.hasher = hasher;
        this.clock = clock;
        Logger = logger;
        ProcessStartTime = clock.UtcNow;
    }

    public UserHub GetOrCreateHub(string username, RuntimeConfig runtimeConfig)
    {
        var initialVersion = runtimeStateStore.GetVersion(username);
        var hub = registry.GetOrAdd(username, name => new UserHub(name, runtimeConfig, ProcessStartTime, this, initialVersion));
        hub.StartIfIdle();
        return hub;
    }

    public void ScanHeartbeats(DateTimeOffset nowUtc)
    {
        var timeout = Config.Limits.HeartbeatTimeoutSeconds;
        foreach (var pair in registry.All)
        {
            var hub = pair.Value;
            List<ConnectionContext>? timedOut = null;
            lock (hub.ScanGate)
            {
                var pingInterval = TimeSpan.FromSeconds(hub.Config.Limits.HeartbeatIntervalSeconds);
                var pingBytes = Protocol.SerializePing(nowUtc);
                for (var index = hub.ConnectionList.Count - 1; index >= 0; index--)
                {
                    var connection = hub.ConnectionList[index];
                    if (!connection.State.HelloReceived && connection.State.HelloDeadline is { } deadline && nowUtc >= deadline)
                    {
                        timedOut ??= new List<ConnectionContext>();
                        timedOut.Add(connection);
                        continue;
                    }

                    if (connection.State.HelloReceived && nowUtc - connection.State.LastPingAt >= pingInterval)
                    {
                        connection.State.LastPingAt = nowUtc;
                        connection.State.MarkPingAwaitingPong();
                        if (!connection.State.TryEnqueueSend(pingBytes) && connection.State.MarkClosed())
                        {
                            connection.State.Cts.Cancel();
                        }
                    }

                    if (nowUtc - connection.State.LastSeen >= TimeSpan.FromSeconds(timeout))
                    {
                        timedOut ??= new List<ConnectionContext>();
                        timedOut.Add(connection);
                    }

                    hub.MarkActivityForScan(nowUtc);
                }
            }

            if (timedOut is not null)
            {
                foreach (var connection in timedOut)
                {
                    if (!connection.State.HelloReceived)
                    {
                        EnqueueHelloTimeout(connection);
                    }
                    else
                    {
                        CancelConnection(connection, "heartbeat_timeout");
                    }
                }
            }

            if (hub.IsEmpty && nowUtc - hub.LastActivityAt >= TimeSpan.FromMinutes(10))
            {
                registry.RemoveIfEmpty(hub, allowDuringRecovery: false);
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

    public void RebuildHub(UserHub hub)
    {
        if (!registry.Remove(hub))
        {
            return;
        }

        foreach (var connection in hub.Connections)
        {
            CancelConnection(connection, "user_loop_failed");
        }

        Registry.RemoveIfEmpty(hub, allowDuringRecovery: true);
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

    private async Task CloseAfterHelloTimeoutAsync(ConnectionContext connection)
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
            EnqueueImmediateClose(connection, "server_busy");
        }
        finally
        {
            CancelConnection(connection, "hello_timeout");
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
            Logger.LogSecurityEvent("disconnect",
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

internal sealed record ReceivedMessage(WebSocketMessageType MessageType, byte[] Payload);
