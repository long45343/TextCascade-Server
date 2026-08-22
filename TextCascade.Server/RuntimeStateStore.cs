using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace TextCascade.Server;

public sealed record RuntimeStateEntry(string Username, ulong Version);

internal sealed record RuntimeStateFile(IReadOnlyList<RuntimeStateEntry> Entries);

public sealed class RuntimeStateStore : IDisposable
{
    private readonly string path;
    private readonly ILogger? logger;
    private readonly ConcurrentDictionary<string, ulong> versions;
    private int isDirty;
    private readonly object writeGate = new();

    private readonly PeriodicTimer? flushTimer;
    private readonly CancellationTokenSource? cts;
    private readonly Task? flushLoopTask;
    private bool disposed;

    public RuntimeStateStore(
        string path,
        TimeSpan? flushInterval = null,
        ILogger<RuntimeStateStore>? logger = null)
    {
        this.path = path;
        this.logger = logger;
        this.versions = new ConcurrentDictionary<string, ulong>(Load(path), StringComparer.Ordinal);

        var interval = flushInterval ?? TimeSpan.FromSeconds(5);
        if (interval > TimeSpan.Zero)
        {
            flushTimer = new PeriodicTimer(interval);
            cts = new CancellationTokenSource();
            flushLoopTask = Task.Run(() => RunFlushLoopAsync(cts.Token));
        }
    }

    public ulong GetVersion(string username)
    {
        return versions.TryGetValue(username, out var version) ? version : 0UL;
    }

    public void SaveVersion(string username, ulong version)
    {
        versions.AddOrUpdate(
            username,
            static (_, newVer) => newVer,
            static (_, current, newVer) => newVer > current ? newVer : current,
            version);
        Volatile.Write(ref isDirty, 1);
    }

    public bool Flush()
    {
        if (Interlocked.Exchange(ref isDirty, 0) == 0)
        {
            return false;
        }

        lock (writeGate)
        {
            try
            {
                var entries = versions
                    .Select(pair => new RuntimeStateEntry(pair.Key, pair.Value))
                    .OrderBy(pair => pair.Username, StringComparer.Ordinal)
                    .ToList();
                WriteAtomic(path, entries);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Volatile.Write(ref isDirty, 1);
                logger?.LogWarning(exception, "Failed to write runtime state file; will retry in next flush cycle. path={Path}", path);
                return false;
            }
        }
    }

    private async Task RunFlushLoopAsync(CancellationToken cancellationToken)
    {
        if (flushTimer is null) return;
        try
        {
            while (await flushTimer.WaitForNextTickAsync(cancellationToken))
            {
                Flush();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (cts is not null)
        {
            cts.Cancel();
            try { flushLoopTask?.GetAwaiter().GetResult(); } catch { }
            cts.Dispose();
        }

        flushTimer?.Dispose();
        Flush();
    }

    private static Dictionary<string, ulong> Load(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, ulong>(StringComparer.Ordinal);
        }

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                ReadCommentHandling = JsonCommentHandling.Disallow,
            };
            using var stream = File.OpenRead(path);
            var state = JsonSerializer.Deserialize<RuntimeStateFile>(stream, options);
            if (state is null)
            {
                throw new JsonException("State file is empty.");
            }

            var result = new Dictionary<string, ulong>(StringComparer.Ordinal);
            foreach (var entry in state.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Username) || entry.Version == 0 || !result.TryAdd(entry.Username, entry.Version))
                {
                    throw new InvalidOperationException("State file contains duplicate, empty, or zero versions.");
                }
            }

            return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Invalid runtime state file '{path}': {exception.Message}", exception);
        }
    }

    private static void WriteAtomic(string path, IReadOnlyList<RuntimeStateEntry> entries)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };
        var json = JsonSerializer.Serialize(new RuntimeStateFile(entries), options);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temporary, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Move(temporary, path, overwrite: true);
                }
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}