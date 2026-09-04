package config_test

import (
	"os"
	"path/filepath"
	"testing"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"

	"github.com/long45343/TextCascade-Server/internal/config"
)

// ---- ConfigTests ----

func TestCreateDefaultConfigHasExpectedValues(t *testing.T) {
	cfg := config.Defaults()
	assert.Equal(t, 8443, cfg.Server.Port)
	assert.Equal(t, 524288, cfg.Limits.MaxTextBytes)
	assert.Equal(t, 589824, cfg.Limits.MaxFrameBytes)
	assert.Equal(t, 30, cfg.Auth.TokenTtlDays)
}

func TestValidateRejectsFrameBytesNotGreaterThanTextBytes(t *testing.T) {
	cfg := config.Defaults()
	cfg.Limits = config.LimitsConfig{MaxTextBytes: 100, MaxFrameBytes: 100, SendQueueCapacity: 16, SeenIdCapacity: 64, HelloTimeoutSeconds: 5, HeartbeatIntervalSeconds: 30, HeartbeatTimeoutSeconds: 60, SnapshotWindowSeconds: 3, SnapshotTotalBytes: 4194304, RecoveryClipQueueCapacity: 16}
	assert.Error(t, cfg.Validate())
}

func TestValidateRejectsHeartbeatTimeoutNotGreaterThanInterval(t *testing.T) {
	cfg := config.Defaults()
	cfg.Limits = config.LimitsConfig{MaxTextBytes: 524288, MaxFrameBytes: 589824, SendQueueCapacity: 16, SeenIdCapacity: 64, HelloTimeoutSeconds: 5, HeartbeatIntervalSeconds: 30, HeartbeatTimeoutSeconds: 30, SnapshotWindowSeconds: 3, SnapshotTotalBytes: 4194304, RecoveryClipQueueCapacity: 16}
	assert.Error(t, cfg.Validate())
}

func TestValidateRejectsMissingTokenSecret(t *testing.T) {
	cfg := config.Defaults()
	assert.Error(t, cfg.Validate())
}

func TestValidateAcceptsFullConfig(t *testing.T) {
	cfg := config.Defaults()
	cfg.TokenSecret = make([]byte, 32)
	assert.NoError(t, cfg.Validate())
}

func TestApplyEnvironmentOverridesReadsTokenSecret(t *testing.T) {
	t.Setenv("TEXTCASCADE_TOKEN_SECRET", "ssssssssssssssssssssssssssssssssssssssss")
	cfg, err := config.ApplyEnv(config.Defaults())
	require.NoError(t, err)
	assert.NotNil(t, cfg.TokenSecret)
	assert.GreaterOrEqual(t, len(cfg.TokenSecret), 32)
}

// ---- RuntimeStateAndProtocolTests（config 部分）----

func TestConfigParsesStateFilePath(t *testing.T) {
	path := filepath.Join(t.TempDir(), "textcascade.toml")
	require.NoError(t, os.WriteFile(path, []byte("[files]\nstate_file = \"/tmp/state.json\"\n"), 0o644))
	cfg, err := config.LoadTOML(path, config.Defaults())
	require.NoError(t, err)
	assert.Equal(t, "/tmp/state.json", cfg.Files.StateFile)
}

// 未知键 warning：解析成功但忽略。
func TestLoadTOMLWarnsUnknownKeys(t *testing.T) {
	path := filepath.Join(t.TempDir(), "textcascade.toml")
	require.NoError(t, os.WriteFile(path, []byte("[server]\nport = 9443\nbogus_key = 1\n"), 0o644))
	cfg, err := config.LoadTOML(path, config.Defaults())
	require.NoError(t, err)
	assert.Equal(t, 9443, cfg.Server.Port)
}

// 重复键 / 非法类型 fail-fast。
func TestLoadTOMLRejectsInvalid(t *testing.T) {
	path := filepath.Join(t.TempDir(), "bad1.toml")
	require.NoError(t, os.WriteFile(path, []byte("[server]\nport = 1\nport = 2\n"), 0o644))
	_, err := config.LoadTOML(path, config.Defaults())
	assert.Error(t, err)

	path2 := filepath.Join(t.TempDir(), "bad2.toml")
	require.NoError(t, os.WriteFile(path2, []byte("[server]\nport = \"abc\"\n"), 0o644))
	_, err = config.LoadTOML(path2, config.Defaults())
	assert.Error(t, err)
}
