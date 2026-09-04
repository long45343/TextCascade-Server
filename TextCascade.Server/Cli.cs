using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Isopoh.Cryptography.Argon2;

namespace TextCascade.Server;

public static class Cli
{
    public const int Ok = 0;
    public const int Error = 1;

    public static int RunCli(string[] args, IPasswordHasher? hasher = null)
    {
        if (args.Length == 0 || args[0] != "user")
        {
            return PrintUsage();
        }

        hasher ??= new Argon2PasswordHasher();

        var rest = args.Skip(1).ToArray();
        if (!TryExtractConfigOption(ref rest, out var configPath))
        {
            Console.Error.WriteLine("--config requires a path.");
            return Error;
        }

        configPath ??= Environment.GetEnvironmentVariable("TEXTCASCADE_CONFIG") is { Length: > 0 } environmentConfig
            ? environmentConfig
            : "textcascade.toml";
        RuntimeConfig config;
        try
        {
            config = Config.CreateDefaultConfig();
            config = Config.LoadTomlConfig(configPath, config);
            config = Config.ApplyEnvironmentOverrides(config);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or DecoderFallbackException or IOException)
        {
            Console.Error.WriteLine($"Configuration error: {exception.Message}");
            return Error;
        }

        SingleInstanceLockHandle? lockHandle;
        try
        {
            var lockPath = CreateLockPath(config.Files.UsersFile);
            lockHandle = SingleInstanceLock.Acquire(lockPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Unable to acquire users file lock: {exception.Message}");
            return Error;
        }

        using (lockHandle)
        {
            if (lockHandle is null)
            {
                Console.Error.WriteLine("Another TextCascade CLI process is running.");
                return Error;
            }

            return rest switch
        {
            { Length: > 0 } when rest[0] == "add" => CommandAddUser(rest, hasher, config),
            { Length: > 0 } when rest[0] == "passwd" => CommandPasswd(rest, hasher, config),
            { Length: > 0 } when rest[0] == "disable" => CommandSetDisabled(rest, disabled: true, config),
            { Length: > 0 } when rest[0] == "enable" => CommandSetDisabled(rest, disabled: false, config),
            { Length: > 0 } when rest[0] == "delete" => CommandDeleteUser(rest, config),
            { Length: > 0 } when rest[0] == "revoke-tokens" => CommandRevokeTokens(rest, config),
            { Length: > 0 } when rest[0] == "list" => CommandListUsers(rest, config),
            { Length: > 0 } when rest[0] == "hash" => CommandHashPassword(rest, hasher, config),
            _ => PrintUsage(),
        };
        }
    }

    internal static string CreateLockPath(string usersFile)
    {
        var fullUsersPath = Path.GetFullPath(usersFile);
        var directory = Path.GetDirectoryName(fullUsersPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("users.json path must include a parent directory.");
        }

        var fileName = Path.GetFileName(fullUsersPath);
        return Path.Combine(directory, $"{fileName}.lock");
    }

    private static int PrintUsage()
    {
        Console.Error.WriteLine("Usage: TextCascade.Server user <command> [options]");
        Console.Error.WriteLine("Commands: add, passwd, disable, enable, delete, revoke-tokens, list, hash");
        Console.Error.WriteLine("All commands accept --config <path>; fallback order is --config, TEXTCASCADE_CONFIG, then textcascade.toml.");
        return Error;
    }

    private static bool TryExtractConfigOption(ref string[] args, out string? configPath)
    {
        configPath = null;
        var remaining = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--config", StringComparison.Ordinal))
            {
                if (index + 1 >= args.Length)
                {
                    return false;
                }

                configPath = args[++index];
            }
            else if (args[index].StartsWith("--config=", StringComparison.Ordinal))
            {
                configPath = args[index]["--config=".Length..];
            }
            else
            {
                remaining.Add(args[index]);
            }
        }

        args = remaining.ToArray();
        return true;
    }

    private static int CommandAddUser(string[] args, IPasswordHasher hasher, RuntimeConfig config)
    {
        if (!TryGetOption(args, "username", out var username))
        {
            Console.Error.WriteLine("--username is required");
            return Error;
        }

        var password = ReadPassword("Password: ", args);
        var confirm = HasPasswordStdin(args) ? password : ReadPassword("Confirm: ", args);
        if (!string.Equals(password, confirm, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Passwords do not match.");
            return Error;
        }

        var usersPath = config.Files.UsersFile;
        var users = LoadForWrite(usersPath);
        if (users.Users.Any(user => string.Equals(user.Username, username, StringComparison.Ordinal)))
        {
            Console.Error.WriteLine($"User {username} already exists.");
            return Error;
        }

        var hash = hasher.Hash(password, CreateArgon2Config(config));
        var tokenVersion = users.NextTokenVersion;
        if (tokenVersion <= 0 || tokenVersion == long.MaxValue)
        {
            Console.Error.WriteLine("nextTokenVersion overflow; refusing to add user.");
            return Error;
        }

        users.Users.Add(new UserRecord(username, hash, tokenVersion));
        users.NextTokenVersion = IncrementWatermark(users.NextTokenVersion);
        UsersFile.SaveUsers(usersPath, users);
        Console.WriteLine($"Added user {username} (tokenVersion {tokenVersion}).");
        return Ok;
    }

    private static int CommandPasswd(string[] args, IPasswordHasher hasher, RuntimeConfig config)
    {
        if (!TryGetOption(args, "username", out var username))
        {
            Console.Error.WriteLine("--username is required");
            return Error;
        }

        var usersPath = config.Files.UsersFile;
        var users = LoadForWrite(usersPath);
        var index = users.Users.FindIndex(user => string.Equals(user.Username, username, StringComparison.Ordinal));
        if (index < 0)
        {
            Console.Error.WriteLine($"User {username} not found.");
            return Error;
        }

        var password = ReadPassword("New password: ", args);
        var hash = hasher.Hash(password, CreateArgon2Config(config));
        users.Users[index] = users.Users[index] with { PasswordHash = hash };
        UsersFile.SaveUsers(usersPath, users);
        Console.WriteLine($"Password updated for {username}.");
        return Ok;
    }

    private static int CommandSetDisabled(string[] args, bool disabled, RuntimeConfig config)
    {
        if (!TryGetOption(args, "username", out var username))
        {
            Console.Error.WriteLine("--username is required");
            return Error;
        }

        var usersPath = config.Files.UsersFile;
        var users = LoadForWrite(usersPath);
        var index = users.Users.FindIndex(user => string.Equals(user.Username, username, StringComparison.Ordinal));
        if (index < 0)
        {
            Console.Error.WriteLine($"User {username} not found.");
            return Error;
        }

        users.Users[index] = users.Users[index] with { Disabled = disabled };
        UsersFile.SaveUsers(usersPath, users);
        Console.WriteLine($"User {username} {(disabled ? "disabled" : "enabled")}.");
        return Ok;
    }

    private static int CommandDeleteUser(string[] args, RuntimeConfig config)
    {
        if (!TryGetOption(args, "username", out var username))
        {
            Console.Error.WriteLine("--username is required");
            return Error;
        }

        var usersPath = config.Files.UsersFile;
        var users = LoadForWrite(usersPath);
        var index = users.Users.FindIndex(user => string.Equals(user.Username, username, StringComparison.Ordinal));
        if (index < 0)
        {
            Console.Error.WriteLine($"User {username} not found.");
            return Error;
        }

        users.Users.RemoveAt(index);
        UsersFile.SaveUsers(usersPath, users);
        Console.WriteLine($"Deleted user {username}.");
        return Ok;
    }

    private static int CommandRevokeTokens(string[] args, RuntimeConfig config)
    {
        if (!TryGetOption(args, "username", out var username))
        {
            Console.Error.WriteLine("--username is required");
            return Error;
        }

        var usersPath = config.Files.UsersFile;
        var users = LoadForWrite(usersPath);
        var index = users.Users.FindIndex(user => string.Equals(user.Username, username, StringComparison.Ordinal));
        if (index < 0)
        {
            Console.Error.WriteLine($"User {username} not found.");
            return Error;
        }

        var newVersion = users.NextTokenVersion;
        if (newVersion <= 0 || newVersion == long.MaxValue)
        {
            Console.Error.WriteLine("nextTokenVersion overflow; refusing to revoke tokens.");
            return Error;
        }

        users.Users[index] = users.Users[index] with { TokenVersion = newVersion };
        users.NextTokenVersion = IncrementWatermark(users.NextTokenVersion);
        UsersFile.SaveUsers(usersPath, users);
        Console.WriteLine($"Revoked tokens for {username} (new tokenVersion {newVersion}).");
        return Ok;
    }

    private static int CommandListUsers(string[] args, RuntimeConfig config)
    {
        var users = File.Exists(config.Files.UsersFile) ? UsersFile.LoadUsers(config.Files.UsersFile) : new UsersFile();
        Console.WriteLine($"nextTokenVersion: {users.NextTokenVersion}");
        Console.WriteLine("username,disabled,tokenVersion");
        foreach (var user in users.Users)
        {
            Console.WriteLine($"{user.Username},{user.Disabled},{user.TokenVersion}");
        }
        return Ok;
    }

    private static int CommandHashPassword(string[] args, IPasswordHasher hasher, RuntimeConfig config)
    {
        var password = ReadPassword("Password: ", args);
        var hash = hasher.Hash(password, CreateArgon2Config(config));
        Console.WriteLine(hash);
        return Ok;
    }

    private static UsersFile LoadForWrite(string path)
    {
        return File.Exists(path) ? UsersFile.LoadUsers(path) : new UsersFile();
    }

    private static long IncrementWatermark(long current)
    {
        checked
        {
            return current + 1;
        }
    }

    internal static Argon2Config CreateArgon2Config(RuntimeConfig config)
    {
        return new Argon2Config
        {
            Type = Argon2Type.HybridAddressing,
            Version = Argon2Version.Nineteen,
            MemoryCost = config.Auth.Argon2MemoryKiB,
            TimeCost = config.Auth.Argon2Iterations,
            Threads = config.Auth.Argon2Parallelism,
        };
    }

    private static bool TryGetOption(string[] args, string name, out string value)
    {
        var flag = $"--{name}";
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
            {
                value = args[i + 1];
                return value.Length > 0;
            }
        }
        value = string.Empty;
        return false;
    }

    internal static bool HasPasswordStdin(string[] args) => HasFlag(args, "password-stdin");

    private static bool HasFlag(string[] args, string name)
    {
        var flag = $"--{name}";
        return args.Any(arg => string.Equals(arg, flag, StringComparison.Ordinal));
    }

    internal static string ReadPassword(string prompt, string[] args)
    {
        if (HasPasswordStdin(args))
        {
            var line = Console.In.ReadLine();
            if (string.IsNullOrEmpty(line))
            {
                Console.Error.WriteLine("--password-stdin requires one non-empty line.");
                throw new ArgumentException("--password-stdin requires one non-empty line.");
            }

            return line;
        }

        Console.Write(prompt);
        var builder = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace && builder.Length > 0)
            {
                builder.Remove(builder.Length - 1, 1);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }
        return builder.ToString();
    }
}

public sealed class SingleInstanceLockHandle : IDisposable
{
    private readonly string lockPath;
    private readonly FileStream stream;

    public SingleInstanceLockHandle(string lockPath, FileStream stream)
    {
        this.lockPath = lockPath;
        this.stream = stream;
    }

    public void Dispose()
    {
        try
        {
            stream.Dispose();
            if (File.Exists(lockPath))
            {
                File.Delete(lockPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public static class SingleInstanceLock
{
    public static SingleInstanceLockHandle? Acquire(string lockPath, TimeSpan? pollDelay = null)
    {
        if (string.IsNullOrWhiteSpace(lockPath))
        {
            throw new ArgumentException("Lock path must not be empty.", nameof(lockPath));
        }

        var directory = Path.GetDirectoryName(lockPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("Lock path must include a directory.", nameof(lockPath));
        }

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Directory '{directory}' does not exist.");
        }

        var delay = pollDelay ?? TimeSpan.FromMilliseconds(100);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // FileShare.None: the OS releases the handle when the holder process dies,
                // so a leftover file (crash, power loss) is simply reopened and never blocks;
                // the PID below is diagnostic only.
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                stream.SetLength(0);
                using (var writer = new StreamWriter(stream, Encoding.UTF8, bufferSize: 32, leaveOpen: true))
                {
                    writer.Write(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                }

                return new SingleInstanceLockHandle(lockPath, stream);
            }
            catch (IOException)
            {
            }

            Thread.Sleep(delay);
        }

        return null;
    }
}


