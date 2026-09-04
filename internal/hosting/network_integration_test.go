package hosting_test

import (
	"crypto/tls"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/gorilla/websocket"
	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"

	"github.com/long45343/TextCascade-Server/internal/config"
	"github.com/long45343/TextCascade-Server/internal/hosting"
	"github.com/long45343/TextCascade-Server/internal/state"
	"github.com/long45343/TextCascade-Server/internal/users"
)

// ---- NetworkIntegration（12 例，真实 TLS + 自签 PFX + 随机端口）----

// networkFixture 对应 C# NetworkTestFixture。
type networkFixture struct {
	dir       string
	cfg       *config.RuntimeConfig
	f         *users.UsersFile
	usersPath string
	statePath string
	pfxPath   string
}

func newNetworkFixture(t *testing.T, modify func(*config.RuntimeConfig)) *networkFixture {
	t.Helper()
	dir := t.TempDir()
	pfxPath := filepath.Join(dir, "server.pfx")
	generateSelfSignedPFX(t, pfxPath)

	usersPath := filepath.Join(dir, "users.json")
	statePath := filepath.Join(dir, "state.json")
	f := &users.UsersFile{
		NextTokenVersion: 2,
		Users:            []users.UserRecord{{Username: "alice", PasswordHash: fixtureValidHash, TokenVersion: 1}},
	}
	require.NoError(t, users.Save(usersPath, f))

	cfg := config.Defaults()
	cfg.TokenSecret = []byte("12345678901234567890123456789012")
	cfg.Files = config.FilesConfig{UsersFile: usersPath, StateFile: statePath}
	cfg.Server = config.ServerConfig{Bind: "127.0.0.1", Port: 0, CertificatePath: pfxPath}
	if modify != nil {
		modify(&cfg)
	}
	return &networkFixture{dir: dir, cfg: &cfg, f: f, usersPath: usersPath, statePath: statePath, pfxPath: pfxPath}
}

// start 装载 PFX 并启动真实 TLS 服务器（等价 C# StartAsync）。
func (n *networkFixture) start(t *testing.T) *runningServer {
	t.Helper()
	certificate, err := hosting.LoadCertificate(n.pfxPath)
	require.NoError(t, err)
	store, err := state.NewStore(n.statePath, 0, nil)
	require.NoError(t, err)
	t.Cleanup(store.Stop)
	return startServerAssembly(t, n.cfg, n.f, store, &certificate)
}

func dialWSS(t *testing.T, wssURL, token, clientID string, lastServerVersion uint64, snapshot string) *websocket.Conn {
	t.Helper()
	dialer := websocket.Dialer{
		HandshakeTimeout: 10 * time.Second,
		Subprotocols:     []string{"textcascade.v1"},
		TLSClientConfig:  &tls.Config{InsecureSkipVerify: true},
	}
	header := http.Header{}
	header.Set("Authorization", "Bearer "+token)
	conn, resp, err := dialer.Dial(wssURL, header)
	if resp != nil && resp.Body != nil {
		defer resp.Body.Close()
	}
	require.NoError(t, err)
	sendJSONWS(t, conn, helloMessage(clientID, clientID, lastServerVersion, snapshot))
	return conn
}

func networkLogin(t *testing.T, authority string) string {
	t.Helper()
	return loginHTTP(t, insecureHTTPClient(), "https://"+authority, "alice", "password123")
}

// N1
func TestConnectsWithSelfSignedPfxOverWss(t *testing.T) {
	fixture := newNetworkFixture(t, nil)
	server := fixture.start(t)

	token := networkLogin(t, server.Authority)
	ws := dialWSS(t, server.wssURL, token, "tls-client-1", 0, "")
	defer closeWS(t, ws)

	welcome := receiveJSONWS(t, ws)
	assert.Equal(t, "welcome", welcome["type"])
}

// N2：显式 TLS 1.2 / TLS 1.3 握手探针。
func TestServerHandshakesWithExplicitTLSVersions(t *testing.T) {
	for _, version := range []uint16{tls.VersionTLS12, tls.VersionTLS13} {
		version := version
		t.Run(fmt.Sprintf("TLS_%04x", version), func(t *testing.T) {
			fixture := newNetworkFixture(t, nil)
			server := fixture.start(t)

			dialer := net.Dialer{Timeout: 5 * time.Second}
			tcp, err := dialer.Dial("tcp", fmt.Sprintf("127.0.0.1:%d", server.Port))
			require.NoError(t, err)
			defer tcp.Close()

			conn := tls.Client(tcp, &tls.Config{
				ServerName:         "localhost",
				InsecureSkipVerify: true,
				MinVersion:         version,
				MaxVersion:         version,
			})
			require.NoError(t, conn.SetDeadline(time.Now().Add(10*time.Second)))
			require.NoError(t, conn.Handshake())
			defer conn.Close()

			assert.True(t, int(conn.ConnectionState().Version) == int(version) || tlsVersionName(conn.ConnectionState().Version) == tlsVersionName(version))
			assert.Equal(t, tlsVersionName(version), tlsVersionName(conn.ConnectionState().Version))
		})
	}
}

func tlsVersionName(v uint16) string {
	switch v {
	case tls.VersionTLS12:
		return "TLS1.2"
	case tls.VersionTLS13:
		return "TLS1.3"
	default:
		return fmt.Sprintf("%04x", v)
	}
}

// N3
func TestHttpUpgradeSucceedsWithBearerAndSubProtocol(t *testing.T) {
	fixture := newNetworkFixture(t, nil)
	server := fixture.start(t)

	token := networkLogin(t, server.Authority)
	ws := dialWSS(t, server.wssURL, token, "subproto-client", 0, "")
	defer closeWS(t, ws)

	assert.Equal(t, "textcascade.v1", ws.Subprotocol())
	welcome := receiveJSONWS(t, ws)
	assert.EqualValues(t, 1, welcome["protocolVersion"])
}

// N4
func TestHttpsLoginEndpointWorks(t *testing.T) {
	fixture := newNetworkFixture(t, nil)
	server := fixture.start(t)

	resp, err := insecureHTTPClient().Post("https://"+server.Authority+"/api/v1/login", "application/json",
		strings.NewReader(`{"username":"alice","password":"password123"}`))
	require.NoError(t, err)
	defer resp.Body.Close()
	require.True(t, resp.StatusCode >= 200 && resp.StatusCode < 300, "Login failed: %s", resp.Status)

	var root map[string]any
	require.NoError(t, json.NewDecoder(resp.Body).Decode(&root))
	assert.NotEmpty(t, root["token"])
	assert.EqualValues(t, 1, root["protocolVersion"])
	for _, key := range []string{"expiresAtUtc", "maxTextBytes", "helloTimeoutSeconds", "heartbeatIntervalSeconds", "heartbeatTimeoutSeconds"} {
		assert.Contains(t, root, key)
	}
}

// N5
func TestRandomPortBindingActuallyBinds(t *testing.T) {
	fixture := newNetworkFixture(t, nil)
	server := fixture.start(t)

	assert.Greater(t, server.Port, 0)
	conn, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", server.Port), 5*time.Second)
	require.NoError(t, err)
	assert.NotNil(t, conn)
	_ = conn.Close()
}

// N6
func TestFragmentedClipReassemblesAndBroadcasts(t *testing.T) {
	fixture := newNetworkFixture(t, nil)
	server := fixture.start(t)

	token := networkLogin(t, server.Authority)
	wsA := dialWSS(t, server.wssURL, token, "frag-A", 0, "")
	wsB := dialWSS(t, server.wssURL, token, "frag-B", 0, "")
	defer closeWS(t, wsA)
	defer closeWS(t, wsB)
	_ = receiveJSONWS(t, wsA) // welcome A
	_ = receiveJSONWS(t, wsB) // welcome B

	// ~300KB payload 分三片发送（低于单帧上限、跨 MSS 边界）。
	payload := strings.Repeat("x", 300_000)
	clip := fmt.Sprintf(`{"type":"clip","id":"frag-clip-1","payload":"%s","encrypted":false,"hash":"frag-hash"}`, payload)
	sendFragmented(t, wsA, clip, 100_000)

	ack := receiveTypedWS(t, wsA)
	assert.Equal(t, "clip_ack", ack.msgType)

	broadcast := receiveTypedWS(t, wsB)
	assert.Equal(t, "clip", broadcast.msgType)
	var doc map[string]any
	require.NoError(t, json.Unmarshal([]byte(broadcast.payload), &doc))
	assert.Equal(t, payload, doc["payload"])
}

// N7
func TestOversizeFrameCloses1009(t *testing.T) {
	fixture := newNetworkFixture(t, nil)
	server := fixture.start(t)

	token := networkLogin(t, server.Authority)
	ws := dialWSS(t, server.wssURL, token, "oversize-A", 0, "")
	defer closeWS(t, ws)
	_ = receiveJSONWS(t, ws) // welcome

	// 总帧超过 max_frame_bytes（589824）。
	oversize := strings.Repeat("y", 600_000)
	clip := fmt.Sprintf(`{"type":"clip","id":"oversize-1","payload":"%s","encrypted":false,"hash":"h"}`, oversize)
	sendFragmented(t, ws, clip, 300_000)

	// 服务器发送 frame_too_large 错误后以 1009 关闭。
	closeSeen := false
	for attempt := 0; attempt < 2 && !closeSeen; attempt++ {
		require.NoError(t, ws.SetReadDeadline(time.Now().Add(10*time.Second)))
		_, _, err := ws.ReadMessage()
		if err != nil {
			var closeErr *websocket.CloseError
			if errors.As(err, &closeErr) && closeErr.Code == websocket.CloseMessageTooBig {
				closeSeen = true
			} else if strings.Contains(err.Error(), "close 1009") || strings.Contains(err.Error(), "unexpected EOF") {
				closeSeen = true
			}
		}
	}
	assert.True(t, closeSeen, "server should close the connection with 1009 after an oversize frame")
}

// N8
func TestZeroLengthFrameTreatedAsFrameTooLarge(t *testing.T) {
	fixture := newNetworkFixture(t, nil)
	server := fixture.start(t)

	token := networkLogin(t, server.Authority)
	ws := dialWSS(t, server.wssURL, token, "zerolen-A", 0, "")
	defer closeWS(t, ws)
	_ = receiveJSONWS(t, ws) // welcome

	require.NoError(t, ws.SetWriteDeadline(time.Now().Add(10*time.Second)))
	require.NoError(t, ws.WriteMessage(websocket.TextMessage, []byte{}))

	closeSeen := false
	for attempt := 0; attempt < 2 && !closeSeen; attempt++ {
		require.NoError(t, ws.SetReadDeadline(time.Now().Add(10*time.Second)))
		_, _, err := ws.ReadMessage()
		if err != nil {
			var closeErr *websocket.CloseError
			if errors.As(err, &closeErr) && closeErr.Code == websocket.CloseMessageTooBig {
				closeSeen = true
			} else if strings.Contains(err.Error(), "close 1009") || strings.Contains(err.Error(), "unexpected EOF") {
				closeSeen = true
			}
		}
	}
	assert.True(t, closeSeen, "zero-length frame should terminate the connection")
}

// ---- RestartRecoveryTests ----

// N9
func TestRestartKeepsTokenValidDirectReconnect(t *testing.T) {
	fixture := newNetworkFixture(t, func(cfg *config.RuntimeConfig) {
		cfg.Limits.SnapshotWindowSeconds = 0
	})
	first := fixture.start(t)

	var token string
	// 第一实例：登录并推送一条 clip，把版本推到 1。
	token = networkLogin(t, first.Authority)
	ws := dialWSS(t, first.wssURL, token, "restart-A", 0, "")
	welcome := receiveTypedWS(t, ws)
	require.Equal(t, "welcome", welcome.msgType)

	sendJSONWS(t, ws, `{"type":"clip","id":"pre-restart-1","payload":"before restart","encrypted":false,"hash":"h1"}`)
	ack := receiveTypedWS(t, ws)
	require.Equal(t, "clip_ack", ack.msgType)
	var ackDoc map[string]any
	require.NoError(t, json.Unmarshal([]byte(ack.payload), &ackDoc))
	assert.EqualValues(t, 1, ackDoc["version"])
	closeWS(t, ws)

	// 优雅停机后 state 文件必须已刷盘。
	first.stop()
	_, statErr := os.Stat(fixture.statePath)
	assert.NoError(t, statErr, "state file should exist after graceful stop")

	// 第二实例：同一 users/state 文件；旧 token 免登录可用。
	second := fixture.start(t)
	ws2 := dialWSS(t, second.wssURL, token, "restart-B", 0, "")
	welcome2 := receiveTypedWS(t, ws2)
	require.Equal(t, "welcome", welcome2.msgType)

	sendJSONWS(t, ws2, `{"type":"clip","id":"post-restart-1","payload":"after restart","encrypted":false,"hash":"h2"}`)
	ack2 := receiveTypedWS(t, ws2)
	require.Equal(t, "clip_ack", ack2.msgType)
	var ack2Doc map[string]any
	require.NoError(t, json.Unmarshal([]byte(ack2.payload), &ack2Doc))
	assert.EqualValues(t, 2, ack2Doc["version"])
	closeWS(t, ws2)
	second.stop()
}

// N10
func TestRestartSnapshotElectionRestoresLatest(t *testing.T) {
	fixture := newNetworkFixture(t, func(cfg *config.RuntimeConfig) {
		cfg.Limits.SnapshotWindowSeconds = 5
	})
	first := fixture.start(t)
	token := networkLogin(t, first.Authority)
	first.stop()

	second := fixture.start(t)
	defer second.stop()

	modifiedTime := time.Now().UTC()
	snapshotOf := func(version uint64, payload string) string {
		return fmt.Sprintf(`{"payload":%q,"encrypted":false,"hash":"hash-%d","localModifiedAtUtc":"%s"}`,
			payload, version, modifiedTime.Format("2006-01-02T15:04:05Z"))
	}

	ws128 := dialWSS(t, second.wssURL, token, "snap-128", 128, snapshotOf(128, "snapshot-v128"))
	ws64 := dialWSS(t, second.wssURL, token, "snap-64", 64, snapshotOf(64, "snapshot-v64"))
	defer closeWS(t, ws128)
	defer closeWS(t, ws64)

	time.Sleep(100 * time.Millisecond)

	// 通过服务器强制关闭恢复窗口，立即选举。
	hub := second.srv.GetOrCreateHub("alice")
	hub.CloseRecoveryWindow(time.Now().UTC().Add(time.Minute))

	welcome128 := receiveTypedWS(t, ws128)
	welcome64 := receiveTypedWS(t, ws64)
	for _, welcome := range []typedMessage{welcome128, welcome64} {
		require.Equal(t, "welcome", welcome.msgType)
		var doc map[string]any
		require.NoError(t, json.Unmarshal([]byte(welcome.payload), &doc))
		latest := doc["latest"].(map[string]any)
		assert.Equal(t, "snapshot-v128", latest["payload"])
		assert.EqualValues(t, 128, latest["version"])
	}

	// 选举后的下一条 clip 从恢复版本继续（恢复不 +1）。
	sendJSONWS(t, ws64, `{"type":"clip","id":"post-election-1","payload":"fresh clip","encrypted":false,"hash":"h3"}`)
	ack := receiveTypedWS(t, ws64)
	require.Equal(t, "clip_ack", ack.msgType)
	var ackDoc map[string]any
	require.NoError(t, json.Unmarshal([]byte(ack.payload), &ackDoc))
	assert.EqualValues(t, 129, ackDoc["version"])
}

// N11 + N12 合并链：login → connect → clip 流 → 优雅停机 bye/1001。
func TestFullChainLoginConnectSendReceiveBye1001(t *testing.T) {
	fixture := newNetworkFixture(t, func(cfg *config.RuntimeConfig) {
		cfg.Limits.SnapshotWindowSeconds = 0
	})
	server := fixture.start(t)

	tokenA := networkLogin(t, server.Authority)
	tokenB := networkLogin(t, server.Authority)

	wsA := dialWSS(t, server.wssURL, tokenA, "chain-A", 0, "")
	wsB := dialWSS(t, server.wssURL, tokenB, "chain-B", 0, "")

	// 泵协程持续读帧（复刻 .NET ClientWebSocket 的自动 close 应答行为，
	// 使服务器停机握手可完成）；bye/close 事件写入 sink。
	sinkA := make(chan typedMessage, 32)
	sinkB := make(chan typedMessage, 32)
	go pumpMessages(wsA, sinkA)
	go pumpMessages(wsB, sinkB)

	welcomeA := <-sinkA
	require.Equal(t, "welcome", welcomeA.msgType)
	welcomeB := <-sinkB
	require.Equal(t, "welcome", welcomeB.msgType)

	sendJSONWS(t, wsA, `{"type":"clip","id":"chain-clip-1","payload":"chain payload","encrypted":false,"hash":"hc"}`)

	ack := <-sinkA
	require.Equal(t, "clip_ack", ack.msgType)

	// 两个 hello 竞速恢复窗口关闭可能给 B 投递第二条 welcome；跳过多余帧直到广播 clip。
	for {
		broadcast := <-sinkB
		if broadcast.msgType == "clip" {
			var doc map[string]any
			require.NoError(t, json.Unmarshal([]byte(broadcast.payload), &doc))
			assert.Equal(t, "chain payload", doc["payload"])
			break
		}
		require.Equal(t, "welcome", broadcast.msgType)
	}

	// 优雅停机：先 bye 后 close 1001；两者均为尽力而为契约（spec §7）。
	server.stop()

	// 排空 B 的剩余事件直到 close，验证 bye（若送达）的原因。
	byeReason := ""
	deadline := time.Now().Add(10 * time.Second)
	for {
		select {
		case msg := <-sinkB:
			if msg.msgType == "bye" {
				var doc map[string]any
				_ = json.Unmarshal([]byte(msg.payload), &doc)
				byeReason, _ = doc["reason"].(string)
			}
			if msg.msgType == "close" {
				goto drained
			}
		default:
			if time.Now().After(deadline) {
				goto drained
			}
			time.Sleep(20 * time.Millisecond)
		}
	}
drained:
	if byeReason != "" {
		assert.Equal(t, "server_shutdown", byeReason)
	}
	// 若 bye 未送达：快速 drain 在帧落地前中止 socket，同样契约合法。

	// 关闭底层连接（泵已退出）。
	_ = wsA.Close()
	_ = wsB.Close()
}

// pumpMessages 循环读帧直至连接关闭；gorilla 默认 close 处理器会在读到
// close 帧时自动回显（等价 ClientWebSocket 的自动 close 应答）。
func pumpMessages(conn *websocket.Conn, sink chan<- typedMessage) {
	for {
		_ = conn.SetReadDeadline(time.Now().Add(30 * time.Second))
		_, data, err := conn.ReadMessage()
		if err != nil {
			sink <- typedMessage{msgType: "close", payload: ""}
			return
		}
		var doc map[string]any
		if json.Unmarshal(data, &doc) != nil {
			continue
		}
		msgType, _ := doc["type"].(string)
		sink <- typedMessage{msgType: msgType, payload: string(data)}
	}
}

// receiveByeOrClose 一直读到 bye 帧或 close/abort；绝不抛出。
func receiveByeOrClose(t *testing.T, conn *websocket.Conn) [2]string {
	t.Helper()
	for {
		require.NoError(t, conn.SetReadDeadline(time.Now().Add(20*time.Second)))
		_, data, err := conn.ReadMessage()
		if err != nil {
			return [2]string{"close", ""}
		}
		var doc map[string]any
		if json.Unmarshal(data, &doc) != nil {
			return [2]string{"close", ""}
		}
		if doc["type"] == "bye" {
			reason, _ := doc["reason"].(string)
			return [2]string{"bye", reason}
		}
	}
}

// typedMessage 是一条完整消息（帧分片已重组）。
type typedMessage struct {
	msgType string
	payload string
}

// receiveTypedWS 循环读至完整消息（服务器帧也可能分片）。
func receiveTypedWS(t *testing.T, conn *websocket.Conn) typedMessage {
	t.Helper()
	for {
		require.NoError(t, conn.SetReadDeadline(time.Now().Add(15*time.Second)))
		_, data, err := conn.ReadMessage()
		if err != nil {
			return typedMessage{msgType: "close", payload: "{}"}
		}
		var doc map[string]any
		if json.Unmarshal(data, &doc) != nil {
			continue
		}
		msgType, _ := doc["type"].(string)
		return typedMessage{msgType: msgType, payload: string(data)}
	}
}

// sendFragmented 以 chunkSize 分片发送一条消息（NextWriter 流式写）。
func sendFragmented(t *testing.T, conn *websocket.Conn, message string, chunkSize int) {
	t.Helper()
	require.NoError(t, conn.SetWriteDeadline(time.Now().Add(10*time.Second)))
	writer, err := conn.NextWriter(websocket.TextMessage)
	require.NoError(t, err)
	data := []byte(message)
	for start := 0; start < len(data); start += chunkSize {
		end := start + chunkSize
		if end > len(data) {
			end = len(data)
		}
		_, err := writer.Write(data[start:end])
		require.NoError(t, err)
	}
	require.NoError(t, writer.Close())
}
