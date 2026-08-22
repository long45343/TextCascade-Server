using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TextCascade.Server;

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

    private readonly IConnectionCoordinator coordinator;
    private readonly RuntimeStateStore runtimeStateStore;
    private long lastActivityTicks;

    public UserHub(string username, RuntimeConfig config, DateTimeOffset processStart, IConnectionCoordinator coordinator, RuntimeStateStore runtimeStateStore, ulong initialVersion)
    {
        Username = username;
        this.config = config;
        this.coordinator = coordinator;
        this.runtimeStateStore = runtimeStateStore;
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
                    coordinator.Logger.LogError(
                        exception,
                        "User loop failed; rebuilding hub. username={Username}",
                        Username);
                    coordinator.RebuildHub(this);
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
                coordinator.CancelConnection(disconnectJob.Connection, disconnectJob.Reason);
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
        coordinator.RemoveEmptyHubAfterRecovery(this);

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
            coordinator.Logger.LogWarning(
                "Replacing reused clip id. username={Username} clipId={ClipId} clientId={ClientId} previousVersion={PreviousVersion}",
                Username,
                clip.Id,
                sender.ClientId,
                Version);
        }

        if (!ClipBucket.TryAcquire(nowUtc))
        {
            coordinator.Logger.LogSecurityEvent("reject",
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
            coordinator.Logger.LogSecurityEvent("clip",
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

        coordinator.Logger.LogInformation(
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



