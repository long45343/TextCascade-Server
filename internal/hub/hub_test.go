package hub_test

import (
	"context"
	"fmt"
	"log/slog"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"

	"github.com/long45343/TextCascade-Server/internal/auth"
	"github.com/long45343/TextCascade-Server/internal/clock"
	"github.com/long45343/TextCascade-Server/internal/config"
	"github.com/long45343/TextCascade-Server/internal/core"
	"github.com/long45343/TextCascade-Server/internal/hub"
	"github.com/long45343/TextCascade-Server/internal/models"
	"github.com/long45343/TextCascade-Server/internal/protocol"
	"github.com/long45343/TextCascade-Server/internal/state"
	syncserver "github.com/long45343/TextCascade-Server/internal/sync"
	"github.com/long45343/TextCascade-Server/internal/users"
)

// fakeCoordinator 对应 C# FakeCoordinator / RecordingCoordinator。
type fakeCoordinator struct {
	mu               sync.Mutex
	cancelled        []cancelledCall
	rebuiltHubs      []*hub.Hub
	removedEmptyHubs []*hub.Hub
	warnings         *[]string
}

type cancelledCall struct {
	Connection *models.Connection
	Reason     string
}

func (f *fakeCoordinator) Logger() *slog.Logger {
	if f.warnings != nil {
		return slog.New(&warningCapture{warnings: f.warnings})
	}
	return slog.New(slog.NewTextHandler(&strings.Builder{}, nil))
}

func (f *fakeCoordinator) CancelConnection(connection *models.Connection, reason string) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.cancelled = append(f.cancelled, cancelledCall{connection, reason})
}

func (f *fakeCoordinator) RebuildHub(h *hub.Hub) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.rebuiltHubs = append(f.rebuiltHubs, h)
}

func (f *fakeCoordinator) RemoveEmptyHubAfterRecovery(h *hub.Hub) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.removedEmptyHubs = append(f.removedEmptyHubs, h)
}

func (f *fakeCoordinator) rebuildCount() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return len(f.rebuiltHubs)
}

type warningCapture struct{ warnings *[]string }

func (h *warningCapture) Enabled(_ context.Context, level slog.Level) bool {
	return level >= slog.LevelWarn
}

func (h *warningCapture) Handle(_ context.Context, r slog.Record) error {
	if r.Level >= slog.LevelWarn {
		*h.warnings = append(*h.warnings, r.Message)
	}
	return nil
}

func (h *warningCapture) WithAttrs(attrs []slog.Attr) slog.Handler { return h }
func (h *warningCapture) WithGroup(name string) slog.Handler       { return h }

// noOpHasher 供 syncserver.Server 构造使用（不执行真实 Argon2）。
type noOpHasher struct{}

func (noOpHasher) Hash(password string, params auth.Params) string {
	return "$argon2id$v=19$m=19456,t=2,p=1$ZHVtbXlzYWx0$ZHVtbXloYXNo"
}
func (noOpHasher) Verify(password, encodedHash string) bool                { return false }
func (noOpHasher) NeedsRehash(encodedHash string, params auth.Params) bool { return false }

var _ auth.PasswordHasher = noOpHasher{}

func newTestConfig() *config.RuntimeConfig {
	cfg := config.Defaults()
	return &cfg
}

func newTestHub(t *testing.T, initialVersion uint64) (*hub.Hub, *fakeCoordinator) {
	t.Helper()
	store, err := state.NewStore(t.TempDir()+"/state.json", 0, nil)
	require.NoError(t, err)
	t.Cleanup(store.Stop)
	cfg := newTestConfig()
	coord := &fakeCoordinator{}
	h := hub.New("alice", cfg, time.Unix(1760000000, 0).UTC(), coord, store, initialVersion)
	return h, coord
}

func newDummyConnection(t *testing.T, cfg *config.RuntimeConfig, clientID string) *models.Connection {
	t.Helper()
	return models.NewConnection("conn-"+clientID, "alice", clientID, clientID, nil, cfg)
}

// ---- UserHubCoordinationTests ----

func TestUserHubDoesNotDependOnSyncServerConcreteType(t *testing.T) {
	coord := &fakeCoordinator{}
	store, err := state.NewStore(t.TempDir()+"/state.json", 0, nil)
	require.NoError(t, err)
	t.Cleanup(store.Stop)

	cfg := newTestConfig()
	cfg.Limits.SnapshotWindowSeconds = 0

	h := hub.New("alice", cfg, time.Now().UTC(), coord, store, 1)

	h.CloseRecoveryWindow(time.Now().UTC())

	require.Len(t, coord.removedEmptyHubs, 1)
	assert.Same(t, h, coord.removedEmptyHubs[0])
}

func TestUserLoopFailureNotifiesCoordinator(t *testing.T) {
	coord := &fakeCoordinator{}
	store, err := state.NewStore(t.TempDir()+"/state.json", 0, nil)
	require.NoError(t, err)
	t.Cleanup(store.Stop)
	cfg := newTestConfig()

	// 初始版本设为 MaxUint64，下一条 clip 触发版本溢出。
	h := hub.New("alice", cfg, time.Now().UTC(), coord, store, ^uint64(0))

	h.StartIfIdle()

	conn := newDummyConnection(t, cfg, "c1")
	h.AddConnection(conn)

	// 关闭恢复窗口使 clip 可被应用。
	h.CloseRecoveryWindow(time.Now().UTC().Add(10 * time.Second))

	clip := &protocol.ClientClip{ID: "id-overflow", Payload: "data", Encrypted: false, Hash: "hash"}
	assert.True(t, h.TryWriteJob(models.ClipJob{Sender: conn, Clip: clip}))

	// 等待用户循环失败通知。
	deadline := time.Now().Add(3 * time.Second)
	for coord.rebuildCount() == 0 && time.Now().Before(deadline) {
		time.Sleep(20 * time.Millisecond)
	}

	require.Equal(t, 1, coord.rebuildCount())
	assert.Same(t, h, coord.rebuiltHubs[0])
}

// ---- UserLoopConcurrencyTests ----

// waitLoopActive 阻塞直到用户循环被证明正在消费 job（处理一个可观测的
// PongJob 并更新 LastSeen）。此门控消除测试对调度时序的依赖：一旦 job 被
// 处理，readerActive 必然被该循环持有且不会再释放（RunUserLoop 不返回）。
func waitLoopActive(t *testing.T, h *hub.Hub) {
	t.Helper()
	conn := newDummyConnection(t, newTestConfig(), "gate")
	// HelloReceived 初始为 false，循环处理 HelloJob 后翻真——无初始化值歧义。
	require.True(t, h.TryWriteJob(models.HelloJob{
		Connection: conn,
		Hello:      &protocol.ClientHello{ClientID: "gate", ClientName: "gate"},
	}))
	deadline := time.Now().Add(10 * time.Second)
	for !conn.State.HelloReceived() {
		if time.Now().After(deadline) {
			t.Fatal("user loop did not start in time")
		}
		time.Sleep(10 * time.Millisecond)
	}
}

func TestStartIfIdleCreatesSingleLoopUnderConcurrency(t *testing.T) {
	h, _ := newTestHub(t, 1)

	var wg sync.WaitGroup
	for i := 0; i < 100; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			h.StartIfIdle()
		}()
	}
	wg.Wait()

	// 门控：确证唯一循环正在运行。
	waitLoopActive(t, h)

	// 循环占住 readerActive：再次调用 RunUserLoop 必被 CAS 立即拒绝。
	err := h.RunUserLoop()
	require.Error(t, err)
	assert.Contains(t, err.Error(), "already running")
}

func TestRunUserLoopRejectsConcurrentReader(t *testing.T) {
	h, _ := newTestHub(t, 1)

	go func() {
		_ = h.RunUserLoop()
	}()

	// 门控：等待首个循环真正占住 readerActive（而非依赖 sleep 时序）。
	waitLoopActive(t, h)

	err := h.RunUserLoop()
	require.Error(t, err)
	assert.Equal(t, "User loop is already running.", err.Error())
}

// ---- IdempotencyBehaviorTests（U22–U25 + referenceId）----

func clipByID(id, payload, hash string, encrypted bool) *protocol.ClientClip {
	return &protocol.ClientClip{ID: id, Payload: payload, Encrypted: encrypted, Hash: hash}
}

// dequeueAck 从发送队列读 clip_ack / rate_limited。
func dequeueAck(conn *models.Connection) (bool, []byte) {
	for {
		select {
		case payload, ok := <-conn.State.SendCh():
			if !ok {
				return false, nil
			}
			text := string(payload)
			if strings.Contains(text, "clip_ack") {
				return true, payload
			}
			if strings.Contains(text, "rate_limited") {
				return false, payload
			}
		default:
			return false, nil
		}
	}
}

func drainQueue(conn *models.Connection) {
	for {
		select {
		case <-conn.State.SendCh():
		default:
			return
		}
	}
}

// U22
func TestDuplicateIdAfterBucketDrainedStillAcked(t *testing.T) {
	h, _ := newTestHub(t, 0)
	cfg := newTestConfig()
	sender := newDummyConnection(t, cfg, "client-A")

	now := time.Unix(1760000000, 0).UTC()

	// 用 10 个不同 clip 耗尽突发配额；时间不推进，补币为 0。
	for index := 0; index < 10; index++ {
		h.ApplyClip(clipByID(fmtID(index), fmtPayload(index), "h", false), sender, now)
	}
	drainQueue(sender)

	// 第 11 个不同 clip 必须被限速。
	h.ApplyClip(clipByID("id-overflow", "payload-overflow", "h", false), sender, now)
	acked, _ := dequeueAck(sender)
	assert.False(t, acked, "11th distinct clip should be rate limited")

	// 首个 clip 的重复发送不消耗令牌，仍应 ack。
	h.ApplyClip(clipByID("id-0", "payload-0", "h", false), sender, now)
	acked, payload := dequeueAck(sender)
	require.True(t, acked, "duplicate id should bypass the token bucket")

	// 重复 ack 重放 id-0 最初的版本（第 1 个 clip），hub 版本不越过已耗尽的突发。
	decoded, err := protocol.Decode(payload, 3)
	require.NoError(t, err)
	versionNode := decoded.Get("version")
	require.NotNil(t, versionNode)
	assert.Equal(t, "1", versionNode.RawNumber())
	assert.EqualValues(t, 10, h.Version())
}

// U23
func TestDuplicateIdNewContentIsTreatedAsFreshMessage(t *testing.T) {
	h, coord := newTestHub(t, 0)
	cfg := newTestConfig()
	warnings := &[]string{}
	coord.warnings = warnings
	sender := newDummyConnection(t, cfg, "client-A")
	now := time.Unix(1760000000, 0).UTC()

	h.ApplyClip(clipByID("same-id", "first", "h", false), sender, now)
	assert.EqualValues(t, 1, h.Version())
	drainQueue(sender)

	*coord.warnings = nil
	h.ApplyClip(clipByID("same-id", "second", "h2", false), sender, now)

	// 新内容：消耗令牌、产生新版本、记录复用 warning。
	acked, _ := dequeueAck(sender)
	assert.True(t, acked, "fresh-clip path should succeed while tokens remain")
	assert.EqualValues(t, 2, h.Version())
	found := false
	for _, entry := range *warnings {
		if strings.Contains(entry, "Replacing reused clip id") {
			found = true
		}
	}
	assert.True(t, found)
}

// U24
func TestIsUnchangedDuplicateForUnknownIDReturnsFalse(t *testing.T) {
	ring := core.NewRing(4)
	latest, unchanged := ring.IsUnchangedDuplicate("missing", "payload", "hash", false)
	assert.False(t, unchanged)
	assert.Nil(t, latest)
}

// U25
func TestTryAcquireBoundaryCases(t *testing.T) {
	now := time.Unix(1760000000, 0).UTC()

	// 突发边界：burst 次通过，同瞬间下一次失败。
	bucket := core.NewBucket(3, 2.0, now)
	assert.True(t, bucket.TryAcquire(now))
	assert.True(t, bucket.TryAcquire(now))
	assert.True(t, bucket.TryAcquire(now))
	assert.False(t, bucket.TryAcquire(now))

	// 半秒补币（2 tokens/sec）授予一枚。
	assert.True(t, bucket.TryAcquire(now.Add(500*time.Millisecond)))

	// 时钟回拨直接拒绝。
	assert.False(t, bucket.TryAcquire(now))
}

func TestDuplicateIdRateLimitedErrorCarriesReferenceID(t *testing.T) {
	h, _ := newTestHub(t, 0)
	cfg := newTestConfig()
	sender := newDummyConnection(t, cfg, "client-A")
	now := time.Unix(1760000000, 0).UTC()

	for index := 0; index < 10; index++ {
		h.ApplyClip(clipByID(fmtID(index), fmtPayload(index), "h", false), sender, now)
	}
	drainQueue(sender)

	h.ApplyClip(clipByID("id-overflow", "payload-overflow", "h", false), sender, now)
	acked, payload := dequeueAck(sender)
	assert.False(t, acked)
	require.NotNil(t, payload)

	decoded, err := protocol.Decode(payload, 3)
	require.NoError(t, err)
	code := decoded.Get("code")
	referenceID := decoded.Get("referenceId")
	require.NotNil(t, code)
	require.NotNil(t, referenceID)
	assert.Equal(t, "rate_limited", code.Str())
	assert.Equal(t, "id-overflow", referenceID.Str())
}

// ---- RuntimeStateAndProtocolTests（恢复窗口部分）----

func newSyncServerForRecovery(t *testing.T) (*syncserver.Server, *state.Store) {
	t.Helper()
	cfg := newTestConfig()
	store, err := state.NewStore(t.TempDir()+"/state.json", 0, nil)
	require.NoError(t, err)
	t.Cleanup(store.Stop)
	f := &users.UsersFile{}
	srv := syncserver.New(cfg, f, store, noOpHasher{}, clock.System, silentTestLogger())
	return srv, store
}

func silentTestLogger() *slog.Logger {
	return slog.New(slog.NewTextHandler(&strings.Builder{}, nil))
}

func TestRecoveryWindowRestoresSnapshotAtPersistedVersion(t *testing.T) {
	path := t.TempDir() + "/state.json"

	initialStore, err := state.NewStore(path, 0, nil)
	require.NoError(t, err)
	initialStore.SaveVersion("alice", 7)
	initialStore.Flush()
	initialStore.Stop()

	cfg := newTestConfig()
	stateStore, err := state.NewStore(path, 0, nil)
	require.NoError(t, err)
	t.Cleanup(stateStore.Stop)
	f := &users.UsersFile{}
	srv := syncserver.New(cfg, f, stateStore, noOpHasher{}, clock.System, silentTestLogger())
	testStart := time.Unix(1760000000, 0).UTC()

	h := hub.New("alice", cfg, testStart, srv, stateStore, 7)
	modified := time.Unix(1759999990, 0).UTC()
	h.AcceptSnapshot(&protocol.ClientHello{
		ClientID:          "client-a",
		ClientName:        "Client A",
		LastServerVersion: 7,
		Snapshot:          &protocol.ClipSnapshot{Payload: "restored", Encrypted: false, Hash: "client-local-hash", LocalModifiedAtUtc: modified},
	})

	h.CloseRecoveryWindow(testStart.Add(3 * time.Second))

	require.NotNil(t, h.Latest())
	assert.EqualValues(t, 7, h.Version())
	assert.Equal(t, "restored", h.Latest().Payload)
}

func TestRecoveryWindowIgnoresStaleSnapshot(t *testing.T) {
	path := t.TempDir() + "/state.json"

	initialStore, err := state.NewStore(path, 0, nil)
	require.NoError(t, err)
	initialStore.SaveVersion("alice", 7)
	initialStore.Flush()
	initialStore.Stop()

	cfg := newTestConfig()
	stateStore, err := state.NewStore(path, 0, nil)
	require.NoError(t, err)
	t.Cleanup(stateStore.Stop)
	f := &users.UsersFile{}
	srv := syncserver.New(cfg, f, stateStore, noOpHasher{}, clock.System, silentTestLogger())
	testStart := time.Unix(1760000000, 0).UTC()

	h := hub.New("alice", cfg, testStart, srv, stateStore, 7)
	h.AcceptSnapshot(&protocol.ClientHello{
		ClientID:          "client-a",
		ClientName:        "Client A",
		LastServerVersion: 6,
		Snapshot:          &protocol.ClipSnapshot{Payload: "stale", Encrypted: false, Hash: "client-local-hash", LocalModifiedAtUtc: testStart},
	})

	h.CloseRecoveryWindow(testStart.Add(3 * time.Second))

	assert.Nil(t, h.Latest())
	assert.EqualValues(t, 7, h.Version())
}

func fmtID(index int) string      { return "id-" + itoa(index) }
func fmtPayload(index int) string { return "payload-" + itoa(index) }

func itoa(i int) string { return fmt.Sprintf("%d", i) }
