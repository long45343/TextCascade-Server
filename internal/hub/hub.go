// Package hub：hub.go（UserHub：单消费者循环、快照恢复窗口、ApplyClip 广播）。
package hub

import (
	"errors"
	"log/slog"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/long45343/TextCascade-Server/internal/config"
	"github.com/long45343/TextCascade-Server/internal/core"
	"github.com/long45343/TextCascade-Server/internal/logging"
	"github.com/long45343/TextCascade-Server/internal/models"
	"github.com/long45343/TextCascade-Server/internal/protocol"
	"github.com/long45343/TextCascade-Server/internal/state"
)

// jobQueue 是自实现无界队列（slice + sync.Cond），TryWrite 恒真（spec §6，1:1 语义）。
type jobQueue struct {
	mu       sync.Mutex
	notEmpty *sync.Cond
	items    []models.UserJob
}

func newJobQueue() *jobQueue {
	q := &jobQueue{}
	q.notEmpty = sync.NewCond(&q.mu)
	return q
}

func (q *jobQueue) push(job models.UserJob) {
	q.mu.Lock()
	q.items = append(q.items, job)
	q.mu.Unlock()
	q.notEmpty.Signal()
}

func (q *jobQueue) pop() (models.UserJob, bool) {
	q.mu.Lock()
	defer q.mu.Unlock()
	if len(q.items) == 0 {
		return nil, false
	}
	job := q.items[0]
	q.items = q.items[1:]
	return job, true
}

func (q *jobQueue) wait() models.UserJob {
	q.mu.Lock()
	defer q.mu.Unlock()
	for len(q.items) == 0 {
		q.notEmpty.Wait()
	}
	job := q.items[0]
	q.items = q.items[1:]
	return job
}

// Hub 对应 C# UserHub。
type Hub struct {
	username     string
	cfg          *config.RuntimeConfig
	processStart time.Time
	coordinator  Coordinator
	store        *state.Store

	stateMu sync.Mutex
	latest  *protocol.LatestText
	version uint64

	connMu      sync.Mutex
	connections []*models.Connection

	userLoopMu      sync.Mutex
	userLoopRunning bool
	readerActive    atomic.Int32

	snapshotMu           sync.Mutex
	snapshotCandidates   []*protocol.ClientHello
	snapshotBytes        int
	recoveryQueue        []models.RecoveryClip
	recoveryWindowClosed bool

	clipBucket *core.Bucket
	seenIDs    *core.Ring

	jobs           *jobQueue
	lastActivityNs atomic.Int64
}

// New 对应 C# UserHub 构造。
func New(username string, cfg *config.RuntimeConfig, processStart time.Time, coord Coordinator, store *state.Store, initialVersion uint64) *Hub {
	h := &Hub{
		username:     username,
		cfg:          cfg,
		processStart: processStart,
		coordinator:  coord,
		store:        store,
		version:      initialVersion,
		clipBucket:   core.NewBucket(cfg.RateLimit.ClipBurst, float64(cfg.RateLimit.ClipTokensPerSecond), processStart),
		seenIDs:      core.NewRing(cfg.Limits.SeenIdCapacity),
		jobs:         newJobQueue(),
	}
	h.lastActivityNs.Store(processStart.UnixNano())
	return h
}

// Username 用户名。
func (h *Hub) Username() string { return h.username }

// Config 暴露配置（心跳扫描用）。
func (h *Hub) Config() *config.RuntimeConfig { return h.cfg }

// Latest 取当前最新文本（锁内）。
func (h *Hub) Latest() *protocol.LatestText {
	h.stateMu.Lock()
	defer h.stateMu.Unlock()
	return h.latest
}

// Version 取当前版本（锁内）。
func (h *Hub) Version() uint64 {
	h.stateMu.Lock()
	defer h.stateMu.Unlock()
	return h.version
}

// LastActivityAt 对应 C# LastActivityAt。
func (h *Hub) LastActivityAt() time.Time {
	return time.Unix(0, h.lastActivityNs.Load()).UTC()
}

// IsEmpty 对应 C# IsEmpty。
func (h *Hub) IsEmpty() bool {
	h.connMu.Lock()
	defer h.connMu.Unlock()
	return len(h.connections) == 0
}

// LockScan 等价 C# hub.ScanGate（心跳扫描在锁内遍历内部列表）。
func (h *Hub) LockScan() { h.connMu.Lock() }

// UnlockScan 配对 LockScan。
func (h *Hub) UnlockScan() { h.connMu.Unlock() }

// ConnectionsUnlocked 返回内部连接列表（调用方必须已持有 LockScan）。
func (h *Hub) ConnectionsUnlocked() []*models.Connection { return h.connections }

// Connections 返回快照（对应 C# Connections 属性）。
func (h *Hub) Connections() []*models.Connection {
	h.connMu.Lock()
	defer h.connMu.Unlock()
	snapshot := make([]*models.Connection, len(h.connections))
	copy(snapshot, h.connections)
	return snapshot
}

// AddConnection 对应 C# AddConnection（含恢复窗口关闭后的立即 welcome）。
func (h *Hub) AddConnection(connection *models.Connection) {
	h.connMu.Lock()
	h.connections = append(h.connections, connection)
	h.connMu.Unlock()
	h.markActivity(time.Now().UTC())

	now := time.Now().UTC()
	if h.isRecoveryWindowClosed() {
		broadcastToConnection(connection, protocol.MarshalWelcome(h.Latest()))
		return
	}

	h.EnsureRecoveryWindowClosed(now)
	if h.isRecoveryWindowClosed() {
		return
	}
}

// RemoveConnection 对应 C# RemoveConnection。
func (h *Hub) RemoveConnection(connection *models.Connection) bool {
	h.connMu.Lock()
	removed := false
	for i, conn := range h.connections {
		if conn == connection {
			h.connections = append(h.connections[:i], h.connections[i+1:]...)
			removed = true
			break
		}
	}
	h.connMu.Unlock()
	if removed {
		h.markActivity(time.Now().UTC())
	}
	return removed
}

// StartIfIdle 对应 C# StartIfIdle：mutex 保证单读者，启动单消费 goroutine。
// 用户循环异常 → recover → RebuildHub（F7）。
func (h *Hub) StartIfIdle() {
	h.userLoopMu.Lock()
	if h.userLoopRunning {
		h.userLoopMu.Unlock()
		return
	}
	h.userLoopRunning = true
	h.userLoopMu.Unlock()

	go func() {
		defer func() {
			h.userLoopMu.Lock()
			h.userLoopRunning = false
			h.userLoopMu.Unlock()
		}()
		defer func() {
			if r := recover(); r != nil {
				h.coordinator.Logger().Error("User loop failed; rebuilding hub. username="+h.username, "exception", slog.AnyValue(r))
				h.coordinator.RebuildHub(h)
			}
		}()
		if err := h.RunUserLoop(); err != nil {
			h.coordinator.Logger().Error("User loop failed; rebuilding hub. username="+h.username, "exception", slog.StringValue(err.Error()))
			h.coordinator.RebuildHub(h)
		}
	}()
}

// TryWriteJob 对应 C# TryWriteJob：无界队列恒真。
func (h *Hub) TryWriteJob(job models.UserJob) bool {
	h.jobs.push(job)
	return true
}

// RunUserLoop 对应 C# RunUserLoopAsync；重复启动返回错误（C# InvalidOperationException）。
func (h *Hub) RunUserLoop() error {
	if !h.readerActive.CompareAndSwap(0, 1) {
		return errors.New("User loop is already running.")
	}
	defer h.readerActive.Store(0)

	for {
		job := h.jobs.wait()
		h.processJob(job, time.Now().UTC())
		for {
			next, ok := h.jobs.pop()
			if !ok {
				break
			}
			h.processJob(next, time.Now().UTC())
		}
	}
}

func (h *Hub) processJob(job models.UserJob, now time.Time) {
	switch j := job.(type) {
	case models.ClipJob:
		h.ApplyClip(j.Clip, j.Sender, now)
	case models.PongJob:
		j.Connection.State.SetLastSeen(now)
	case models.HelloJob:
		j.Connection.State.SetHelloReceived(true)
		if j.Hello.Snapshot != nil {
			h.AcceptSnapshot(j.Hello)
		}
	case models.DisconnectJob:
		h.coordinator.CancelConnection(j.Connection, j.Reason)
	}
}

// AcceptSnapshot 对应 C# AcceptSnapshot：预算累计（仅 payload UTF-8 字节）。
func (h *Hub) AcceptSnapshot(hello *protocol.ClientHello) {
	h.snapshotMu.Lock()
	defer h.snapshotMu.Unlock()
	if h.recoveryWindowClosed {
		return
	}
	if hello.Snapshot == nil {
		return
	}
	bytes := len(hello.Snapshot.Payload)
	if h.snapshotBytes+bytes > h.cfg.Limits.SnapshotTotalBytes {
		return
	}
	h.snapshotCandidates = append(h.snapshotCandidates, hello)
	h.snapshotBytes += bytes
}

// ClassifyClip 对应 C# ClassifyClip：恢复窗口有界队列；满断开提交者。
func (h *Hub) ClassifyClip(clip *protocol.ClientClip, connection *models.Connection) models.RecoveryDecision {
	h.snapshotMu.Lock()
	defer h.snapshotMu.Unlock()
	if h.recoveryWindowClosed {
		return models.DecisionProcessNow
	}
	if len(h.recoveryQueue) >= h.cfg.Limits.RecoveryClipQueueCapacity {
		return models.DecisionQueueFull
	}
	h.recoveryQueue = append(h.recoveryQueue, models.RecoveryClip{Clip: clip, Connection: connection})
	return models.DecisionQueued
}

// CloseRecoveryWindow 对应 C# CloseRecoveryWindow：
// 选举 → 恢复 → 按到达序处理恢复队列 → 广播 welcome。
func (h *Hub) CloseRecoveryWindow(now time.Time) {
	h.snapshotMu.Lock()
	if h.recoveryWindowClosed {
		h.snapshotMu.Unlock()
		return
	}
	h.recoveryWindowClosed = true

	winner := core.SelectSnapshotWinner(h.snapshotCandidates)
	if winner != nil {
		h.stateMu.Lock()
		canRestoreLatest := winner.Version > h.version || (winner.Version == h.version && h.latest == nil)
		if !canRestoreLatest {
			winner = nil
		} else {
			if winner.Version > h.version {
				h.store.SaveVersion(h.username, winner.Version)
			}
			h.version = winner.Version
			h.latest = protocol.LatestFromSnapshot(winner.Snapshot, winner.Version, winner.ClientID, winner.ClientName)
		}
		h.stateMu.Unlock()
	}

	clips := make([]models.RecoveryClip, len(h.recoveryQueue))
	copy(clips, h.recoveryQueue)
	h.recoveryQueue = h.recoveryQueue[:0]
	h.snapshotMu.Unlock()

	for _, recovery := range clips {
		if recovery.Connection.State.IsClosed() {
			continue
		}
		h.ApplyClip(recovery.Clip, recovery.Connection, now)
	}

	h.broadcastWelcome(now)

	// Spec §6.2：存活到恢复窗口关闭的空 hub 现在被回收。
	h.coordinator.RemoveEmptyHubAfterRecovery(h)

	h.markActivity(now)
}

func (h *Hub) broadcastWelcome(now time.Time) {
	_ = now
	bytes := protocol.MarshalWelcome(h.Latest())
	for _, connection := range h.Connections() {
		if !connection.State.TryEnqueueSend(bytes) && connection.State.MarkClosed() {
			connection.State.Cancel()
		}
	}
}

// IsRecoveryWindowOpen 对应 C# IsRecoveryWindowOpen：窗口 = ProcessStart + snapshot_window_seconds。
func (h *Hub) IsRecoveryWindowOpen(now time.Time) bool {
	h.snapshotMu.Lock()
	closed := h.recoveryWindowClosed
	h.snapshotMu.Unlock()
	return !closed && now.Before(h.processStart.Add(time.Duration(h.cfg.Limits.SnapshotWindowSeconds)*time.Second))
}

func (h *Hub) isRecoveryWindowClosed() bool {
	h.snapshotMu.Lock()
	defer h.snapshotMu.Unlock()
	return h.recoveryWindowClosed
}

// EnsureRecoveryWindowClosed 对应 C# EnsureRecoveryWindowClosed。
func (h *Hub) EnsureRecoveryWindowClosed(now time.Time) {
	h.snapshotMu.Lock()
	closed := h.recoveryWindowClosed
	h.snapshotMu.Unlock()
	if !closed && !now.Before(h.processStart.Add(time.Duration(h.cfg.Limits.SnapshotWindowSeconds)*time.Second)) {
		h.CloseRecoveryWindow(now)
	}
}

func (h *Hub) markActivity(now time.Time) {
	h.lastActivityNs.Store(now.UnixNano())
}

// MarkActivityForScan 对应 C# MarkActivityForScan（10 分钟空闲回收依据）。
func (h *Hub) MarkActivityForScan(now time.Time) {
	h.markActivity(now)
}

// ApplyClip 对应 C# ApplyClip：幂等（内容比较）→ 令牌桶 → NextVersion → SaveVersion →
// 不可变替换 → 单次序列化广播（排除发送方连接）→ ACK。
// 死分支兜底原样保留（§13.6）：dupLatest 优先，Latest 次之，空 LatestText 兜底（不可达）。
func (h *Hub) ApplyClip(clip *protocol.ClientClip, sender *models.Connection, now time.Time) {
	duplicateLatest, unchanged := h.seenIDs.IsUnchangedDuplicate(clip.ID, clip.Payload, clip.Hash, clip.Encrypted)
	if unchanged {
		ackLatest := duplicateLatest
		if ackLatest == nil {
			ackLatest = h.Latest()
		}
		if ackLatest == nil {
			ackLatest = &protocol.LatestText{
				Payload:        "",
				Version:        h.Version(),
				Hash:           "",
				Encrypted:      false,
				FromClientID:   sender.ClientID,
				FromClientName: sender.ClientName,
				UpdatedAtUtc:   now,
			}
		}
		ackBytes := protocol.MarshalClipAck(clip.ID, ackLatest)
		if !sender.State.TryEnqueueSend(ackBytes) && sender.State.MarkClosed() {
			sender.State.Cancel()
		}
		return
	}

	if _, exists := h.seenIDs.TryGet(clip.ID); exists {
		h.coordinator.Logger().Warn(
			"Replacing reused clip id. username=" + h.username + " clipId=" + clip.ID + " clientId=" + sender.ClientID + " previousVersion=" + strconv.FormatUint(h.Version(), 10))
	}

	if !h.clipBucket.TryAcquire(now) {
		logging.SecurityEvent(h.coordinator.Logger(), "reject",
			logging.Field{Key: "username", Value: h.username},
			logging.Field{Key: "code", Value: "rate_limited"},
			logging.Field{Key: "bytes", Value: len(clip.Payload)})
		errorBytes := protocol.MarshalError(&protocol.Error{Code: protocol.ErrRateLimited, Message: "Clip rate limited.", ReferenceID: &clip.ID})
		if !sender.State.TryEnqueueSend(errorBytes) && sender.State.MarkClosed() {
			sender.State.Cancel()
		}
		return
	}

	next := core.NextVersion(h.Version())
	h.store.SaveVersion(h.username, next)
	h.stateMu.Lock()
	h.version = next
	latest := &protocol.LatestText{
		Payload:        clip.Payload,
		Version:        next,
		Hash:           clip.Hash,
		Encrypted:      clip.Encrypted,
		FromClientID:   sender.ClientID,
		FromClientName: sender.ClientName,
		UpdatedAtUtc:   now,
	}
	h.latest = latest
	h.stateMu.Unlock()
	h.seenIDs.Remember(clip.ID, latest)
	logging.SecurityEvent(h.coordinator.Logger(), "clip",
		logging.Field{Key: "username", Value: h.username},
		logging.Field{Key: "version", Value: latest.Version},
		logging.Field{Key: "clipId", Value: clip.ID},
		logging.Field{Key: "bytes", Value: len(clip.Payload)},
		logging.Field{Key: "fromClientId", Value: sender.ClientID},
		logging.Field{Key: "encrypted", Value: clip.Encrypted})

	broadcastBytes := protocol.MarshalClip(clip.ID, latest)
	var deliveries []string
	for _, connection := range h.Connections() {
		if connection == sender {
			continue
		}
		queued := connection.State.TryEnqueueSend(broadcastBytes)
		if queued {
			deliveries = append(deliveries, connection.ClientID+":queued")
		} else {
			deliveries = append(deliveries, connection.ClientID+":full")
		}
		if !queued && connection.State.MarkClosed() {
			connection.State.Cancel()
		}
	}

	h.coordinator.Logger().Info(
		"Clip broadcast. username=" + h.username + " version=" + strconv.FormatUint(next, 10) + " clipId=" + clip.ID + " recipients=[" + strings.Join(deliveries, ",") + "]")

	ackBytesFinal := protocol.MarshalClipAck(clip.ID, latest)
	if !sender.State.TryEnqueueSend(ackBytesFinal) && sender.State.MarkClosed() {
		sender.State.Cancel()
	}
}

func broadcastToConnection(connection *models.Connection, payload []byte) {
	if !connection.State.TryEnqueueSend(payload) && connection.State.MarkClosed() {
		connection.State.Cancel()
	}
}

// Broadcast 对应 C# BroadcastAsync。
func (h *Hub) Broadcast(payload []byte) {
	for _, connection := range h.Connections() {
		if connection.State.IsClosed() {
			continue
		}
		if !connection.State.TryEnqueueSend(payload) && connection.State.MarkClosed() {
			connection.State.Cancel()
		}
	}
}
