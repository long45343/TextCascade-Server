using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
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
            Console.Error.WriteLine("Usage: TextCascade.Server user <command> [options]");
            Console.Error.WriteLine("Commands: add, passwd, disable, enable, delete, revoke-tokens, list, hash");
            return Error;
        }

        hasher ??= new Argon2PasswordHasher();
        using var lockHandle = SingleInstanceLock.Acquire();
        if (lockHandle is null)
        {
            Console.Error.WriteLine("Another TextCascade CLI process is running.");
            return Error;
        }

        var rest = args.Skip(1).ToArray();
        return rest switch
        {
            { Length: > 0 } when rest[0] == "add" => CommandAddUser(rest, hasher),
            { Length: > 0 } when rest[0] == "passwd" => CommandPasswd(rest, hasher),
            { Length: > 0 } when rest[0] == "disable" => CommandSetDisabled(rest, disabled: true),
            { Length: > 0 } when rest[0] == "enable" => CommandSetDisabled(rest, disabled: false),
            { Length: > 0 } when rest[0] == "delete" => CommandDeleteUser(rest),
            { Length: > 0 } when rest[0] == "revoke-tokens" => CommandRevokeTokens(rest),
            { Length: > 0 } when rest[0] == "list" => CommandListUsers(rest),
            { Length: > 0 } when rest[0] == "hash" => CommandHashPassword(rest, hasher),
            _ => PrintUsage(),
        };
    }

    private static int PrintUsage()
    {
        Console.Error.WriteLine("Usage: TextCascade.Server user <command> [options]");
        Console.Error.WriteLine("Commands: add, passwd, disable, enable, delete, revoke-tokens, list, hash");
        return Error;
    }

    private static int CommandAddUser(string[] args, IPasswordHasher hasher)
    {
        if (!TryGetOption(args, "username", out var username))
        {
            Console.Error.WriteLine("--username is required");
            return Error;
        }

        var password = ReadPassword("Password: ");
        var confirm = ReadPassword("Confirm: ");
        if (!string.Equals(password, confirm, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Passwords do not match.");
            return Error;
        }

        var config = Config.CreateDefaultConfig();
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

    private static int CommandPasswd(string[] args, IPasswordHasher hasher)
    {
        if (!TryGetOption(args, "username", out var username))
        {
            Console.Error.WriteLine("--username is required");
            return Error;
        }

        var config = Config.CreateDefaultConfig();
        var usersPath = config.Files.UsersFile;
        var users = LoadForWrite(usersPath);
        var index = users.Users.FindIndex(user => string.Equals(user.Username, username, StringComparison.Ordinal));
        if (index < 0)
        {
            Console.Error.WriteLine($"User {username} not found.");
            return Error;
        }

        var password = ReadPassword("New password: ");
        var hash = hasher.Hash(password, CreateArgon2Config(config));
        users.Users[index] = users.Users[index] with { PasswordHash = hash };
        UsersFile.SaveUsers(usersPath, users);
        Console.WriteLine($"Password updated for {username}.");
        return Ok;
    }

    private static int CommandSetDisabled(string[] args, bool disabled)
    {
        if (!TryGetOption(args, "username", out var username))
        {
            Console.Error.WriteLine("--username is required");
            return Error;
        }

        var config = Config.CreateDefaultConfig();
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

    private static int CommandDeleteUser(string[] args)
    {
        if (!TryGetOption(args, "username", out var username))
        {
            Console.Error.WriteLine("--username is required");
            return Error;
        }

        var config = Config.CreateDefaultConfig();
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

    private static int CommandRevokeTokens(string[] args)
    {
        if (!TryGetOption(args, "username", out var username))
        {
            Console.Error.WriteLine("--username is required");
            return Error;
        }

        var config = Config.CreateDefaultConfig();
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

    private static int CommandListUsers(string[] args)
    {
        var config = Config.CreateDefaultConfig();
        var users = File.Exists(config.Files.UsersFile) ? UsersFile.LoadUsers(config.Files.UsersFile) : new UsersFile();
        Console.WriteLine($"nextTokenVersion: {users.NextTokenVersion}");
        Console.WriteLine("username,disabled,tokenVersion");
        foreach (var user in users.Users)
        {
            Console.WriteLine($"{user.Username},{user.Disabled},{user.TokenVersion}");
        }
        return Ok;
    }

    private static int CommandHashPassword(string[] args, IPasswordHasher hasher)
    {
        var password = ReadPassword("Password: ");
        var config = Config.CreateDefaultConfig();
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

    private static string ReadPassword(string prompt)
    {
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
    public static SingleInstanceLockHandle? Acquire(TimeSpan? pollDelay = null)
    {
        var directory = AppContext.BaseDirectory;
        var lockPath = Path.Combine(directory, ".textcascade-cli.lock");
        var delay = pollDelay ?? TimeSpan.FromMilliseconds(100);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(lockPath))
                {
                    var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using (var writer = new StreamWriter(stream, Encoding.UTF8, bufferSize: 32, leaveOpen: true))
                    {
                        writer.Write(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                    }
                    return new SingleInstanceLockHandle(lockPath, stream);
                }

                if (TryRecoverStaleLock(lockPath, out var recovered))
                {
                    return recovered;
                }
            }
            catch (IOException)
            {
            }

            Thread.Sleep(delay);
        }

        return null;
    }

    private static bool TryRecoverStaleLock(string lockPath, out SingleInstanceLockHandle? handle)
    {
        handle = null;
        try
        {
            var text = File.ReadAllText(lockPath).Trim();
            if (!int.TryParse(text, CultureInfo.InvariantCulture, out var pid))
            {
                return false;
            }

            if (IsProcessAlive(pid))
            {
                return false;
            }

            File.Delete(lockPath);
            var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using (var writer = new StreamWriter(stream, Encoding.UTF8, bufferSize: 32, leaveOpen: true))
            {
                writer.Write(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            }
            handle = new SingleInstanceLockHandle(lockPath, stream);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            _ = process.Handle;
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }
}
