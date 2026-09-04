// Package auth 是 Auth.cs（argon2.go / token.go）与 AuthService.cs（login.go）的迁移。
// 本文件为 Argon2 哈希器半边：x/crypto/argon2（Argon2id），PHC 编码自洽（Q6：
// 不与 Isopoh 兼容，迁移时重置全部存量密码，见 spec §11 runbook）。
package auth

import (
	"crypto/rand"
	"crypto/subtle"
	"encoding/base64"
	"fmt"
	"regexp"
	"strconv"
	"strings"

	"golang.org/x/crypto/argon2"
)

// Params 对应 C# Argon2Config（仅协议用到的三个参数）。
type Params struct {
	MemoryKiB   int
	Iterations  int
	Parallelism int
}

// PasswordHasher 对应 C# IPasswordHasher。
type PasswordHasher interface {
	Hash(password string, params Params) string
	Verify(password, encodedHash string) bool
	NeedsRehash(encodedHash string, params Params) bool
}

// Argon2Hasher 对应 C# Argon2PasswordHasher。
type Argon2Hasher struct{}

// NewArgon2Hasher 构造生产哈希器。
func NewArgon2Hasher() *Argon2Hasher { return &Argon2Hasher{} }

const (
	argon2KeyLen  = 32
	argon2SaltLen = 16
	argon2Version = 19
)

var phcParamRe = regexp.MustCompile(`^m=(\d+),t=(\d+),p=(\d+)$`)

// Hash 生成 `$argon2id$v=19$m=M,t=T,p=P$<b64salt>$<b64hash>`（RawURL 无填充……
// 实际上 PHC 惯例为标准 base64 无填充，与 users.json 校验正则 [A-Za-z0-9+/=] 相容）。
func (Argon2Hasher) Hash(password string, params Params) string {
	salt := make([]byte, argon2SaltLen)
	if _, err := rand.Read(salt); err != nil {
		panic(fmt.Sprintf("failed to generate Argon2 salt: %v", err))
	}
	key := argon2.IDKey([]byte(password), salt, uint32(params.Iterations), uint32(params.MemoryKiB), uint8(params.Parallelism), argon2KeyLen)
	encoded := "$argon2id$v=" + strconv.Itoa(argon2Version) +
		"$m=" + strconv.Itoa(params.MemoryKiB) +
		",t=" + strconv.Itoa(params.Iterations) +
		",p=" + strconv.Itoa(params.Parallelism) +
		"$" + base64.RawStdEncoding.EncodeToString(salt) +
		"$" + base64.RawStdEncoding.EncodeToString(key)
	return encoded
}

// Verify 解析 PHC 串并常数时间比较；任何解析失败一律 false（等价 C# 捕获
// ArgumentException/FormatException）。
func (Argon2Hasher) Verify(password, encodedHash string) bool {
	salt, key, params, err := decodePHC(encodedHash)
	if err != nil {
		return false
	}
	computed := argon2.IDKey([]byte(password), salt, uint32(params.Iterations), uint32(params.MemoryKiB), uint8(params.Parallelism), uint32(len(key)))
	return subtle.ConstantTimeCompare(computed, key) == 1
}

// NeedsRehash 语义与 C# 一致：参数不一致 → 登录响应携带 needsRehash，不重写文件。
func (h Argon2Hasher) NeedsRehash(encodedHash string, params Params) bool {
	return NeedsRehash(encodedHash, params)
}

// NeedsRehash 包级函数（合并 C# 实例/静态双载）。
func NeedsRehash(encodedHash string, params Params) bool {
	if encodedHash == "" || !strings.HasPrefix(encodedHash, "$argon2id$") {
		return true
	}

	segments := strings.Split(encodedHash, "$")
	if len(segments) < 4 {
		return true
	}

	// segments[0] 为空（前导 $）、[1] 为 "argon2id"、[2] 为 "v=N"、[3] 为 "m=M,t=T,p=P"。
	match := phcParamRe.FindStringSubmatch(segments[3])
	if match == nil {
		return true
	}
	storedMemory, err1 := strconv.Atoi(match[1])
	storedTime, err2 := strconv.Atoi(match[2])
	storedThreads, err3 := strconv.Atoi(match[3])
	if err1 != nil || err2 != nil || err3 != nil {
		return true
	}

	return storedMemory != params.MemoryKiB || storedTime != params.Iterations || storedThreads != params.Parallelism
}

func decodePHC(encodedHash string) (salt, key []byte, params Params, err error) {
	segments := strings.Split(encodedHash, "$")
	if len(segments) != 6 || segments[0] != "" || segments[1] != "argon2id" {
		return nil, nil, Params{}, fmt.Errorf("invalid PHC string")
	}

	var version int
	if _, err := fmt.Sscanf(segments[2], "v=%d", &version); err != nil || version != argon2Version {
		return nil, nil, Params{}, fmt.Errorf("unsupported Argon2 version")
	}

	match := phcParamRe.FindStringSubmatch(segments[3])
	if match == nil {
		return nil, nil, Params{}, fmt.Errorf("invalid Argon2 parameters")
	}
	memory, err1 := strconv.Atoi(match[1])
	timeCost, err2 := strconv.Atoi(match[2])
	threads, err3 := strconv.Atoi(match[3])
	if err1 != nil || err2 != nil || err3 != nil {
		return nil, nil, Params{}, fmt.Errorf("invalid Argon2 parameters")
	}

	salt, err = decodePHCBase64(segments[4])
	if err != nil {
		return nil, nil, Params{}, err
	}
	key, err = decodePHCBase64(segments[5])
	if err != nil {
		return nil, nil, Params{}, err
	}

	return salt, key, Params{MemoryKiB: memory, Iterations: timeCost, Parallelism: threads}, nil
}

// decodePHCBase64 接受标准字母表的无填充与带填充两种形态
// （users.json 校验正则允许 '='，历史文件可能带填充）。
func decodePHCBase64(segment string) ([]byte, error) {
	if decoded, err := base64.RawStdEncoding.DecodeString(segment); err == nil {
		return decoded, nil
	}
	return base64.StdEncoding.DecodeString(segment)
}
