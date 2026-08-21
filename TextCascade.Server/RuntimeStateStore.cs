using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TextCascade.Server;

public sealed record RuntimeStateEntry(string Username, ulong Version);

internal sealed record RuntimeStateFile(IReadOnlyList<RuntimeStateEntry> Entries);

public sealed class RuntimeStateStore
{
    private readonly object gate = new();
    private readonly string path;
    private readonly Dictionary<string, ulong> versions;

    public RuntimeStateStore(string path)
    {
        this.path = path;
        versions = Load(path);
    }

    public ulong GetVersion(string username)
    {
        lock (gate)
        {
            return versions.TryGetValue(username, out var version) ? version : 0UL;
        }
    }

    public void SaveVersion(string username, ulong version)
    {
        lock (gate)
        {
            if (versions.TryGetValue(username, out var current) && version <= current)
            {
                return;
            }

            versions[username] = version;
            WriteAtomic(path, versions.Select(pair => new RuntimeStateEntry(pair.Key, pair.Value))
                .OrderBy(pair => pair.Username, StringComparer.Ordinal)
                .ToList());
        }
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
