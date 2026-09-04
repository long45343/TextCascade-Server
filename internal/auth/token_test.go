package auth

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"strings"
	"testing"
	"time"

	"github.com/long45343/TextCascade-Server/internal/users"
	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

var testSecret = []byte(strings.Repeat("k", 32))

func testUser(username string, version int64) users.UserRecord {
	return users.UserRecord{Username: username, PasswordHash: "$argon2id$v=19$m=19456,t=2,p=1$c2FsdA$hash", TokenVersion: version}
}

func testLookup(userList ...users.UserRecord) map[string]users.UserRecord {
	lookup := make(map[string]users.UserRecord, len(userList))
	for _, u := range userList {
		lookup[u.Username] = u
	}
	return lookup
}

func newService(t *testing.T) *TokenService {
	t.Helper()
	service, err := NewTokenService(testSecret)
	require.NoError(t, err)
	return service
}

// ---- TokenServiceTests ----

func TestRoundTrip(t *testing.T) {
	now := time.Unix(1760000000, 0).UTC()
	service := newService(t)
	token := service.Create(testUser("alice", 1), now, 30*24*time.Hour)
	payload, ok := service.TryVerifyToken(token.CompactToken, now, testLookup(testUser("alice", 1)))
	require.True(t, ok)
	assert.Equal(t, "alice", payload.Subject)
	assert.EqualValues(t, 1, payload.Version)
}

func TestRejectsExpiredToken(t *testing.T) {
	now := time.Unix(1760000000, 0).UTC()
	service := newService(t)
	token := service.Create(testUser("alice", 1), now, 10*time.Second)
	later := now.Add(20 * time.Second)
	_, ok := service.TryVerifyToken(token.CompactToken, later, testLookup(testUser("alice", 1)))
	assert.False(t, ok)
}

func TestRejectsWhenTokenVersionChanged(t *testing.T) {
	now := time.Unix(1760000000, 0).UTC()
	service := newService(t)
	token := service.Create(testUser("alice", 1), now, 30*24*time.Hour)
	_, ok := service.TryVerifyToken(token.CompactToken, now, testLookup(testUser("alice", 2)))
	assert.False(t, ok)
}

func TestRejectsWhenUserDisabled(t *testing.T) {
	now := time.Unix(1760000000, 0).UTC()
	service := newService(t)
	token := service.Create(testUser("alice", 1), now, 30*24*time.Hour)
	disabled := testUser("alice", 1)
	disabled.Disabled = true
	_, ok := service.TryVerifyToken(token.CompactToken, now, testLookup(disabled))
	assert.False(t, ok)
}

func TestRejectsWhenUserMissing(t *testing.T) {
	now := time.Unix(1760000000, 0).UTC()
	service := newService(t)
	token := service.Create(testUser("alice", 1), now, 30*24*time.Hour)
	_, ok := service.TryVerifyToken(token.CompactToken, now, testLookup())
	assert.False(t, ok)
}

func TestRejectsTamperedSignature(t *testing.T) {
	now := time.Unix(1760000000, 0).UTC()
	service := newService(t)
	token := service.Create(testUser("alice", 1), now, 30*24*time.Hour)
	last := token.CompactToken[len(token.CompactToken)-1]
	replacement := byte('b')
	if last == 'b' {
		replacement = 'a'
	}
	tampered := token.CompactToken[:len(token.CompactToken)-1] + string(replacement)
	_, ok := service.TryVerifyToken(tampered, now, testLookup(testUser("alice", 1)))
	assert.False(t, ok)
}

func TestRejectsUnknownFields(t *testing.T) {
	now := time.Unix(1760000000, 0).UTC()
	payloadJSON := "{\"sub\":\"alice\",\"ver\":1,\"iat\":1760000000,\"exp\":1762592000,\"extra\":5}"
	payloadBytes := []byte(payloadJSON)
	mac := hmac.New(sha256.New, testSecret)
	mac.Write(payloadBytes)
	signature := mac.Sum(nil)
	token := base64Url(payloadBytes) + "." + base64Url(signature)
	_, ok := newService(t).TryVerifyToken(token, now, testLookup(testUser("alice", 1)))
	assert.False(t, ok)
}

func base64Url(b []byte) string {
	return base64.RawURLEncoding.EncodeToString(b)
}

// ---- AuthDeepTests（U1–U11）----

func compactFromPayloadJSON(payloadJSON string, secret []byte) string {
	payloadBytes := []byte(payloadJSON)
	mac := hmac.New(sha256.New, secret)
	mac.Write(payloadBytes)
	signature := mac.Sum(nil)
	return base64Url(payloadBytes) + "." + base64Url(signature)
}

func verifyPayloadJSON(t *testing.T, payloadJSON string, expected *TokenPayload) bool {
	t.Helper()
	token := compactFromPayloadJSON(payloadJSON, testSecret)
	now := time.Unix(1760000001, 0).UTC()
	actual, ok := newService(t).TryVerifyToken(token, now, testLookup(testUser("alice", 1)))
	if !ok {
		return false
	}
	if expected == nil {
		return true
	}
	return actual.Subject == expected.Subject &&
		actual.Version == expected.Version &&
		actual.IssuedAtUnix == expected.IssuedAtUnix &&
		actual.ExpiresAtUnix == expected.ExpiresAtUnix
}

// U1
func TestSignTokenFieldOrderAndMinimalJSON(t *testing.T) {
	payload := TokenPayload{Subject: "alice", Version: 1, IssuedAtUnix: 1760000000, ExpiresAtUnix: 1762592000}
	compact := SignToken(payload, testSecret)
	segment := strings.SplitN(compact, ".", 2)[0]
	payloadBytes, err := base64.RawURLEncoding.DecodeString(segment)
	require.NoError(t, err)
	assert.Equal(t, `{"sub":"alice","ver":1,"iat":1760000000,"exp":1762592000}`, string(payloadBytes))
}

// U2
func TestVerifyTokenRejectsDuplicateFields(t *testing.T) {
	assert.False(t, verifyPayloadJSON(t, `{"sub":"alice","ver":1,"ver":1,"iat":1760000000,"exp":1762592000}`, nil))
	assert.False(t, verifyPayloadJSON(t, `{"sub":"alice","sub":"alice","ver":1,"iat":1760000000,"exp":1762592000}`, nil))
}

// U3
func TestVerifyTokenRejectsUnknownField(t *testing.T) {
	assert.False(t, verifyPayloadJSON(t, `{"sub":"alice","ver":1,"iat":1760000000,"exp":1762592000,"aud":"x"}`, nil))
}

// U4
func TestVerifyTokenRejectsFractionNumber(t *testing.T) {
	assert.False(t, verifyPayloadJSON(t, `{"sub":"alice","ver":1,"iat":1760000000.0,"exp":1762592000}`, nil))
	assert.False(t, verifyPayloadJSON(t, `{"sub":"alice","ver":1.0,"iat":1760000000,"exp":1762592000}`, nil))
}

// U5
func TestVerifyTokenRejectsStringNumber(t *testing.T) {
	assert.False(t, verifyPayloadJSON(t, `{"sub":"alice","ver":1,"iat":1760000000,"exp":"1762592000"}`, nil))
}

// U6
func TestVerifyTokenRejectsNegativeValue(t *testing.T) {
	assert.False(t, verifyPayloadJSON(t, `{"sub":"alice","ver":-1,"iat":1760000000,"exp":1762592000}`, nil))
	assert.False(t, verifyPayloadJSON(t, `{"sub":"alice","ver":1,"iat":-1760000000,"exp":1762592000}`, nil))
}

// U7
func TestVerifyTokenRejectsExpNotAfterIat(t *testing.T) {
	assert.False(t, verifyPayloadJSON(t, `{"sub":"alice","ver":1,"iat":1762592000,"exp":1760000000}`, nil))
	assert.False(t, verifyPayloadJSON(t, `{"sub":"alice","ver":1,"iat":1760000000,"exp":1760000000}`, nil))
}

// U8
func TestVerifyTokenRejectsZeroIat(t *testing.T) {
	assert.False(t, verifyPayloadJSON(t, `{"sub":"alice","ver":1,"iat":0,"exp":1762592000}`, nil))
}

// U9
func TestVerifyTokenRoundTripInstanceOverload(t *testing.T) {
	now := time.Unix(1760000000, 0).UTC()
	service := newService(t)
	token := service.Create(testUser("alice", 1), now, 30*24*time.Hour)

	payload, ok := service.TryVerifyToken(token.CompactToken, now, testLookup(testUser("alice", 1)))
	require.True(t, ok)
	assert.Equal(t, "alice", payload.Subject)
	assert.EqualValues(t, 1, payload.Version)
	assert.EqualValues(t, 1760000000, payload.IssuedAtUnix)
	assert.EqualValues(t, 1762592000, payload.ExpiresAtUnix)
}

// U10
func TestNeedsRehashParameterParsing(t *testing.T) {
	encoded := "$argon2id$v=19$m=19456,t=2,p=1$c2FsdA$aG9zdA"

	assert.False(t, NeedsRehash(encoded, Params{MemoryKiB: 19456, Iterations: 2, Parallelism: 1}))
	assert.True(t, NeedsRehash(encoded, Params{MemoryKiB: 1024, Iterations: 2, Parallelism: 1}))
	assert.True(t, NeedsRehash(encoded, Params{MemoryKiB: 19456, Iterations: 3, Parallelism: 1}))
	assert.True(t, NeedsRehash(encoded, Params{MemoryKiB: 19456, Iterations: 2, Parallelism: 4}))
	assert.True(t, NeedsRehash("", Params{MemoryKiB: 19456, Iterations: 2, Parallelism: 1}))
	assert.True(t, NeedsRehash("$argon2i$v=19$m=19456,t=2,p=1$c2FsdA$aG9zdA", Params{MemoryKiB: 19456, Iterations: 2, Parallelism: 1}))
	assert.True(t, NeedsRehash("not-a-hash", Params{MemoryKiB: 19456, Iterations: 2, Parallelism: 1}))
}

// NewTokenService 拒绝短 secret（C# 构造器抛 ArgumentException）。
func TestNewTokenServiceRejectsShortSecret(t *testing.T) {
	_, err := NewTokenService([]byte("short"))
	assert.Error(t, err)
}

// Argon2 三函数冒烟（轻量参数）。
func TestArgon2HashVerifyRoundTrip(t *testing.T) {
	hasher := Argon2Hasher{}
	params := Params{MemoryKiB: 1024, Iterations: 1, Parallelism: 1}
	encoded := hasher.Hash("s3cret", params)
	assert.True(t, strings.HasPrefix(encoded, "$argon2id$v=19$m=1024,t=1,p=1$"))
	assert.True(t, hasher.Verify("s3cret", encoded))
	assert.False(t, hasher.Verify("wrong", encoded))
	assert.False(t, hasher.Verify("s3cret", "garbage"))
	assert.True(t, NeedsRehash(encoded, Params{MemoryKiB: 2048, Iterations: 1, Parallelism: 1}))
	assert.False(t, NeedsRehash(encoded, params))
}

// json.Marshal 与 C# Utf8JsonWriter 对本协议字符串输出一致性抽查。
func TestSubjectJSONEscaping(t *testing.T) {
	subject := "a\"b"
	subjectBytes, err := json.Marshal(subject)
	require.NoError(t, err)
	assert.Equal(t, `"a\"b"`, string(subjectBytes))
}
