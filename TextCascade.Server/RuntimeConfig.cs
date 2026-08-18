using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Syntax;

namespace TextCascade.Server;

public sealed record ServerConfig(string Bind, int Port, string CertificatePath);

public sealed record AuthConfig(
    int TokenTtlDays,
    string TokenSecretEnv,
    int Argon2MemoryKiB,
    int Argon2Iterations,
    int Argon2Parallelism);

public sealed record LimitsConfig(
    int MaxTextBytes,
    int MaxFrameBytes,
    int SendQueueCapacity,
    int SeenIdCapacity,
    int HelloTimeoutSeconds,
    int HeartbeatIntervalSeconds,
    int HeartbeatTimeoutSeconds,
    int SnapshotWindowSeconds,
    int SnapshotTotalBytes,
    int RecoveryClipQueueCapacity);

public sealed record RateLimitConfig(
    int LoginIpPerMinute,
    int LoginUserPerMinute,
    int MaxKeys,
    int ClipBurst,
    int ClipTokensPerSecond);

public sealed record FilesConfig(string UsersFile);

public sealed record RuntimeConfig(
    ServerConfig Server,
    AuthConfig Auth,
    LimitsConfig Limits,
    RateLimitConfig RateLimit,
    FilesConfig Files,
    byte[]? TokenSecret = null);

public static class RuntimeConfigAccessor
{
    private static RuntimeConfig? current;

    public static RuntimeConfig? Current
    {
        get => current;
        set => current = value;
    }
}

public static class Config
{
    public static RuntimeConfig CreateDefaultConfig() => new(
        new ServerConfig("0.0.0.0", 8443, "certs/server.pfx"),
        new AuthConfig(30, "TEXTCASCADE_TOKEN_SECRET", 19456, 2, 1),
        new LimitsConfig(524288, 589824, 16, 64, 5, 30, 60, 3, 4194304, 16),
        new RateLimitConfig(10, 5, 10000, 10, 2),
        new FilesConfig("users.json"));

    public static RuntimeConfig LoadTomlConfig(string path, RuntimeConfig? defaults = null)
    {
        var config = defaults ?? CreateDefaultConfig();
        if (!File.Exists(path))
        {
            return config;
        }

        string text;
        try
        {
            text = File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException("TOML configuration must be UTF-8.", exception);
        }

        var syntax = Toml.Parse(text, path);
        if (syntax.HasErrors)
        {
            throw new InvalidOperationException($"Invalid TOML configuration: {string.Join("; ", syntax.Diagnostics)}");
        }

        TomlTable model;
        try
        {
            model = Toml.ToModel(syntax);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TomlException)
        {
            throw new InvalidOperationException($"Invalid TOML configuration: {exception.Message}", exception);
        }

        WarnUnknownKeys(model, "", new[] { "server", "auth", "limits", "rate_limit", "files" });
        return ApplyTomlModel(config, model);
    }

    private static RuntimeConfig ApplyTomlModel(RuntimeConfig config, TomlTable model)
    {
        if (TryGetTable(model, "server", out var server))
        {
            WarnUnknownKeys(server, "server", new[] { "bind", "port", "certificate_path" });
            var bind = GetString(server, "bind", config.Server.Bind, "server.bind");
            var port = GetInt(server, "port", config.Server.Port, "server.port");
            var certificate = GetString(server, "certificate_path", config.Server.CertificatePath, "server.certificate_path");
            config = config with { Server = new ServerConfig(bind, port, certificate) };
        }

        if (TryGetTable(model, "auth", out var auth))
        {
            WarnUnknownKeys(auth, "auth", new[] { "token_ttl_days", "token_secret_env", "argon2_memory_kib", "argon2_iterations", "argon2_parallelism" });
            var ttl = GetInt(auth, "token_ttl_days", config.Auth.TokenTtlDays, "auth.token_ttl_days");
            var secretEnv = GetString(auth, "token_secret_env", config.Auth.TokenSecretEnv, "auth.token_secret_env");
            var memory = GetInt(auth, "argon2_memory_kib", config.Auth.Argon2MemoryKiB, "auth.argon2_memory_kib");
            var iterations = GetInt(auth, "argon2_iterations", config.Auth.Argon2Iterations, "auth.argon2_iterations");
            var parallelism = GetInt(auth, "argon2_parallelism", config.Auth.Argon2Parallelism, "auth.argon2_parallelism");
            config = config with { Auth = new AuthConfig(ttl, secretEnv, memory, iterations, parallelism) };
        }

        if (TryGetTable(model, "limits", out var limits))
        {
            WarnUnknownKeys(limits, "limits", new[] { "max_text_bytes", "max_frame_bytes", "send_queue_capacity", "seen_id_capacity", "hello_timeout_seconds", "heartbeat_interval_seconds", "heartbeat_timeout_seconds", "snapshot_window_seconds", "snapshot_total_bytes", "recovery_clip_queue_capacity" });
            var maxText = GetInt(limits, "max_text_bytes", config.Limits.MaxTextBytes, "limits.max_text_bytes");
            var maxFrame = GetInt(limits, "max_frame_bytes", config.Limits.MaxFrameBytes, "limits.max_frame_bytes");
            var sendQueue = GetInt(limits, "send_queue_capacity", config.Limits.SendQueueCapacity, "limits.send_queue_capacity");
            var seenId = GetInt(limits, "seen_id_capacity", config.Limits.SeenIdCapacity, "limits.seen_id_capacity");
            var helloTimeout = GetInt(limits, "hello_timeout_seconds", config.Limits.HelloTimeoutSeconds, "limits.hello_timeout_seconds");
            var heartbeatInterval = GetInt(limits, "heartbeat_interval_seconds", config.Limits.HeartbeatIntervalSeconds, "limits.heartbeat_interval_seconds");
            var heartbeatTimeout = GetInt(limits, "heartbeat_timeout_seconds", config.Limits.HeartbeatTimeoutSeconds, "limits.heartbeat_timeout_seconds");
            var snapshotWindow = GetInt(limits, "snapshot_window_seconds", config.Limits.SnapshotWindowSeconds, "limits.snapshot_window_seconds");
            var snapshotTotal = GetInt(limits, "snapshot_total_bytes", config.Limits.SnapshotTotalBytes, "limits.snapshot_total_bytes");
            var recoveryQueue = GetInt(limits, "recovery_clip_queue_capacity", config.Limits.RecoveryClipQueueCapacity, "limits.recovery_clip_queue_capacity");
            config = config with { Limits = new LimitsConfig(maxText, maxFrame, sendQueue, seenId, helloTimeout, heartbeatInterval, heartbeatTimeout, snapshotWindow, snapshotTotal, recoveryQueue) };
        }

        if (TryGetTable(model, "rate_limit", out var rate))
        {
            WarnUnknownKeys(rate, "rate_limit", new[] { "login_ip_per_minute", "login_user_per_minute", "max_keys", "clip_burst", "clip_tokens_per_second" });
            config = config with
            {
                RateLimit = new RateLimitConfig(
                    GetInt(rate, "login_ip_per_minute", config.RateLimit.LoginIpPerMinute, "rate_limit.login_ip_per_minute"),
                    GetInt(rate, "login_user_per_minute", config.RateLimit.LoginUserPerMinute, "rate_limit.login_user_per_minute"),
                    GetInt(rate, "max_keys", config.RateLimit.MaxKeys, "rate_limit.max_keys"),
                    GetInt(rate, "clip_burst", config.RateLimit.ClipBurst, "rate_limit.clip_burst"),
                    GetInt(rate, "clip_tokens_per_second", config.RateLimit.ClipTokensPerSecond, "rate_limit.clip_tokens_per_second")),
            };
        }

        if (TryGetTable(model, "files", out var files))
        {
            WarnUnknownKeys(files, "files", new[] { "users_file" });
            config = config with { Files = new FilesConfig(GetString(files, "users_file", config.Files.UsersFile, "files.users_file")) };
        }

        return config;
    }

    private static bool TryGetTable(TomlTable table, string key, out TomlTable result)
    {
        if (!table.TryGetValue(key, out var value))
        {
            result = null!;
            return false;
        }

        if (value is not TomlTable nested)
        {
            throw new InvalidOperationException($"TOML key '{key}' must be a table.");
        }

        result = nested;
        return true;
    }

    private static string GetString(TomlTable table, string key, string fallback, string path)
    {
        if (!table.TryGetValue(key, out var value)) return fallback;
        if (value is not string text) throw new InvalidOperationException($"TOML key '{path}' must be a string.");
        return text;
    }

    private static int GetInt(TomlTable table, string key, int fallback, string path)
    {
        if (!table.TryGetValue(key, out var value)) return fallback;
        if (value is not long number || number is < int.MinValue or > int.MaxValue)
        {
            throw new InvalidOperationException($"TOML key '{path}' must be a 32-bit integer.");
        }

        return (int)number;
    }

    private static void WarnUnknownKeys(TomlTable table, string section, IReadOnlyCollection<string> known)
    {
        foreach (var key in table.Keys)
        {
            if (!known.Contains(key, StringComparer.Ordinal))
            {
                var path = string.IsNullOrEmpty(section) ? key : $"{section}.{key}";
                Console.Error.WriteLine($"Warning: unknown TOML key '{path}' was ignored.");
            }
        }
    }

    public static RuntimeConfig ApplyEnvironmentOverrides(RuntimeConfig config)
    {
        if (Environment.GetEnvironmentVariable("TEXTCASCADE_BIND") is { Length: > 0 } bind)
        {
            config = config with { Server = config.Server with { Bind = bind } };
        }

        if (Environment.GetEnvironmentVariable("TEXTCASCADE_PORT") is { Length: > 0 } portText)
        {
            if (!int.TryParse(portText, out var port))
            {
                throw new InvalidOperationException("TEXTCASCADE_PORT must be a valid integer.");
            }

            config = config with { Server = config.Server with { Port = port } };
        }

        if (Environment.GetEnvironmentVariable("TEXTCASCADE_CERTIFICATE_PATH") is { Length: > 0 } certificatePath)
        {
            config = config with { Server = config.Server with { CertificatePath = certificatePath } };
        }

        if (Environment.GetEnvironmentVariable("TEXTCASCADE_USERS_FILE") is { Length: > 0 } usersFile)
        {
            config = config with { Files = config.Files with { UsersFile = usersFile } };
        }

        if (Environment.GetEnvironmentVariable(config.Auth.TokenSecretEnv) is { Length: > 0 } secretText)
        {
            var secret = Encoding.UTF8.GetBytes(secretText);
            config = config with { TokenSecret = secret };
        }

        return config;
    }

    public static void ValidateConfig(RuntimeConfig config)
    {
        if (config.Server.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("server.port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(config.Server.Bind))
        {
            throw new InvalidOperationException("server.bind must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(config.Server.CertificatePath))
        {
            throw new InvalidOperationException("server.certificate_path must not be empty.");
        }

        if (config.Auth.TokenTtlDays <= 0)
        {
            throw new InvalidOperationException("auth.token_ttl_days must be positive.");
        }

        if (string.IsNullOrWhiteSpace(config.Auth.TokenSecretEnv))
        {
            throw new InvalidOperationException("auth.token_secret_env must not be empty.");
        }

        if (config.Auth.Argon2MemoryKiB <= 0 || config.Auth.Argon2Iterations <= 0 || config.Auth.Argon2Parallelism <= 0)
        {
            throw new InvalidOperationException("Argon2 parameters must be positive.");
        }

        if (config.TokenSecret is null || config.TokenSecret.Length < 32)
        {
            throw new InvalidOperationException($"Token secret from {config.Auth.TokenSecretEnv} must be at least 32 bytes.");
        }

        if (config.Limits.MaxTextBytes <= 0 || config.Limits.MaxFrameBytes <= config.Limits.MaxTextBytes)
        {
            throw new InvalidOperationException("limits.max_frame_bytes must be greater than max_text_bytes.");
        }

        if (config.Limits.SendQueueCapacity <= 0 || config.Limits.SeenIdCapacity <= 0 || config.Limits.HelloTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("limits capacities and timeouts must be positive.");
        }

        if (config.Limits.HeartbeatIntervalSeconds <= 0 || config.Limits.HeartbeatTimeoutSeconds <= config.Limits.HeartbeatIntervalSeconds)
        {
            throw new InvalidOperationException("heartbeat timeout must be greater than interval.");
        }

        if (config.Limits.SnapshotWindowSeconds <= 0 || config.Limits.SnapshotTotalBytes <= 0 || config.Limits.RecoveryClipQueueCapacity <= 0)
        {
            throw new InvalidOperationException("snapshot limits must be positive.");
        }

        if (config.RateLimit.LoginIpPerMinute <= 0 || config.RateLimit.LoginUserPerMinute <= 0 || config.RateLimit.MaxKeys <= 0
            || config.RateLimit.ClipBurst <= 0 || config.RateLimit.ClipTokensPerSecond <= 0)
        {
            throw new InvalidOperationException("rate limits must be positive.");
        }

        if (string.IsNullOrWhiteSpace(config.Files.UsersFile))
        {
            throw new InvalidOperationException("files.users_file must not be empty.");
        }
    }
}
