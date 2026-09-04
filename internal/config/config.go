// Package config 是 RuntimeConfig.cs 的 1:1 迁移：
// 默认值、TOML 加载（严格解析 + 未知键 warning）、环境变量覆盖、校验规则全部一致。
package config

import (
	"errors"
	"fmt"
	"os"
	"sort"
	"strconv"
	"strings"
	"unicode/utf8"

	"github.com/pelletier/go-toml/v2"
)

type ServerConfig struct {
	Bind            string
	Port            int
	CertificatePath string
}

type AuthConfig struct {
	TokenTtlDays      int
	TokenSecretEnv    string
	Argon2MemoryKiB   int
	Argon2Iterations  int
	Argon2Parallelism int
}

type LimitsConfig struct {
	MaxTextBytes              int
	MaxFrameBytes             int
	SendQueueCapacity         int
	SeenIdCapacity            int
	HelloTimeoutSeconds       int
	HeartbeatIntervalSeconds  int
	HeartbeatTimeoutSeconds   int
	SnapshotWindowSeconds     int
	SnapshotTotalBytes        int
	RecoveryClipQueueCapacity int
}

type RateLimitConfig struct {
	LoginIpPerMinute    int
	LoginUserPerMinute  int
	MaxKeys             int
	ClipBurst           int
	ClipTokensPerSecond int
}

type FilesConfig struct {
	UsersFile string
	StateFile string
}

type RuntimeConfig struct {
	Server      ServerConfig
	Auth        AuthConfig
	Limits      LimitsConfig
	RateLimit   RateLimitConfig
	Files       FilesConfig
	TokenSecret []byte
}

func Defaults() RuntimeConfig {
	return RuntimeConfig{
		Server:    ServerConfig{Bind: "0.0.0.0", Port: 8443, CertificatePath: "certs/server.pfx"},
		Auth:      AuthConfig{TokenTtlDays: 30, TokenSecretEnv: "TEXTCASCADE_TOKEN_SECRET", Argon2MemoryKiB: 19456, Argon2Iterations: 2, Argon2Parallelism: 1},
		Limits:    LimitsConfig{MaxTextBytes: 524288, MaxFrameBytes: 589824, SendQueueCapacity: 16, SeenIdCapacity: 64, HelloTimeoutSeconds: 5, HeartbeatIntervalSeconds: 30, HeartbeatTimeoutSeconds: 60, SnapshotWindowSeconds: 3, SnapshotTotalBytes: 4194304, RecoveryClipQueueCapacity: 16},
		RateLimit: RateLimitConfig{LoginIpPerMinute: 10, LoginUserPerMinute: 5, MaxKeys: 10000, ClipBurst: 10, ClipTokensPerSecond: 2},
		Files:     FilesConfig{UsersFile: "users.json", StateFile: "textcascade.state.json"},
	}
}

// LoadTOML 在 defaults 基础上加载 path；文件不存在时原样返回 defaults。
func LoadTOML(path string, def RuntimeConfig) (RuntimeConfig, error) {
	config := def
	raw, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return config, nil
		}
		return config, err
	}

	if !utf8.Valid(raw) {
		return config, errors.New("TOML configuration must be UTF-8.")
	}

	var model map[string]any
	if err := toml.Unmarshal(raw, &model); err != nil {
		return config, fmt.Errorf("Invalid TOML configuration: %v", err)
	}

	warnUnknownKeys(model, "", []string{"server", "auth", "limits", "rate_limit", "files"})
	return applyTOMLModel(config, model)
}

func applyTOMLModel(config RuntimeConfig, model map[string]any) (RuntimeConfig, error) {
	if server, ok, err := getTable(model, "server"); err != nil {
		return config, err
	} else if ok {
		warnUnknownKeys(server, "server", []string{"bind", "port", "certificate_path"})
		var err error
		if config.Server.Bind, err = getString(server, "bind", config.Server.Bind, "server.bind"); err != nil {
			return config, err
		}
		if config.Server.Port, err = getInt(server, "port", config.Server.Port, "server.port"); err != nil {
			return config, err
		}
		if config.Server.CertificatePath, err = getString(server, "certificate_path", config.Server.CertificatePath, "server.certificate_path"); err != nil {
			return config, err
		}
	}

	if auth, ok, err := getTable(model, "auth"); err != nil {
		return config, err
	} else if ok {
		warnUnknownKeys(auth, "auth", []string{"token_ttl_days", "token_secret_env", "argon2_memory_kib", "argon2_iterations", "argon2_parallelism"})
		var err error
		if config.Auth.TokenTtlDays, err = getInt(auth, "token_ttl_days", config.Auth.TokenTtlDays, "auth.token_ttl_days"); err != nil {
			return config, err
		}
		if config.Auth.TokenSecretEnv, err = getString(auth, "token_secret_env", config.Auth.TokenSecretEnv, "auth.token_secret_env"); err != nil {
			return config, err
		}
		if config.Auth.Argon2MemoryKiB, err = getInt(auth, "argon2_memory_kib", config.Auth.Argon2MemoryKiB, "auth.argon2_memory_kib"); err != nil {
			return config, err
		}
		if config.Auth.Argon2Iterations, err = getInt(auth, "argon2_iterations", config.Auth.Argon2Iterations, "auth.argon2_iterations"); err != nil {
			return config, err
		}
		if config.Auth.Argon2Parallelism, err = getInt(auth, "argon2_parallelism", config.Auth.Argon2Parallelism, "auth.argon2_parallelism"); err != nil {
			return config, err
		}
	}

	if limits, ok, err := getTable(model, "limits"); err != nil {
		return config, err
	} else if ok {
		warnUnknownKeys(limits, "limits", []string{"max_text_bytes", "max_frame_bytes", "send_queue_capacity", "seen_id_capacity", "hello_timeout_seconds", "heartbeat_interval_seconds", "heartbeat_timeout_seconds", "snapshot_window_seconds", "snapshot_total_bytes", "recovery_clip_queue_capacity"})
		var err error
		if config.Limits.MaxTextBytes, err = getInt(limits, "max_text_bytes", config.Limits.MaxTextBytes, "limits.max_text_bytes"); err != nil {
			return config, err
		}
		if config.Limits.MaxFrameBytes, err = getInt(limits, "max_frame_bytes", config.Limits.MaxFrameBytes, "limits.max_frame_bytes"); err != nil {
			return config, err
		}
		if config.Limits.SendQueueCapacity, err = getInt(limits, "send_queue_capacity", config.Limits.SendQueueCapacity, "limits.send_queue_capacity"); err != nil {
			return config, err
		}
		if config.Limits.SeenIdCapacity, err = getInt(limits, "seen_id_capacity", config.Limits.SeenIdCapacity, "limits.seen_id_capacity"); err != nil {
			return config, err
		}
		if config.Limits.HelloTimeoutSeconds, err = getInt(limits, "hello_timeout_seconds", config.Limits.HelloTimeoutSeconds, "limits.hello_timeout_seconds"); err != nil {
			return config, err
		}
		if config.Limits.HeartbeatIntervalSeconds, err = getInt(limits, "heartbeat_interval_seconds", config.Limits.HeartbeatIntervalSeconds, "limits.heartbeat_interval_seconds"); err != nil {
			return config, err
		}
		if config.Limits.HeartbeatTimeoutSeconds, err = getInt(limits, "heartbeat_timeout_seconds", config.Limits.HeartbeatTimeoutSeconds, "limits.heartbeat_timeout_seconds"); err != nil {
			return config, err
		}
		if config.Limits.SnapshotWindowSeconds, err = getInt(limits, "snapshot_window_seconds", config.Limits.SnapshotWindowSeconds, "limits.snapshot_window_seconds"); err != nil {
			return config, err
		}
		if config.Limits.SnapshotTotalBytes, err = getInt(limits, "snapshot_total_bytes", config.Limits.SnapshotTotalBytes, "limits.snapshot_total_bytes"); err != nil {
			return config, err
		}
		if config.Limits.RecoveryClipQueueCapacity, err = getInt(limits, "recovery_clip_queue_capacity", config.Limits.RecoveryClipQueueCapacity, "limits.recovery_clip_queue_capacity"); err != nil {
			return config, err
		}
	}

	if rate, ok, err := getTable(model, "rate_limit"); err != nil {
		return config, err
	} else if ok {
		warnUnknownKeys(rate, "rate_limit", []string{"login_ip_per_minute", "login_user_per_minute", "max_keys", "clip_burst", "clip_tokens_per_second"})
		var err error
		if config.RateLimit.LoginIpPerMinute, err = getInt(rate, "login_ip_per_minute", config.RateLimit.LoginIpPerMinute, "rate_limit.login_ip_per_minute"); err != nil {
			return config, err
		}
		if config.RateLimit.LoginUserPerMinute, err = getInt(rate, "login_user_per_minute", config.RateLimit.LoginUserPerMinute, "rate_limit.login_user_per_minute"); err != nil {
			return config, err
		}
		if config.RateLimit.MaxKeys, err = getInt(rate, "max_keys", config.RateLimit.MaxKeys, "rate_limit.max_keys"); err != nil {
			return config, err
		}
		if config.RateLimit.ClipBurst, err = getInt(rate, "clip_burst", config.RateLimit.ClipBurst, "rate_limit.clip_burst"); err != nil {
			return config, err
		}
		if config.RateLimit.ClipTokensPerSecond, err = getInt(rate, "clip_tokens_per_second", config.RateLimit.ClipTokensPerSecond, "rate_limit.clip_tokens_per_second"); err != nil {
			return config, err
		}
	}

	if files, ok, err := getTable(model, "files"); err != nil {
		return config, err
	} else if ok {
		warnUnknownKeys(files, "files", []string{"users_file", "state_file"})
		var err error
		if config.Files.UsersFile, err = getString(files, "users_file", config.Files.UsersFile, "files.users_file"); err != nil {
			return config, err
		}
		if config.Files.StateFile, err = getString(files, "state_file", config.Files.StateFile, "files.state_file"); err != nil {
			return config, err
		}
	}

	return config, nil
}

func getTable(table map[string]any, key string) (map[string]any, bool, error) {
	value, ok := table[key]
	if !ok {
		return nil, false, nil
	}
	nested, ok := value.(map[string]any)
	if !ok {
		return nil, true, fmt.Errorf("TOML key '%s' must be a table.", key)
	}
	return nested, true, nil
}

func getString(table map[string]any, key, fallback, path string) (string, error) {
	value, ok := table[key]
	if !ok {
		return fallback, nil
	}
	text, ok := value.(string)
	if !ok {
		return "", fmt.Errorf("TOML key '%s' must be a string.", path)
	}
	return text, nil
}

func getInt(table map[string]any, key string, fallback int, path string) (int, error) {
	value, ok := table[key]
	if !ok {
		return fallback, nil
	}
	number, ok := value.(int64)
	if !ok || number < int64(-2147483648) || number > int64(2147483647) {
		return 0, fmt.Errorf("TOML key '%s' must be a 32-bit integer.", path)
	}
	return int(number), nil
}

func warnUnknownKeys(table map[string]any, section string, known []string) {
	keys := make([]string, 0, len(table))
	for key := range table {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	for _, key := range keys {
		matched := false
		for _, k := range known {
			if k == key {
				matched = true
				break
			}
		}
		if matched {
			continue
		}
		path := key
		if section != "" {
			path = section + "." + key
		}
		fmt.Fprintf(os.Stderr, "Warning: unknown TOML key '%s' was ignored.\n", path)
	}
}

// ApplyEnv 应用 5 个环境变量 + token secret 变量（变量名取自 Auth.TokenSecretEnv）。
func ApplyEnv(config RuntimeConfig) (RuntimeConfig, error) {
	if bind := os.Getenv("TEXTCASCADE_BIND"); len(bind) > 0 {
		config.Server.Bind = bind
	}

	if portText := os.Getenv("TEXTCASCADE_PORT"); len(portText) > 0 {
		port, err := strconv.Atoi(portText)
		if err != nil {
			return config, errors.New("TEXTCASCADE_PORT must be a valid integer.")
		}
		config.Server.Port = port
	}

	if certificatePath := os.Getenv("TEXTCASCADE_CERTIFICATE_PATH"); len(certificatePath) > 0 {
		config.Server.CertificatePath = certificatePath
	}

	if usersFile := os.Getenv("TEXTCASCADE_USERS_FILE"); len(usersFile) > 0 {
		config.Files.UsersFile = usersFile
	}

	if stateFile := os.Getenv("TEXTCASCADE_STATE_FILE"); len(stateFile) > 0 {
		config.Files.StateFile = stateFile
	}

	if secretText := os.Getenv(config.Auth.TokenSecretEnv); len(secretText) > 0 {
		config.TokenSecret = []byte(secretText)
	}

	return config, nil
}

func (c *RuntimeConfig) Validate() error {
	if c.Server.Port < 1 || c.Server.Port > 65535 {
		return errors.New("server.port must be between 1 and 65535.")
	}

	if strings.TrimSpace(c.Server.Bind) == "" {
		return errors.New("server.bind must not be empty.")
	}

	if strings.TrimSpace(c.Server.CertificatePath) == "" {
		return errors.New("server.certificate_path must not be empty.")
	}

	if c.Auth.TokenTtlDays <= 0 {
		return errors.New("auth.token_ttl_days must be positive.")
	}

	if strings.TrimSpace(c.Auth.TokenSecretEnv) == "" {
		return errors.New("auth.token_secret_env must not be empty.")
	}

	if c.Auth.Argon2MemoryKiB <= 0 || c.Auth.Argon2Iterations <= 0 || c.Auth.Argon2Parallelism <= 0 {
		return errors.New("Argon2 parameters must be positive.")
	}

	if c.TokenSecret == nil || len(c.TokenSecret) < 32 {
		return fmt.Errorf("Token secret from %s must be at least 32 bytes.", c.Auth.TokenSecretEnv)
	}

	if c.Limits.MaxTextBytes <= 0 || c.Limits.MaxFrameBytes <= c.Limits.MaxTextBytes {
		return errors.New("limits.max_frame_bytes must be greater than max_text_bytes.")
	}

	if c.Limits.SendQueueCapacity <= 0 || c.Limits.SeenIdCapacity <= 0 || c.Limits.HelloTimeoutSeconds <= 0 {
		return errors.New("limits capacities and timeouts must be positive.")
	}

	if c.Limits.HeartbeatIntervalSeconds <= 0 || c.Limits.HeartbeatTimeoutSeconds <= c.Limits.HeartbeatIntervalSeconds {
		return errors.New("heartbeat timeout must be greater than interval.")
	}

	if c.Limits.SnapshotWindowSeconds <= 0 || c.Limits.SnapshotTotalBytes <= 0 || c.Limits.RecoveryClipQueueCapacity <= 0 {
		return errors.New("snapshot limits must be positive.")
	}

	if c.RateLimit.LoginIpPerMinute <= 0 || c.RateLimit.LoginUserPerMinute <= 0 || c.RateLimit.MaxKeys <= 0 ||
		c.RateLimit.ClipBurst <= 0 || c.RateLimit.ClipTokensPerSecond <= 0 {
		return errors.New("rate limits must be positive.")
	}

	if strings.TrimSpace(c.Files.UsersFile) == "" {
		return errors.New("files.users_file must not be empty.")
	}

	if strings.TrimSpace(c.Files.StateFile) == "" {
		return errors.New("files.state_file must not be empty.")
	}

	return nil
}
