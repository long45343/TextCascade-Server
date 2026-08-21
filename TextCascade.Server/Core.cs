namespace TextCascade.Server;

public sealed class SlidingWindowLoginLimiter
{
    private readonly object gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> windows = new(StringComparer.Ordinal);

    public bool TryConsumeLoginLimit(string ip, string username, DateTimeOffset nowUtc, RuntimeConfig config)
    {
        lock (gate)
        {
            RemoveExpired(nowUtc.AddMinutes(-1));
            var ipAllowed = TryConsume($"ip:{ip}", config.RateLimit.LoginIpPerMinute, nowUtc, config.RateLimit.MaxKeys, allowNewKey: true);
            if (!ipAllowed)
            {
                return false;
            }

            var userAllowed = TryConsume($"user:{username}", config.RateLimit.LoginUserPerMinute, nowUtc, config.RateLimit.MaxKeys, allowNewKey: true);
            return ipAllowed && userAllowed;
        }
    }

    public void ResetUserLoginLimit(string username)
    {
        lock (gate)
        {
            windows.Remove($"user:{username}");
        }
    }

    private bool TryConsume(string key, int limit, DateTimeOffset nowUtc, int maxKeys, bool allowNewKey)
    {
        var cutoff = nowUtc.AddMinutes(-1);
        if (!windows.TryGetValue(key, out var queue))
        {
            if (!allowNewKey || windows.Count >= maxKeys)
            {
                RemoveExpired(cutoff);
                if (windows.Count >= maxKeys)
                {
                    return false;
                }
            }

            windows[key] = queue = new Queue<DateTimeOffset>();
        }

        while (queue.Count > 0 && queue.Peek() <= cutoff)
        {
            queue.Dequeue();
        }

        if (queue.Count >= limit)
        {
            return false;
        }

        queue.Enqueue(nowUtc);
        if (queue.Count == 0)
        {
            windows.Remove(key);
        }

        return true;
    }

    private void RemoveExpired(DateTimeOffset cutoff)
    {
        var stale = windows.Where(pair => pair.Value.Count == 0 || pair.Value.Peek() <= cutoff)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in stale)
        {
            windows.Remove(key);
        }
    }
}

public sealed class TokenBucket
{
    private readonly object gate = new();
    private double tokens;
    private DateTimeOffset lastRefill;

    public TokenBucket(int burst, double tokensPerSecond, DateTimeOffset nowUtc)
    {
        if (burst <= 0) throw new ArgumentOutOfRangeException(nameof(burst));
        if (tokensPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(tokensPerSecond));
        tokens = burst;
        Capacity = burst;
        TokensPerSecond = tokensPerSecond;
        lastRefill = nowUtc;
    }

    public int Capacity { get; }

    public double TokensPerSecond { get; }

    public bool TryAcquire(DateTimeOffset nowUtc)
    {
        lock (gate)
        {
            if (nowUtc < lastRefill)
            {
                return false;
            }

            tokens = Math.Min(Capacity, tokens + (nowUtc - lastRefill).TotalSeconds * TokensPerSecond);
            lastRefill = nowUtc;
            if (tokens < 1)
            {
                return false;
            }

            tokens -= 1;
            return true;
        }
    }
}

public sealed class SeenIdRing
{
    private readonly object gate = new();
    private readonly string?[] ids;
    private readonly LatestText?[] results;
    private int next;

    public SeenIdRing(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        ids = new string?[capacity];
        results = new LatestText?[capacity];
    }

    public bool TryDuplicate(string id)
    {
        lock (gate)
        {
            for (var i = 0; i < ids.Length; i++)
            {
                if (string.Equals(ids[i], id, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            RememberInternal(id, null);
            return false;
        }
    }

    public bool TryGetResult(string id, out LatestText? result)
    {
        lock (gate)
        {
            for (var i = 0; i < ids.Length; i++)
            {
                if (string.Equals(ids[i], id, StringComparison.Ordinal))
                {
                    result = results[i];
                    return true;
                }
            }

            result = null;
            return false;
        }
    }

    public void RememberId(string id) => RememberId(id, null);

    public void RememberId(string id, LatestText? result)
    {
        lock (gate)
        {
            RememberInternal(id, result);
        }
    }

    public bool IsUnchangedDuplicate(string id, string payload, string hash, bool encrypted, out LatestText? latest)
    {
        lock (gate)
        {
            for (var index = 0; index < ids.Length; index++)
            {
                if (!string.Equals(ids[index], id, StringComparison.Ordinal))
                {
                    continue;
                }

                latest = results[index];
                return latest is not null
                    && string.Equals(latest.Payload, payload, StringComparison.Ordinal)
                    && string.Equals(latest.Hash, hash, StringComparison.Ordinal)
                    && latest.Encrypted == encrypted;
            }

            latest = null;
            return false;
        }
    }

    private void RememberInternal(string id, LatestText? result)
    {
        ids[next] = id;
        results[next] = result;
        next = (next + 1) % ids.Length;
    }
}

public sealed record SnapshotWinner(ClipSnapshot Snapshot, ulong Version, string ClientId, string ClientName);

public static class CoreLogic
{
    public static ulong NextVersion(ulong current)
    {
        if (current == ulong.MaxValue)
        {
            throw new InvalidOperationException("Version overflow.");
        }

        return current + 1;
    }

    public static LatestText WithVersion(LatestText latest, ulong next, DateTimeOffset? nowUtc = null)
    {
        return latest with { Version = next, UpdatedAtUtc = nowUtc ?? latest.UpdatedAtUtc };
    }

    public static SnapshotWinner? SelectSnapshotWinner(IEnumerable<ClientHello> hellos)
    {
        return hellos
            .Where(hello => hello.LastServerVersion > 0 && hello.Snapshot is not null)
            .Select(hello => (hello.ClientId, hello.ClientName, Snapshot: hello.Snapshot!, hello.LastServerVersion))
            .OrderByDescending(item => item.LastServerVersion)
            .ThenByDescending(item => item.Snapshot.LocalModifiedAtUtc)
            .ThenByDescending(item => item.ClientId, StringComparer.Ordinal)
            .Select(item => new SnapshotWinner(item.Snapshot, item.LastServerVersion, item.ClientId, item.ClientName))
            .FirstOrDefault();
    }
}
