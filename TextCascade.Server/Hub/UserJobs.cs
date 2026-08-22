namespace TextCascade.Server;

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

