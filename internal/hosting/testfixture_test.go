package hosting_test

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/json"
	"fmt"
	"log/slog"
	"math/big"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/gorilla/websocket"
	"github.com/stretchr/testify/require"
	"software.sslmate.com/src/go-pkcs12"

	"github.com/long45343/TextCascade-Server/internal/auth"
	"github.com/long45343/TextCascade-Server/internal/clock"
	"github.com/long45343/TextCascade-Server/internal/config"
	"github.com/long45343/TextCascade-Server/internal/hosting"
	"github.com/long45343/TextCascade-Server/internal/state"
	syncserver "github.com/long45343/TextCascade-Server/internal/sync"
	"github.com/long45343/TextCascade-Server/internal/users"
)

const fixtureValidHash = "$argon2id$v=19$m=19456,t=2,p=1$c2FsdA$aG9zdA"

// fastVerifyHasher 对应 C# FastPasswordHasher：Verify 仅接受固定凭据。
type fastVerifyHasher struct{}

func (fastVerifyHasher) Hash(string, auth.Params) string { return fixtureValidHash }

func (fastVerifyHasher) Verify(password, encodedHash string) bool {
	return password == "password123" && encodedHash == fixtureValidHash
}

func (fastVerifyHasher) NeedsRehash(string, auth.Params) bool { return false }

var _ auth.PasswordHasher = fastVerifyHasher{}

// logCapture 收集全部日志消息（对应 C# TestLogCollector）。
type logCapture struct {
	mu      sync.Mutex
	entries []string
}

func (c *logCapture) logger() *slog.Logger {
	return slog.New(&captureAllHandler{c: c})
}

func (c *logCapture) snapshot() []string {
	c.mu.Lock()
	defer c.mu.Unlock()
	return append([]string(nil), c.entries...)
}

type captureAllHandler struct{ c *logCapture }

func (h *captureAllHandler) Enabled(_ context.Context, level slog.Level) bool {
	return level >= slog.LevelInfo
}

func (h *captureAllHandler) Handle(_ context.Context, r slog.Record) error {
	h.c.mu.Lock()
	defer h.c.mu.Unlock()
	h.c.entries = append(h.c.entries, r.Message)
	return nil
}

func (h *captureAllHandler) WithAttrs(attrs []slog.Attr) slog.Handler { return h }
func (h *captureAllHandler) WithGroup(name string) slog.Handler       { return h }

// runningServer 对应 C# NetworkTestFixture.RunningServer。
type runningServer struct {
	Port        int
	Authority   string
	srv         *syncserver.Server
	wssURL      string
	wsURL       string
	httpSrv     *http.Server
	ln          net.Listener
	scannerStop func()
	scannerDone chan struct{}
	logs        *logCapture
}

// stop 执行与生产 Run 一致的优雅停机：scanner 停止 → Shutdown(2s) → 关闭监听。
func (r *runningServer) stop() {
	r.scannerStop()
	<-r.scannerDone
	r.srv.Shutdown(2*time.Second, clock.System.Now())
	_ = r.httpSrv.Close()
}

// serverAssembly 是 hosting.Run 的可测装配等价物（CreateApp + StartAsync）。
func startServerAssembly(t *testing.T, cfg *config.RuntimeConfig, f *users.UsersFile, store *state.Store, certificate *tls.Certificate) *runningServer {
	t.Helper()
	logs := &logCapture{}
	srv := syncserver.New(cfg, f, store, fastVerifyHasher{}, clock.System, logs.logger())

	mux := http.NewServeMux()
	mux.HandleFunc("GET /health", func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(map[string]string{"status": "ok"})
	})
	mux.HandleFunc("POST /api/v1/login", func(w http.ResponseWriter, r *http.Request) {
		auth.HandleLogin(w, r, cfg, srv.LoginDeps())
	})
	mux.HandleFunc("GET /api/v1/sync", func(w http.ResponseWriter, r *http.Request) {
		hosting.HandleSync(w, r, cfg, srv)
	})

	httpSrv := &http.Server{Handler: mux}
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	require.NoError(t, err)

	scannerCtx, scannerStop := context.WithCancel(context.Background())
	scannerDone := make(chan struct{})
	go func() {
		defer close(scannerDone)
		hosting.RunScanner(scannerCtx, srv, clock.System, logs.logger())
	}()

	if certificate != nil {
		httpSrv.TLSConfig = &tls.Config{
			Certificates: []tls.Certificate{*certificate},
			NextProtos:   []string{"http/1.1"},
		}
		go func() { _ = httpSrv.ServeTLS(ln, "", "") }()
	} else {
		go func() { _ = httpSrv.Serve(ln) }()
	}

	port := ln.Addr().(*net.TCPAddr).Port
	authority := fmt.Sprintf("127.0.0.1:%d", port)
	return &runningServer{
		Port:        port,
		Authority:   authority,
		srv:         srv,
		wsURL:       "ws://" + authority + "/api/v1/sync",
		wssURL:      "wss://" + authority + "/api/v1/sync",
		httpSrv:     httpSrv,
		ln:          ln,
		scannerStop: scannerStop,
		scannerDone: scannerDone,
		logs:        logs,
	}
}

// integrationFixture 对应 C# WebSocketIntegrationTests.IntegrationTestFixture（明文 HTTP）。
type integrationFixture struct {
	cfg   *config.RuntimeConfig
	srv   *syncserver.Server
	store *state.Store
	f     *users.UsersFile
	s     *runningServer
	logs  *logCapture
}

func newIntegrationFixture(t *testing.T, modify func(*config.RuntimeConfig)) *integrationFixture {
	t.Helper()
	dir := t.TempDir()
	usersPath := filepath.Join(dir, "users.json")
	statePath := filepath.Join(dir, "state.json")

	f := &users.UsersFile{
		NextTokenVersion: 2,
		Users: []users.UserRecord{
			{Username: "alice", PasswordHash: fixtureValidHash, TokenVersion: 1},
			{Username: "bob", PasswordHash: fixtureValidHash, TokenVersion: 1},
		},
	}
	require.NoError(t, users.Save(usersPath, f))

	store, err := state.NewStore(statePath, 0, nil)
	require.NoError(t, err)
	t.Cleanup(store.Stop)

	cfg := config.Defaults()
	cfg.TokenSecret = []byte("12345678901234567890123456789012")
	cfg.Files = config.FilesConfig{UsersFile: usersPath, StateFile: statePath}
	cfg.Server = config.ServerConfig{Bind: "127.0.0.1", Port: 0, CertificatePath: "dummy.pem"}
	cfg.Limits.SnapshotWindowSeconds = 0
	if modify != nil {
		modify(&cfg)
	}

	s := startServerAssembly(t, &cfg, f, store, nil)
	t.Cleanup(s.stop)
	return &integrationFixture{cfg: &cfg, srv: s.srv, store: store, f: f, s: s, logs: s.logs}
}

func loginHTTP(t *testing.T, client *http.Client, base, username, password string) string {
	t.Helper()
	body := fmt.Sprintf(`{"username":%q,"password":%q}`, username, password)
	resp, err := client.Post(base+"/api/v1/login", "application/json", strings.NewReader(body))
	require.NoError(t, err)
	defer resp.Body.Close()
	require.True(t, resp.StatusCode >= 200 && resp.StatusCode < 300, "Login failed: %s", resp.Status)
	var payload struct {
		Token           string `json:"token"`
		ProtocolVersion int    `json:"protocolVersion"`
	}
	require.NoError(t, json.NewDecoder(resp.Body).Decode(&payload))
	require.Equal(t, 1, payload.ProtocolVersion)
	require.NotEmpty(t, payload.Token)
	return payload.Token
}

// wsClient 是带子协议与 Bearer 的 gorilla 客户端。
func dialWS(t *testing.T, rawURL, token string) *websocket.Conn {
	t.Helper()
	dialer := websocket.Dialer{HandshakeTimeout: 5 * time.Second, Subprotocols: []string{"textcascade.v1"}}
	if strings.HasPrefix(rawURL, "wss:") {
		dialer.TLSClientConfig = &tls.Config{InsecureSkipVerify: true}
	}
	header := http.Header{}
	header.Set("Authorization", "Bearer "+token)
	conn, resp, err := dialer.Dial(rawURL, header)
	if resp != nil && resp.Body != nil {
		defer resp.Body.Close()
	}
	require.NoError(t, err)
	return conn
}

func sendJSONWS(t *testing.T, conn *websocket.Conn, payload string) {
	t.Helper()
	require.NoError(t, conn.SetWriteDeadline(time.Now().Add(5*time.Second)))
	require.NoError(t, conn.WriteMessage(websocket.TextMessage, []byte(payload)))
}

func receiveJSONWS(t *testing.T, conn *websocket.Conn) map[string]any {
	t.Helper()
	require.NoError(t, conn.SetReadDeadline(time.Now().Add(5*time.Second)))
	_, data, err := conn.ReadMessage()
	require.NoError(t, err)
	var payload map[string]any
	require.NoError(t, json.Unmarshal(data, &payload))
	return payload
}

func closeWS(t *testing.T, conn *websocket.Conn) {
	t.Helper()
	if conn != nil {
		_ = conn.SetWriteDeadline(time.Now().Add(500 * time.Millisecond))
		_ = conn.WriteControl(websocket.CloseMessage,
			websocket.FormatCloseMessage(websocket.CloseNormalClosure, "done"), time.Now().Add(500*time.Millisecond))
		_ = conn.Close()
	}
}

func insecureHTTPClient() *http.Client {
	return &http.Client{Transport: &http.Transport{
		TLSClientConfig: &tls.Config{InsecureSkipVerify: true},
	}}
}

// generateSelfSignedPFX 生成自签证书 PFX（等价 C# SelfSignedCertificate.Create + Export Pfx）。
func generateSelfSignedPFX(t *testing.T, path string) {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	require.NoError(t, err)
	template := x509.Certificate{
		Subject:               pkix.Name{CommonName: "localhost"},
		NotBefore:             time.Now().Add(-24 * time.Hour),
		NotAfter:              time.Now().Add(5 * 365 * 24 * time.Hour),
		SerialNumber:          big.NewInt(1),
		KeyUsage:              x509.KeyUsageDigitalSignature | x509.KeyUsageKeyEncipherment | x509.KeyUsageCertSign,
		ExtKeyUsage:           []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		DNSNames:              []string{"localhost"},
		IPAddresses:           []net.IP{net.ParseIP("127.0.0.1")},
		BasicConstraintsValid: true,
		IsCA:                  true,
	}
	der, err := x509.CreateCertificate(rand.Reader, &template, &template, &key.PublicKey, key)
	require.NoError(t, err)
	cert, err := x509.ParseCertificate(der)
	require.NoError(t, err)
	pfxBytes, err := pkcs12.Encode(rand.Reader, key, cert, nil, "")
	require.NoError(t, err)
	require.NoError(t, os.WriteFile(path, pfxBytes, 0o644))
}
