// Package auth：登录端点半边（AuthService.cs → login.go）。
// 16KB 上限、MaxDepth=3、重复/未知字段拒绝；错误形态 400 invalid_request 统一。
package auth

import (
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net"
	"net/http"
	"time"

	"github.com/long45343/TextCascade-Server/internal/clock"
	"github.com/long45343/TextCascade-Server/internal/config"
	"github.com/long45343/TextCascade-Server/internal/core"
	"github.com/long45343/TextCascade-Server/internal/logging"
	"github.com/long45343/TextCascade-Server/internal/protocol"
	"github.com/long45343/TextCascade-Server/internal/users"
)

// ErrLoginParse 是 LoginParseException 的哨兵 error。
var ErrLoginParse = errors.New("login parse error")

const maxLoginBodyBytes = 16384

// LoginDeps 收敛登录处理所需的协作者（由 hosting 从 sync.Server 装配），
// 避免 auth → sync 的包环。
type LoginDeps struct {
	Limiter   *core.Limiter
	Lookup    func() map[string]users.UserRecord
	Clock     clock.Clock
	Hasher    PasswordHasher
	Params    Params
	DummyHash string
	Logger    *slog.Logger
}

type loginRequest struct {
	Username string
	Password string
}

// HandleLogin 对应 C# HandleLoginAsync，全部内联逻辑 1:1。
func HandleLogin(w http.ResponseWriter, r *http.Request, cfg *config.RuntimeConfig, deps *LoginDeps) {
	ip := remoteIP(r)
	now := deps.Clock.Now()

	request, err := parseLoginRequest(w, r)
	if err != nil {
		writeError(w, http.StatusBadRequest, "invalid_request", err.Error())
		return
	}

	if !deps.Limiter.TryConsumeLoginLimit(ip, request.Username, now, cfg) {
		logging.SecurityEvent(deps.Logger, "login",
			logging.Field{Key: "username", Value: request.Username},
			logging.Field{Key: "ip", Value: ip},
			logging.Field{Key: "success", Value: false},
			logging.Field{Key: "reason", Value: "rate_limited"})
		writeError(w, http.StatusTooManyRequests, "rate_limited", "Too many login attempts.")
		return
	}

	userLookup := deps.Lookup()
	user, found := userLookup[request.Username]
	passwordHash := deps.DummyHash
	if found {
		passwordHash = user.PasswordHash
	}
	passwordOK := deps.Hasher.Verify(request.Password, passwordHash)
	if !found || !passwordOK || user.Disabled {
		logging.SecurityEvent(deps.Logger, "login",
			logging.Field{Key: "username", Value: request.Username},
			logging.Field{Key: "ip", Value: ip},
			logging.Field{Key: "success", Value: false},
			logging.Field{Key: "reason", Value: "invalid_credentials"})
		writeError(w, http.StatusUnauthorized, "invalid_credentials", "Invalid username or password.")
		return
	}

	deps.Limiter.ResetUserWindow(request.Username)
	logging.SecurityEvent(deps.Logger, "login",
		logging.Field{Key: "username", Value: request.Username},
		logging.Field{Key: "ip", Value: ip},
		logging.Field{Key: "success", Value: true})

	// Spec §4.1：参数漂移时输出结构化 rehash warning，不重写 users.json。
	needsRehash := false
	if deps.Hasher.NeedsRehash(user.PasswordHash, deps.Params) {
		needsRehash = true
		deps.Logger.Warn(fmt.Sprintf("Argon2 password hash needs rehash for user %s; users.json was not rewritten.", user.Username))
	}

	tokenService, err := NewTokenService(cfg.TokenSecret)
	if err != nil {
		writeError(w, http.StatusInternalServerError, "invalid_request", err.Error())
		return
	}
	token := tokenService.Create(user, now, time.Duration(cfg.Auth.TokenTtlDays)*24*time.Hour)
	bytes := protocol.MarshalLoginResponse(token.CompactToken, token.Payload.ExpiresAtUnix, cfg, needsRehash)
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write(bytes)
}

// parseLoginRequest 对应 C# ParseLoginRequest。
// 16KB 上限用 http.MaxBytesReader（覆盖 chunked，等价 IHttpMaxRequestBodySizeFeature）。
func parseLoginRequest(w http.ResponseWriter, r *http.Request) (loginRequest, error) {
	if r.ContentLength > maxLoginBodyBytes {
		return loginRequest{}, errors.New("Request body too large.")
	}

	body := http.MaxBytesReader(w, r.Body, maxLoginBodyBytes)
	raw, err := io.ReadAll(body)
	if err != nil {
		return loginRequest{}, errors.New("Request body too large.")
	}

	root, err := protocol.Decode(raw, 3)
	if err != nil {
		return loginRequest{}, errors.New("Invalid JSON.")
	}

	// 登录契约（spec §4.1）：未知字段、重复字段、depth>3 拒绝；键名精确小写
	//（大小写变体一律按未知字段处理）。
	if !root.IsObject() || !hasExactlyLoginFields(root) {
		return loginRequest{}, errors.New("Invalid JSON.")
	}

	usernameValue := root.Get("username")
	passwordValue := root.Get("password")
	if usernameValue == nil || !usernameValue.IsString() || usernameValue.Str() == "" ||
		passwordValue == nil || !passwordValue.IsString() || passwordValue.Str() == "" {
		return loginRequest{}, errors.New("Missing username or password.")
	}

	return loginRequest{Username: usernameValue.Str(), Password: passwordValue.Str()}, nil
}

func hasExactlyLoginFields(root *protocol.Node) bool {
	seen := make(map[string]struct{}, 2)
	for _, member := range root.Members() {
		if member.Key != "username" && member.Key != "password" {
			return false
		}
		if _, dup := seen[member.Key]; dup {
			return false
		}
		seen[member.Key] = struct{}{}
	}
	return len(seen) == 2
}

func remoteIP(r *http.Request) string {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return "unknown"
	}
	return host
}

// writeError 对应 C# WriteError：400 invalid_request 统一形态。
func writeError(w http.ResponseWriter, status int, code, message string) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	payload := fmt.Sprintf(`{"error":%q,"message":%q}`, code, message)
	_, _ = w.Write([]byte(payload))
}
