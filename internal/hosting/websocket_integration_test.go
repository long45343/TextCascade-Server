package hosting_test

import (
	"encoding/json"
	"strings"
	"testing"
	"time"

	"github.com/gorilla/websocket"
	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"

	"github.com/long45343/TextCascade-Server/internal/config"
)

// ---- WebSocketIntegrationTests（5 例，明文 HTTP 内嵌服务器）----

func helloMessage(clientID, clientName string, lastServerVersion uint64, snapshot string) string {
	if snapshot == "" {
		return `{"type":"hello","clientId":"` + clientID + `","clientName":"` + clientName + `","lastServerVersion":` + itoaU64(lastServerVersion) + `,"snapshot":null}`
	}
	return `{"type":"hello","clientId":"` + clientID + `","clientName":"` + clientName + `","lastServerVersion":` + itoaU64(lastServerVersion) + `,"snapshot":` + snapshot + `}`
}

func itoaU64(v uint64) string {
	return jsonNumber(v)
}

func jsonNumber(v uint64) string {
	b, _ := json.Marshal(v)
	return string(b)
}

func TestLoginAndWebSocketHandshakeRoundTrips(t *testing.T) {
	fixture := newIntegrationFixture(t, nil)

	token := loginHTTP(t, insecureHTTPClient(), "http://"+fixture.s.Authority, "alice", "password123")
	ws := dialWS(t, fixture.s.wsURL, token)
	defer closeWS(t, ws)

	assert.Equal(t, "textcascade.v1", ws.Subprotocol())

	sendJSONWS(t, ws, helloMessage("client-1", "Device 1", 0, ""))

	welcome := receiveJSONWS(t, ws)
	assert.Equal(t, "welcome", welcome["type"])
	assert.EqualValues(t, 1, welcome["protocolVersion"])
	_, hasLatest := welcome["latest"]
	assert.False(t, hasLatest)
}

func TestClipBroadcastsToSecondClient(t *testing.T) {
	fixture := newIntegrationFixture(t, nil)

	base := "http://" + fixture.s.Authority
	tokenA := loginHTTP(t, insecureHTTPClient(), base, "alice", "password123")
	tokenB := loginHTTP(t, insecureHTTPClient(), base, "alice", "password123")

	wsA := dialWS(t, fixture.s.wsURL, tokenA)
	wsB := dialWS(t, fixture.s.wsURL, tokenB)
	defer closeWS(t, wsA)
	defer closeWS(t, wsB)

	sendJSONWS(t, wsA, helloMessage("client-A", "Device A", 0, ""))
	_ = receiveJSONWS(t, wsA)

	sendJSONWS(t, wsB, helloMessage("client-B", "Device B", 0, ""))
	_ = receiveJSONWS(t, wsB)

	// A 发送 clip。
	sendJSONWS(t, wsA, `{"type":"clip","id":"clip-msg-1","payload":"Hello World Broadcast","encrypted":false,"hash":"h1"}`)

	// A 收到 clip_ack。
	ackA := receiveJSONWS(t, wsA)
	assert.Equal(t, "clip_ack", ackA["type"])
	assert.Equal(t, "clip-msg-1", ackA["id"])
	assert.EqualValues(t, 1, ackA["version"])

	// B 收到广播 clip。
	clipB := receiveJSONWS(t, wsB)
	assert.Equal(t, "clip", clipB["type"])
	assert.Equal(t, "clip-msg-1", clipB["id"])
	assert.Equal(t, "Hello World Broadcast", clipB["payload"])
	assert.EqualValues(t, 1, clipB["version"])

	// B 发送相同 clip（同 id 同 payload）。
	sendJSONWS(t, wsB, `{"type":"clip","id":"clip-msg-1","payload":"Hello World Broadcast","encrypted":false,"hash":"h1"}`)

	// B 收到重复 ack，版本不变。
	ackB := receiveJSONWS(t, wsB)
	assert.Equal(t, "clip_ack", ackB["type"])
	assert.Equal(t, "clip-msg-1", ackB["id"])
	assert.EqualValues(t, 1, ackB["version"])
}

func TestInvalidTokenDoesNotUpgradeWebSocket(t *testing.T) {
	fixture := newIntegrationFixture(t, nil)

	dialer := websocket.Dialer{HandshakeTimeout: 3 * time.Second}
	header := map[string][]string{"Authorization": {"Bearer invalid-signature-token-here"}}
	_, _, err := dialer.Dial(fixture.s.wsURL, header)
	require.Error(t, err, "invalid token must not upgrade")
}

func TestReconnectRestoresHighestSnapshot(t *testing.T) {
	fixture := newIntegrationFixture(t, func(cfg *config.RuntimeConfig) {
		cfg.Limits.SnapshotWindowSeconds = 10
	})

	// 预置持久化版本 7。
	fixture.store.SaveVersion("alice", 7)

	base := "http://" + fixture.s.Authority
	token := loginHTTP(t, insecureHTTPClient(), base, "alice", "password123")

	wsA := dialWS(t, fixture.s.wsURL, token)
	wsB := dialWS(t, fixture.s.wsURL, token)
	defer closeWS(t, wsA)
	defer closeWS(t, wsB)

	modifiedTime := time.Now().UTC()

	// A：版本 7 快照。
	sendJSONWS(t, wsA, helloMessage("client-A", "Device A", 7,
		`{"payload":"snapshot-v7","encrypted":false,"hash":"hash7","localModifiedAtUtc":"`+modifiedTime.Format("2006-01-02T15:04:05.9999999Z07:00")+`"}`))

	// B：版本 8 快照（晚 1 秒）。
	sendJSONWS(t, wsB, helloMessage("client-B", "Device B", 8,
		`{"payload":"snapshot-v8","encrypted":false,"hash":"hash8","localModifiedAtUtc":"`+modifiedTime.Add(time.Second).Format("2006-01-02T15:04:05.9999999Z07:00")+`"}`))

	// 等待用户队列处理。
	time.Sleep(50 * time.Millisecond)

	// 显式关闭恢复窗口触发立即选举与广播。
	hub := fixture.srv.GetOrCreateHub("alice")
	hub.CloseRecoveryWindow(time.Now().UTC().Add(time.Minute))

	welcomeA := receiveJSONWS(t, wsA)
	welcomeB := receiveJSONWS(t, wsB)

	assert.Equal(t, "welcome", welcomeA["type"])
	latestA := welcomeA["latest"].(map[string]any)
	assert.EqualValues(t, 8, latestA["version"])
	assert.Equal(t, "snapshot-v8", latestA["payload"])

	assert.Equal(t, "welcome", welcomeB["type"])
	latestB := welcomeB["latest"].(map[string]any)
	assert.EqualValues(t, 8, latestB["version"])
	assert.Equal(t, "snapshot-v8", latestB["payload"])
}

func TestAbruptDisconnectIsLoggedAndServerContinues(t *testing.T) {
	fixture := newIntegrationFixture(t, nil)

	base := "http://" + fixture.s.Authority
	token := loginHTTP(t, insecureHTTPClient(), base, "alice", "password123")

	wsA := dialWS(t, fixture.s.wsURL, token)
	wsB := dialWS(t, fixture.s.wsURL, token)
	defer closeWS(t, wsB)

	sendJSONWS(t, wsA, helloMessage("client-A", "Device A", 0, ""))
	_ = receiveJSONWS(t, wsA)

	sendJSONWS(t, wsB, helloMessage("client-B", "Device B", 0, ""))
	_ = receiveJSONWS(t, wsB)

	// 猝然中断 A（等价 wsA.Abort() + Dispose）。
	require.NoError(t, wsA.UnderlyingConn().Close())
	_ = wsA.Close()

	// B 发送 clip——服务器继续正常工作。
	sendJSONWS(t, wsB, `{"type":"clip","id":"clip-after-abort","payload":"Still works","encrypted":false,"hash":"h2"}`)

	ackB := receiveJSONWS(t, wsB)
	assert.Equal(t, "clip_ack", ackB["type"])
	assert.Equal(t, "clip-after-abort", ackB["id"])

	// 结构化日志不得泄露凭据。
	entries := fixture.logs.snapshot()
	assert.NotEmpty(t, entries)
	for _, entry := range entries {
		assert.NotContains(t, strings.ToLower(entry), "password123")
		assert.NotContains(t, strings.ToLower(entry), "12345678901234567890123456789012")
	}
}
