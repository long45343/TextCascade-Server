using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace TextCascade.Server;

public sealed partial record UserRecord(
    string Username,
    string PasswordHash,
    long TokenVersion,
    bool Disabled = false);

public sealed partial class UsersFile
{
    public long NextTokenVersion { get; set; } = 1;

    public List<UserRecord> Users { get; set; } = new();

    [GeneratedRegex(@"^\$argon2id\$v=\d+\$m=\d+,t=\d+,p=\d+\$[A-Za-z0-9+/=]+\$[A-Za-z0-9+/=]+$")]
    private static partial Regex Argon2HashRegex();

    public static UsersFile LoadUsers(string path)
    {
        if (!File.Exists(path))
        {
            return new UsersFile();
        }

        var text = File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !HasUniqueProperties(root, "nextTokenVersion", "users")
            || !root.TryGetProperty("nextTokenVersion", out var watermark)
            || watermark.ValueKind != JsonValueKind.Number
            || !long.TryParse(watermark.GetRawText(), out var nextTokenVersion)
            || !root.TryGetProperty("users", out var usersElement)
            || usersElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("users.json must contain nextTokenVersion and users.");
        }

        foreach (var userElement in usersElement.EnumerateArray())
        {
            if (userElement.ValueKind != JsonValueKind.Object
                || !HasUniqueProperties(userElement, "username", "passwordHash", "tokenVersion", "disabled")
                || !userElement.TryGetProperty("username", out var username)
                || username.ValueKind != JsonValueKind.String
                || !userElement.TryGetProperty("passwordHash", out var passwordHash)
                || passwordHash.ValueKind != JsonValueKind.String
                || !userElement.TryGetProperty("tokenVersion", out var tokenVersion)
                || tokenVersion.ValueKind != JsonValueKind.Number
                || !userElement.TryGetProperty("disabled", out var disabled)
                || disabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidOperationException("Each users.json entry must contain username, passwordHash, tokenVersion, and disabled.");
            }
        }

        var users = JsonSerializer.Deserialize<UsersFile>(text, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        if (users is null)
        {
            throw new InvalidOperationException("users.json is empty or invalid.");
        }

        if (users.NextTokenVersion != nextTokenVersion)
        {
            throw new InvalidOperationException("users.json nextTokenVersion is invalid.");
        }

        ValidateUsers(users);
        return users;
    }

    private static bool HasUniqueProperties(JsonElement element, params string[] known)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!known.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return true;
    }

    public static void ValidateUsers(UsersFile users)
    {
        if (users.NextTokenVersion <= 0)
        {
            throw new InvalidOperationException("nextTokenVersion must be a positive 64-bit integer.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in users.Users)
        {
            if (string.IsNullOrWhiteSpace(user.Username) || !seen.Add(user.Username))
            {
                throw new InvalidOperationException("Usernames must be non-empty and unique.");
            }

            if (user.TokenVersion <= 0 || user.TokenVersion >= users.NextTokenVersion)
            {
                throw new InvalidOperationException("User tokenVersion must be positive and less than nextTokenVersion.");
            }

            if (!Argon2HashRegex().IsMatch(user.PasswordHash))
            {
                throw new InvalidOperationException($"User {user.Username} has an invalid Argon2id hash.");
            }
        }
    }

    public static IReadOnlyDictionary<string, UserRecord> BuildUserLookup(UsersFile users)
    {
        return users.Users.ToDictionary(user => user.Username, user => user, StringComparer.Ordinal);
    }

    public static void SaveUsers(string path, UsersFile users)
    {
        ValidateUsers(users);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        var json = JsonSerializer.Serialize(users, options);
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

    public static UsersFile Copy(UsersFile source)
    {
        return new UsersFile
        {
            NextTokenVersion = source.NextTokenVersion,
            Users = source.Users.Select(user => user with { }).ToList(),
        };
    }
}
