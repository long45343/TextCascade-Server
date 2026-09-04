// Package hosting：SyncEndpoint.cs → endpoint.go。
// 升级前 Bearer 验证（401 不升级）；仅接受 textcascade.v1（Ordinal 精确）否则 400；
// 非 WS 400。CheckOrigin 恒允许以对齐 C# AcceptWebSocketAsync 无 origin 校验的语义。
package hosting

import (
	"crypto/rand"
	"encoding/hex"
	"net/http"
	"strings"
	"time"

	"github.com/gorilla/websocket"

	"github.com/long45343/TextCascade-Server/internal/auth"
	"github.com/long45343/TextCascade-Server/internal/config"
	"github.com/long45343/TextCascade-Server/internal/models"
	"github.com/long45343/TextCascade-Server/internal/sync"
)

const subProtocolName = "textcascade.v1"

// HandleSync 对应 C# SyncEndpoint.HandleAsync。
func HandleSync(w http.ResponseWriter, r *http.Request, cfg *config.RuntimeConfig, srv *sync.Server) {
	tokenHeader := r.Header.Get("Authorization")
	if !strings.HasPrefix(tokenHeader, "Bearer ") {
		w.WriteHeader(http.StatusUnauthorized)
		return
	}

	compactToken := tokenHeader[len("Bearer "):]
	now := time.Now().UTC()
	tokenService, err := auth.NewTokenService(cfg.TokenSecret)
	if err != nil {
		w.WriteHeader(http.StatusUnauthorized)
		return
	}
	payload, ok := tokenService.TryVerifyToken(compactToken, now, srv.UserLookup())
	if !ok {
		w.WriteHeader(http.StatusUnauthorized)
		return
	}

	if !websocket.IsWebSocketUpgrade(r) {
		w.WriteHeader(http.StatusBadRequest)
		return
	}

	if selectSubprotocol(r) == "" {
		w.WriteHeader(http.StatusBadRequest)
		return
	}

	upgrader := websocket.Upgrader{
		Subprotocols: []string{subProtocolName},
		CheckOrigin:  func(*http.Request) bool { return true },
	}
	conn, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		// gorilla 已写出错误响应。
		return
	}

	connectionID := newConnectionID()
	provisional := models.NewConnection(connectionID, payload.Subject, "pending", "pending", conn, cfg)
	conn.SetCloseHandler(func(int, string) error {
		provisional.State.SignalPeerGone()
		return nil
	})
	RunConnection(provisional, payload, cfg, srv)
}

// selectSubprotocol 对应 C# SelectSubProtocol：按请求序，第一个精确匹配
// textcascade.v1 的子协议。
func selectSubprotocol(r *http.Request) string {
	for _, protocol := range websocket.Subprotocols(r) {
		if protocol == subProtocolName {
			return protocol
		}
	}
	return ""
}

// newConnectionID 对应 Guid.NewGuid().ToString("N")：32 个小写十六进制字符。
func newConnectionID() string {
	buf := make([]byte, 16)
	if _, err := rand.Read(buf); err != nil {
		panic(err)
	}
	return hex.EncodeToString(buf)
}
