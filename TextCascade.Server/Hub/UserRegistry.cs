using System.Collections.Concurrent;

namespace TextCascade.Server;

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

