package core

import (
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"

	"github.com/long45343/TextCascade-Server/internal/config"
)

func limiterConfig(loginIP, loginUser, maxKeys int) *config.RuntimeConfig {
	cfg := config.Defaults()
	cfg.RateLimit = config.RateLimitConfig{
		LoginIpPerMinute:    loginIP,
		LoginUserPerMinute:  loginUser,
		MaxKeys:             maxKeys,
		ClipBurst:           10,
		ClipTokensPerSecond: 2,
	}
	return &cfg
}

// ---- LoginLimiterTests ----

func TestIpLimitBlocksAfterExceeding(t *testing.T) {
	limiter := NewLimiter()
	now := time.Unix(1760000000, 0).UTC()
	cfg := limiterConfig(2, 2, 3)

	assert.True(t, limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, cfg))
	assert.True(t, limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, cfg))
	assert.False(t, limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, cfg))
}

func TestUserLimitBlocksAcrossIPs(t *testing.T) {
	limiter := NewLimiter()
	now := time.Unix(1760000000, 0).UTC()
	cfg := limiterConfig(2, 2, 3)

	assert.True(t, limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, cfg))
	assert.True(t, limiter.TryConsumeLoginLimit("2.2.2.2", "alice", now, cfg))
	assert.False(t, limiter.TryConsumeLoginLimit("3.3.3.3", "alice", now, cfg))
}

func TestSuccessResetsUserWindowButNotIPWindow(t *testing.T) {
	limiter := NewLimiter()
	now := time.Unix(1760000000, 0).UTC()
	cfg := limiterConfig(2, 2, 3)

	assert.True(t, limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, cfg))
	assert.True(t, limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, cfg))
	limiter.ResetUserWindow("alice")
	assert.False(t, limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, cfg))
}

func TestMaxKeysRejectsNewKeyWhenFull(t *testing.T) {
	limiter := NewLimiter()
	now := time.Unix(1760000000, 0).UTC()
	cfg := limiterConfig(2, 2, 4)

	assert.True(t, limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, cfg))
	assert.True(t, limiter.TryConsumeLoginLimit("2.2.2.2", "bob", now, cfg))
	assert.False(t, limiter.TryConsumeLoginLimit("3.3.3.3", "carol", now, cfg))
}

func TestExpiredEntriesAreLazilyRemoved(t *testing.T) {
	limiter := NewLimiter()
	now := time.Unix(1760000000, 0).UTC()
	later := now.Add(2 * time.Minute)
	cfg := limiterConfig(2, 2, 3)

	assert.True(t, limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, cfg))
	assert.True(t, limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, cfg))
	assert.False(t, limiter.TryConsumeLoginLimit("1.1.1.1", "alice", now, cfg))
	assert.True(t, limiter.TryConsumeLoginLimit("1.1.1.1", "alice", later, cfg))
}

func TestRemoveExpiredKeepsUnexpiredRetryAfterOlderEntry(t *testing.T) {
	limiter2 := NewLimiter()
	t0 := time.Unix(1760000000, 0).UTC()
	cfg := limiterConfig(3, 3, 10)

	// limit=3；同一 IP 在 t0 消费两次，t0+70s 消费一次。
	assert.True(t, limiter2.TryConsumeLoginLimit("1.1.1.1", "alice", t0, cfg))
	assert.True(t, limiter2.TryConsumeLoginLimit("1.1.1.1", "alice", t0, cfg))
	assert.True(t, limiter2.TryConsumeLoginLimit("1.1.1.1", "alice", t0.Add(70*time.Second), cfg))
	// t0+70s 时 t0 的两条已过期，仅剩一条（70s）→ 还可消费两次。
	assert.True(t, limiter2.TryConsumeLoginLimit("1.1.1.1", "alice", t0.Add(70*time.Second), cfg))
	assert.True(t, limiter2.TryConsumeLoginLimit("1.1.1.1", "alice", t0.Add(70*time.Second), cfg))
	assert.False(t, limiter2.TryConsumeLoginLimit("1.1.1.1", "alice", t0.Add(70*time.Second), cfg))
}

func TestRemoveExpiredDeletesKeyOnlyWhenQueueIsEmpty(t *testing.T) {
	limiter := NewLimiter()
	t0 := time.Unix(1760000000, 0).UTC()

	limiter.EnqueueForTest("ip:1.1.1.1", t0)
	limiter.EnqueueForTest("ip:1.1.1.1", t0.Add(40*time.Second))

	// cutoff t0+10s：首条过期，第二条未过期。
	limiter.RemoveExpiredForTest(t0.Add(10 * time.Second))
	assert.True(t, limiter.HasWindowKey("ip:1.1.1.1"))
	assert.Equal(t, 1, limiter.GetWindowCount("ip:1.1.1.1"))

	// cutoff t0+50s：全部过期。
	limiter.RemoveExpiredForTest(t0.Add(50 * time.Second))
	assert.False(t, limiter.HasWindowKey("ip:1.1.1.1"))
}

func TestLoginLimitsMustBePositive(t *testing.T) {
	baseConfig := config.Defaults()
	baseConfig.TokenSecret = make([]byte, 32)

	configZeroIP := baseConfig
	configZeroIP.RateLimit.LoginIpPerMinute = 0
	require.Error(t, (&configZeroIP).Validate())

	configZeroUser := baseConfig
	configZeroUser.RateLimit.LoginUserPerMinute = 0
	require.Error(t, (&configZeroUser).Validate())
}
