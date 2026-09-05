package auth_test

import (
	"encoding/base64"
	"strconv"
	"testing"

	"golang.org/x/crypto/argon2"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"

	"github.com/long45343/TextCascade-Server/internal/auth"
)

// Isopoh 互通契约（spec §3.1）：
// 以下 PHC 字面量由 C# 版 Isopoh.Cryptography.Argon2 2.0.0 机器生成（2026-09-05），
// 且逐一在 C# 侧用 Argon2.Verify 验证过；Go 版必须能验证同样的字节。
// 任何一侧的编码或计算行为漂移都会使本用例失败。
//
// 生成参数：password = "pw-测试-123"（UTF-8），salt = "saltsaltsaltsalt"（16 字节），
// m=19456, t=2；Isopoh 忽略配置的 Threads 并自行选择 lane 数（此处写入 p=4），
// 但计算符合标准 Argon2id 并如实记录于 PHC 串。
const (
	isopohProduced = "$argon2id$v=19$m=19456,t=2,p=4$c2FsdHNhbHRzYWx0c2FsdA$RVVwrOHkYiiaAiDruSRXQ6rU7Sn5SbeGKYnX9oiUPa8"
	// goProduced 由 Go 版 auth.Hash 用相同 password/salt/m/t 与 p=1 生成，
	// 并已在 C# 侧用 Isopoh.Argon2.Verify 验证通过。
	goProduced = "$argon2id$v=19$m=19456,t=2,p=1$c2FsdHNhbHRzYWx0c2FsdA$9D8y1hjP08cRrEr+NPJ9T/ImaaNx54+1TX8KQ5PvcYs"
)

const interopPassword = "pw-测试-123"

// Go 版验证 C# (Isopoh) 生成的存量哈希——存量 users.json 兼容的契约锚点。
func TestVerifyIsopohProducedHash(t *testing.T) {
	hasher := auth.NewArgon2Hasher()
	require.True(t, hasher.Verify(interopPassword, isopohProduced))
	assert.False(t, hasher.Verify("wrong-password", isopohProduced))
}

// Go 版自身编码的哈希（该字节串已经 C# Isopoh 验证通过）——编码器字节稳定性的契约锚点。
func TestVerifyGoProducedHashPinnedAgainstIsopoh(t *testing.T) {
	hasher := auth.NewArgon2Hasher()
	require.True(t, hasher.Verify(interopPassword, goProduced))
	assert.False(t, hasher.Verify("wrong-password", goProduced))
}

// 字节级等价锚点：固定盐下 x/crypto 的 Argon2id 计算结果必须与钉住的
// C# (Isopoh) 产物逐字节一致（p=4 与 p=1 双参）——这是"同一算法、同一字节"的核心证明。
func TestHashBytesMatchPinnedIsopohOutput(t *testing.T) {
	salt := []byte("saltsaltsaltsalt")
	password := []byte(interopPassword)

	for _, tc := range []struct {
		parallelism uint8
		pinned      string
	}{
		{4, isopohProduced},
		{1, goProduced},
	} {
		key := argon2.IDKey(password, salt, 2, 19456, tc.parallelism, 32)
		recomputed := "$argon2id$v=19$m=19456,t=2,p=" + strconv.Itoa(int(tc.parallelism)) +
			"$" + base64.RawStdEncoding.EncodeToString(salt) +
			"$" + base64.RawStdEncoding.EncodeToString(key)
		assert.Equal(t, tc.pinned, recomputed)
	}
}
