package auth_test

import (
	"bytes"
	"context"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"

	"github.com/long45343/TextCascade-Server/internal/auth"
	"github.com/long45343/TextCascade-Server/internal/clock"
	"github.com/long45343/TextCascade-Server/internal/config"
	"github.com/long45343/TextCascade-Server/internal/state"
	"github.com/long45343/TextCascade-Server/internal/sync"
	"github.com/long45343/TextCascade-Server/internal/users"
)

// authRecordingHasher 对应 C# RecordingHasher。
type authRecordingHasher struct {
	authHashCalls []authHashCall
	verifyPairs   [][2]string
	dummyReturn   string
}

type authHashCall struct {
	password string
	params   auth.Params
}

func newAuthRecordingHasher() *authRecordingHasher {
	return &authRecordingHasher{dummyReturn: "$argon2id$v=19$m=19456,t=2,p=1$dummy"}
}

func (h *authRecordingHasher) Hash(password string, params auth.Params) string {
	h.authHashCalls = append(h.authHashCalls, authHashCall{password: password, params: params})
	return h.dummyReturn
}

func (h *authRecordingHasher) Verify(password, encodedHash string) bool {
	h.verifyPairs = append(h.verifyPairs, [2]string{password, encodedHash})
	return password == "correct-password" && encodedHash == "valid-hash"
}

func (h *authRecordingHasher) NeedsRehash(encodedHash string, params auth.Params) bool { return false }

var _ auth.PasswordHasher = (*authRecordingHasher)(nil)

// captureHandler 记录全部日志消息（对应 C# TestLogger）。
type captureHandler struct {
	messages *[]string
}

func (h *captureHandler) Enabled(_ context.Context, level slog.Level) bool {
	return level >= slog.LevelInfo
}

func (h *captureHandler) Handle(_ context.Context, r slog.Record) error {
	*h.messages = append(*h.messages, r.Message)
	return nil
}

func (h *captureHandler) WithAttrs(attrs []slog.Attr) slog.Handler { return h }

func (h *captureHandler) WithGroup(name string) slog.Handler { return h }

func newCaptureLogger() (*slog.Logger, *[]string) {
	messages := &[]string{}
	return slog.New(&captureHandler{messages: messages}), messages
}

func loginTestConfig() *config.RuntimeConfig {
	cfg := config.Defaults()
	cfg.TokenSecret = make([]byte, 32)
	return &cfg
}

func newLoginServer(t *testing.T, cfg *config.RuntimeConfig, hasher auth.PasswordHasher, f *users.UsersFile) (*sync.Server, func()) {
	t.Helper()
	tempState := filepath.Join(t.TempDir(), "state.json")
	store, err := state.NewStore(tempState, 0, nil)
	require.NoError(t, err)
	srv := sync.New(cfg, f, store, hasher, clock.System, silentLogger())
	return srv, func() { store.Stop() }
}

func silentLogger() *slog.Logger {
	return slog.New(slog.NewTextHandler(&bytes.Buffer{}, nil))
}

func doLogin(cfg *config.RuntimeConfig, deps *auth.LoginDeps, username, password string) *httptest.ResponseRecorder {
	body := `{"username":"` + username + `","password":"` + password + `"}`
	req := httptest.NewRequest(http.MethodPost, "/api/v1/login", strings.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	rec := httptest.NewRecorder()
	auth.HandleLogin(rec, req, cfg, deps)
	return rec
}

// LoginVerificationRunsForMissingUser：缺失用户也执行哈希验证（时序侧信道对齐）。
func TestLoginVerificationRunsForMissingUser(t *testing.T) {
	hasher := newAuthRecordingHasher()
	cfg := loginTestConfig()
	f := &users.UsersFile{NextTokenVersion: 2, Users: []users.UserRecord{
		{Username: "alice", PasswordHash: "valid-hash", TokenVersion: 1},
	}}
	srv, cleanup := newLoginServer(t, cfg, hasher, f)
	defer cleanup()

	require.Equal(t, hasher.dummyReturn, srv.LoginDeps().DummyHash)

	// 1. Missing user
	rec := doLogin(cfg, srv.LoginDeps(), "missing", "any-password")
	assert.Equal(t, http.StatusUnauthorized, rec.Code)
	require.Len(t, hasher.verifyPairs, 1)
	assert.Equal(t, hasher.dummyReturn, hasher.verifyPairs[0][1])
	assert.Equal(t, "any-password", hasher.verifyPairs[0][0])

	// 2. Existing user alice with wrong password
	rec = doLogin(cfg, srv.LoginDeps(), "alice", "wrong-password")
	assert.Equal(t, http.StatusUnauthorized, rec.Code)
	require.Len(t, hasher.verifyPairs, 2)
	assert.Equal(t, "valid-hash", hasher.verifyPairs[1][1])
	assert.Equal(t, "wrong-password", hasher.verifyPairs[1][0])
}

// LoginDummyHashUsesConfiguredArgon2Parameters。
func TestLoginDummyHashUsesConfiguredArgon2Parameters(t *testing.T) {
	hasher := newAuthRecordingHasher()
	cfg := loginTestConfig()
	cfg.Auth = config.AuthConfig{TokenTtlDays: 30, TokenSecretEnv: "TEST_SECRET", Argon2MemoryKiB: 32768, Argon2Iterations: 4, Argon2Parallelism: 2}
	f := &users.UsersFile{}
	srv, cleanup := newLoginServer(t, cfg, hasher, f)
	defer cleanup()

	require.Len(t, hasher.authHashCalls, 1)
	call := hasher.authHashCalls[0]
	assert.Equal(t, "textcascade-login-timing-dummy", call.password)
	assert.Equal(t, 32768, call.params.MemoryKiB)
	assert.Equal(t, 4, call.params.Iterations)
	assert.Equal(t, 2, call.params.Parallelism)
	assert.Equal(t, hasher.dummyReturn, srv.LoginDeps().DummyHash)
}

// DisabledUserStillReturnsUnifiedInvalidCredentials：禁用用户返回统一 401，
// 日志不出现 disabled。
func TestDisabledUserStillReturnsUnifiedInvalidCredentials(t *testing.T) {
	hasher := newAuthRecordingHasher()
	cfg := loginTestConfig()
	f := &users.UsersFile{NextTokenVersion: 2, Users: []users.UserRecord{
		{Username: "dave", PasswordHash: "valid-hash", TokenVersion: 1, Disabled: true},
	}}
	logger, messages := newCaptureLogger()
	srv, cleanup := newLoginServer(t, cfg, hasher, f)
	defer cleanup()
	// 以捕获日志器重建 server
	srv = sync.New(cfg, f, mustStore(t), hasher, clock.System, logger)
	t.Cleanup(func() {})
	_ = srv

	rec := doLogin(cfg, srv.LoginDeps(), "dave", "correct-password")

	assert.Equal(t, http.StatusUnauthorized, rec.Code)
	assert.Contains(t, rec.Body.String(), "invalid_credentials")

	assert.NotEmpty(t, *messages)
	for _, log := range *messages {
		assert.NotContains(t, strings.ToLower(log), "reason=disabled")
		assert.NotContains(t, strings.ToLower(log), "disabled")
	}
}

func mustStore(t *testing.T) *state.Store {
	t.Helper()
	store, err := state.NewStore(filepath.Join(t.TempDir(), "state.json"), 0, nil)
	require.NoError(t, err)
	return store
}

// 登录限速触发 429（rate_limited）。
func TestLoginRateLimited(t *testing.T) {
	hasher := newAuthRecordingHasher()
	cfg := loginTestConfig()
	cfg.RateLimit.LoginIpPerMinute = 1
	cfg.RateLimit.LoginUserPerMinute = 1
	f := &users.UsersFile{NextTokenVersion: 2, Users: []users.UserRecord{
		{Username: "alice", PasswordHash: "valid-hash", TokenVersion: 1},
	}}
	srv, cleanup := newLoginServer(t, cfg, hasher, f)
	defer cleanup()

	rec := doLogin(cfg, srv.LoginDeps(), "alice", "correct-password")
	assert.Equal(t, http.StatusOK, rec.Code)

	rec = doLogin(cfg, srv.LoginDeps(), "alice", "correct-password")
	assert.Equal(t, http.StatusTooManyRequests, rec.Code)
	assert.Contains(t, rec.Body.String(), "rate_limited")
}

// 成功登录返回 token 契约形态。
func TestLoginSuccessResponse(t *testing.T) {
	hasher := newAuthRecordingHasher()
	cfg := loginTestConfig()
	f := &users.UsersFile{NextTokenVersion: 2, Users: []users.UserRecord{
		{Username: "alice", PasswordHash: "valid-hash", TokenVersion: 1},
	}}
	srv, cleanup := newLoginServer(t, cfg, hasher, f)
	defer cleanup()

	rec := doLogin(cfg, srv.LoginDeps(), "alice", "correct-password")
	require.Equal(t, http.StatusOK, rec.Code)
	body := rec.Body.String()
	assert.Contains(t, body, `"token":`)
	assert.Contains(t, body, `"expiresAtUtc":"`)
	assert.Contains(t, body, `"protocolVersion":1`)
	assert.Contains(t, body, `"maxTextBytes":`)
	assert.Contains(t, body, `"helloTimeoutSeconds":`)
	assert.Contains(t, body, `"heartbeatIntervalSeconds":`)
	assert.Contains(t, body, `"heartbeatTimeoutSeconds":`)
	assert.NotContains(t, body, "needsRehash")
	// expiresAtUtc 使用 C# "O" 往返格式：7 位小数 + +00:00
	assert.Regexp(t, `"expiresAtUtc":"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.0000000\+00:00"`, body)
}
