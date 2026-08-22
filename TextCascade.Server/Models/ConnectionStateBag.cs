using System.Threading.Channels;

namespace TextCascade.Server;

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

