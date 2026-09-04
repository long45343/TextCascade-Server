// Package auth：token 半边（Auth.cs 的 TokenService）。
// PHC 无关；出站 payload 为固定字段序 sub/ver/iat/exp 的最小化 UTF-8 JSON
// （手写 marshal，非结构体反射）；入站校验用 jsonscan（MaxDepth=2）。
package auth

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"strconv"
	"strings"
	"time"

	"github.com/long45343/TextCascade-Server/internal/protocol"
	"github.com/long45343/TextCascade-Server/internal/users"
)

// TokenPayload 对应 C# TokenPayload record（Unix 秒）。
type TokenPayload struct {
	Subject       string
	Version       int64
	IssuedAtUnix  int64
	ExpiresAtUnix int64
}

// Token 对应 C# AuthToken record。
type Token struct {
	Payload      TokenPayload
	CompactToken string
}

// TokenService 对应 C# TokenService。
type TokenService struct {
	secret []byte
}

// NewTokenService 对应 C# 构造器：secret 不足 32 字节报错（C# 抛 ArgumentException）。
func NewTokenService(tokenSecret []byte) (*TokenService, error) {
	if tokenSecret == nil || len(tokenSecret) < 32 {
		return nil, fmt.Errorf("Token secret must be at least 32 bytes.")
	}
	cloned := make([]byte, len(tokenSecret))
	copy(cloned, tokenSecret)
	return &TokenService{secret: cloned}, nil
}

// CreateToken 对应 C# CreateToken。
func (s *TokenService) Create(user users.UserRecord, now time.Time, ttl time.Duration) Token {
	payload := CreateTokenPayload(user, now, ttl)
	return Token{Payload: payload, CompactToken: SignToken(payload, s.secret)}
}

// CreateTokenPayload 包级函数（C# 静态方法）；ttl ≤ 0 panic（C# ArgumentOutOfRangeException）。
func CreateTokenPayload(user users.UserRecord, now time.Time, ttl time.Duration) TokenPayload {
	if ttl <= 0 {
		panic("timeToLive must be positive")
	}
	return TokenPayload{
		Subject:       user.Username,
		Version:       user.TokenVersion,
		IssuedAtUnix:  now.Unix(),
		ExpiresAtUnix: now.Add(ttl).Unix(),
	}
}

// SignToken 包级函数（C# 静态方法）：HMAC-SHA256，固定字段序最小化 JSON。
func SignToken(payload TokenPayload, secret []byte) string {
	if secret == nil || len(secret) < 32 {
		panic("Token secret must be at least 32 bytes.")
	}

	// 与 C# Utf8JsonWriter 输出逐字节一致：{"sub":"...","ver":N,"iat":N,"exp":N}
	subjectBytes, err := json.Marshal(payload.Subject)
	if err != nil {
		panic(err)
	}
	payloadBytes := make([]byte, 0, len(subjectBytes)+64)
	payloadBytes = append(payloadBytes, `{"sub":`...)
	payloadBytes = append(payloadBytes, subjectBytes...)
	payloadBytes = append(payloadBytes, `,"ver":`...)
	payloadBytes = strconv.AppendInt(payloadBytes, payload.Version, 10)
	payloadBytes = append(payloadBytes, `,"iat":`...)
	payloadBytes = strconv.AppendInt(payloadBytes, payload.IssuedAtUnix, 10)
	payloadBytes = append(payloadBytes, `,"exp":`...)
	payloadBytes = strconv.AppendInt(payloadBytes, payload.ExpiresAtUnix, 10)
	payloadBytes = append(payloadBytes, '}')

	mac := hmac.New(sha256.New, secret)
	mac.Write(payloadBytes)
	signature := mac.Sum(nil)

	payloadSegment := base64.RawURLEncoding.EncodeToString(payloadBytes)
	signatureSegment := base64.RawURLEncoding.EncodeToString(signature)
	return payloadSegment + "." + signatureSegment
}

// VerifyToken 对应 C# 布尔重载。
func (s *TokenService) VerifyToken(compactToken string, now time.Time, userLookup map[string]users.UserRecord) bool {
	_, ok := s.TryVerifyToken(compactToken, now, userLookup)
	return ok
}

// TryVerifyToken 合并 C# 实例/静态双载（Go 无 out 参数 → 多返回值）。
// 顺序：验签 → 验过期 → 验用户存在 → 验 tokenVersion。
func (s *TokenService) TryVerifyToken(compactToken string, now time.Time, userLookup map[string]users.UserRecord) (TokenPayload, bool) {
	return tryVerifyInternal(compactToken, s.secret, now, userLookup)
}

func tryVerifyInternal(compactToken string, secret []byte, now time.Time, userLookup map[string]users.UserRecord) (TokenPayload, bool) {
	if len(compactToken) == 0 || len(compactToken) > 8192 {
		return TokenPayload{}, false
	}

	parts := strings.Split(compactToken, ".")
	if len(parts) != 2 {
		return TokenPayload{}, false
	}

	payloadBytes, err := base64.RawURLEncoding.DecodeString(parts[0])
	if err != nil || len(payloadBytes) == 0 {
		return TokenPayload{}, false
	}
	signatureBytes, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil || len(signatureBytes) == 0 || len(signatureBytes) != 32 {
		return TokenPayload{}, false
	}

	mac := hmac.New(sha256.New, secret)
	mac.Write(payloadBytes)
	expectedSignature := mac.Sum(nil)
	if !hmac.Equal(signatureBytes, expectedSignature) {
		return TokenPayload{}, false
	}

	candidate, ok := parseTokenPayload(payloadBytes)
	if !ok {
		return TokenPayload{}, false
	}

	if candidate.ExpiresAtUnix <= candidate.IssuedAtUnix || now.Unix() >= candidate.ExpiresAtUnix {
		return TokenPayload{}, false
	}

	user, found := userLookup[candidate.Subject]
	if !found || user.Disabled || user.TokenVersion != candidate.Version {
		return TokenPayload{}, false
	}

	return candidate, true
}

// parseTokenPayload 复刻 C# JsonDocument.Parse(MaxDepth=2) + 恰好 4 个属性 +
// sub/ver/iat/exp 全部存在 + TryParsePositiveInteger 的行为。
func parseTokenPayload(payload []byte) (TokenPayload, bool) {
	root, err := protocol.Decode(payload, 2)
	if err != nil {
		return TokenPayload{}, false
	}
	if !root.IsObject() || root.Len() != 4 {
		return TokenPayload{}, false
	}

	subjectValue := root.Get("sub")
	if subjectValue == nil || !subjectValue.IsString() || subjectValue.Str() == "" {
		return TokenPayload{}, false
	}
	version, ok := tryParsePositiveInteger(root, "ver")
	if !ok {
		return TokenPayload{}, false
	}
	issuedAt, ok := tryParsePositiveInteger(root, "iat")
	if !ok {
		return TokenPayload{}, false
	}
	expiresAt, ok := tryParsePositiveInteger(root, "exp")
	if !ok {
		return TokenPayload{}, false
	}

	return TokenPayload{
		Subject:       subjectValue.Str(),
		Version:       version,
		IssuedAtUnix:  issuedAt,
		ExpiresAtUnix: expiresAt,
	}, true
}

// tryParsePositiveInteger 拒绝负数/零（C# 语义：> 0）；扫描层已拒绝小数/指数形态。
func tryParsePositiveInteger(root *protocol.Node, name string) (int64, bool) {
	element := root.Get(name)
	if element == nil || !element.IsNumber() {
		return 0, false
	}
	value, err := strconv.ParseInt(element.RawNumber(), 10, 64)
	if err != nil || value <= 0 {
		return 0, false
	}
	return value, true
}
