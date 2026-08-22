using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TextCascade.Server;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class SyncServer : IConnectionCoordinator
{
    private readonly UserRegistry registry = new();
    private readonly List<ConnectionContext> pendingHellos = new();
    private readonly object pendingGate = new();
    private readonly IPasswordHasher hasher;
    private readonly IClock clock;
    private readonly RuntimeStateStore runtimeStateStore;
    private IReadOnlyDictionary<string, UserRecord> userLookup;
    private readonly string loginDummyHash;

    public UserRegistry Registry => registry;
    public IPasswordHasher Hasher => hasher;
    public SlidingWindowLoginLimiter LoginLimiter { get; } = new();
    public IClock Clock => clock;
    public ILogger<SyncServer> Logger { get; }
    public IReadOnlyDictionary<string, UserRecord> UserLookup => Volatile.Read(ref userLookup);
    public DateTimeOffset ProcessStartTime { get; }
    public RuntimeConfig Config { get; }
    public RuntimeStateStore RuntimeStateStore => runtimeStateStore;
    internal string LoginDummyHash => loginDummyHash;

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
        loginDummyHash = hasher.Hash(
            "textcascade-login-timing-dummy",
            Cli.CreateArgon2Config(config));
    }

    ILogger IConnectionCoordinator.Logger => Logger;

    void IConnectionCoordinator.RebuildHub(UserHub hub) => RebuildHub(hub);

    public void RemoveEmptyHubAfterRecovery(UserHub hub) => registry.RemoveIfEmpty(hub, allowDuringRecovery: true);

    public void ReplaceUserLookup(UsersFile users)
    {
        var replacement = UsersFile.BuildUserLookup(users);
        Volatile.Write(ref userLookup, replacement);
    }

    public UserHub GetOrCreateHub(string username, RuntimeConfig runtimeConfig)
    {
        var initialVersion = runtimeStateStore.GetVersion(username);
        var hub = registry.GetOrAdd(username, name => new UserHub(name, runtimeConfig, ProcessStartTime, this, runtimeStateStore, initialVersion));
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

        runtimeStateStore.Flush();
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



