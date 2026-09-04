package auth_test

import (
	"regexp"
	"strconv"
	"testing"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"

	"github.com/long45343/TextCascade-Server/internal/auth"
	"github.com/long45343/TextCascade-Server/internal/config"
)

// ---- SlowHashSmokeTests（生产参数真实 Argon2id）----

func productionParams() auth.Params {
	cfg := config.Defaults()
	return auth.Params{MemoryKiB: cfg.Auth.Argon2MemoryKiB, Iterations: cfg.Auth.Argon2Iterations, Parallelism: cfg.Auth.Argon2Parallelism}
}

var paramRe = regexp.MustCompile(`m=(\d+),t=(\d+),p=(\d+)`)

func parseEncodedParameters(t *testing.T, encoded string) (int, int, int) {
	t.Helper()
	match := paramRe.FindStringSubmatch(encoded)
	require.NotNil(t, match, "Not an Argon2 PHC string: %s", encoded)
	memory, _ := strconv.Atoi(match[1])
	timeCost, _ := strconv.Atoi(match[2])
	threads, _ := strconv.Atoi(match[3])
	return memory, timeCost, threads
}

// U26
func TestSlowHash_ThenVerifyRoundTrip(t *testing.T) {
	hasher := auth.NewArgon2Hasher()
	encoded := hasher.Hash("correct horse battery staple", productionParams())

	assert.True(t, len(encoded) >= 10 && encoded[:10] == "$argon2id$", encoded)
	assert.True(t, hasher.Verify("correct horse battery staple", encoded))
	assert.False(t, hasher.Verify("wrong password", encoded))
}

// U27
func TestSlowHash_NeedsRehashMatchingParamsReturnsFalse(t *testing.T) {
	hasher := auth.NewArgon2Hasher()
	encoded := hasher.Hash("some-password", productionParams())
	memory, timeCost, threads := parseEncodedParameters(t, encoded)

	assert.False(t, auth.NeedsRehash(encoded, auth.Params{MemoryKiB: memory, Iterations: timeCost, Parallelism: threads}))
	assert.True(t, auth.NeedsRehash(encoded, auth.Params{MemoryKiB: memory + 1024, Iterations: timeCost, Parallelism: threads}))
	assert.True(t, auth.NeedsRehash(encoded, auth.Params{MemoryKiB: memory, Iterations: timeCost + 1, Parallelism: threads}))
	assert.True(t, auth.NeedsRehash(encoded, auth.Params{MemoryKiB: memory, Iterations: timeCost, Parallelism: threads + 1}))
}

// U28
func TestSlowHash_NeedsRehashStaleParamsReturnsTrue(t *testing.T) {
	hasher := auth.NewArgon2Hasher()
	encoded := hasher.Hash("some-password", productionParams())
	memory, timeCost, threads := parseEncodedParameters(t, encoded)

	staleMemory := memory / 8
	if staleMemory < 1024 {
		staleMemory = 1024
	}
	stale := replaceOnce(encoded, "m="+strconv.Itoa(memory), "m="+strconv.Itoa(staleMemory))
	stale = replaceOnce(stale, "t="+strconv.Itoa(timeCost), "t=1")
	stale = replaceOnce(stale, "p="+strconv.Itoa(threads), "p=1")
	assert.NotEqual(t, encoded, stale)

	assert.True(t, auth.NeedsRehash(stale, auth.Params{MemoryKiB: memory, Iterations: timeCost, Parallelism: threads}))
}

func replaceOnce(s, old, new string) string {
	idx := indexOf(s, old)
	if idx < 0 {
		return s
	}
	return s[:idx] + new + s[idx+len(old):]
}

func indexOf(s, sub string) int {
	for i := 0; i+len(sub) <= len(s); i++ {
		if s[i:i+len(sub)] == sub {
			return i
		}
	}
	return -1
}
