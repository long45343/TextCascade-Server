// Package models 是 Models（ConnectionContext.cs / ConnectionStateBag.cs /
// ReceivedMessage.cs）与 Hub 层数据类型（UserJobs.cs）的迁移。
// Connection 与 UserJob 放在本包以避免 models ↔ hub 循环依赖；
// 包布局相对 spec §5 的唯一偏差，行为不受影响。
package models

import (
	"context"
	"sync"
	"time"

	"github.com/gorilla/websocket"
	"github.com/long45343/TextCascade-Server/internal/config"
	"github.com/long45343/TextCascade-Server/internal/protocol"
)

// FrameType 对应 C# WebSocketMessageType（仅协议关心的两种）。
type FrameType int

const (
	FrameClose FrameType = iota
	FrameData
)

// Frame 对应 C# ReceivedMessage record。
type Frame struct {
	Type    FrameType
	Payload []byte
}

// StateBag 对应 C# ConnectionStateBag：lastSeen/lastPingAt 锁内赋值；
// SendCh 有界 chan（等价 bounded Channel）；ctx/cancel 等价 CTS。
type StateBag struct {
	gate                sync.Mutex
	lastSeen            time.Time
	lastPingAt          time.Time
	closed              bool
	helloTimeoutStarted bool
	pongAwaited         bool

	helloReceived bool
	helloDeadline time.Time

	sendCh chan []byte
	ctx    context.Context
	cancel context.CancelFunc

	writeMu      sync.Mutex
	peerOnce     sync.Once
	peerClosedCh chan struct{}
}

// NewStateBag 对应 C# 构造器：HelloDeadline = now + helloTimeoutSeconds。
func NewStateBag(cfg *config.RuntimeConfig) *StateBag {
	now := time.Now().UTC()
	ctx, cancel := context.WithCancel(context.Background())
	return &StateBag{
		lastSeen:      now,
		lastPingAt:    now,
		sendCh:        make(chan []byte, cfg.Limits.SendQueueCapacity),
		ctx:           ctx,
		cancel:        cancel,
		helloDeadline: now.Add(time.Duration(cfg.Limits.HelloTimeoutSeconds) * time.Second),
		peerClosedCh:  make(chan struct{}),
	}
}

// LastSeen 取值。
func (s *StateBag) LastSeen() time.Time {
	s.gate.Lock()
	defer s.gate.Unlock()
	return s.lastSeen
}

// SetLastSeen 赋值。
func (s *StateBag) SetLastSeen(t time.Time) {
	s.gate.Lock()
	defer s.gate.Unlock()
	s.lastSeen = t
}

// LastPingAt 取值。
func (s *StateBag) LastPingAt() time.Time {
	s.gate.Lock()
	defer s.gate.Unlock()
	return s.lastPingAt
}

// SetLastPingAt 赋值。
func (s *StateBag) SetLastPingAt(t time.Time) {
	s.gate.Lock()
	defer s.gate.Unlock()
	s.lastPingAt = t
}

// MarkPingAwaitingPong 对应同名方法。
func (s *StateBag) MarkPingAwaitingPong() {
	s.gate.Lock()
	defer s.gate.Unlock()
	s.pongAwaited = true
}

// TryTakePongAwaiting 对应同名方法。
func (s *StateBag) TryTakePongAwaiting() bool {
	s.gate.Lock()
	defer s.gate.Unlock()
	if !s.pongAwaited {
		return false
	}
	s.pongAwaited = false
	return true
}

// IsClosed 对应 IsClosed 属性。
func (s *StateBag) IsClosed() bool {
	s.gate.Lock()
	defer s.gate.Unlock()
	return s.closed
}

// MarkClosed CAS 守卫：首次置 true 返回 true。
func (s *StateBag) MarkClosed() bool {
	s.gate.Lock()
	defer s.gate.Unlock()
	if s.closed {
		return false
	}
	s.closed = true
	return true
}

// TryStartHelloTimeout 对应同名方法。
func (s *StateBag) TryStartHelloTimeout() bool {
	s.gate.Lock()
	defer s.gate.Unlock()
	if s.helloTimeoutStarted || s.closed {
		return false
	}
	s.helloTimeoutStarted = true
	return true
}

// TryEnqueueSend 对应同名方法：select default，满 false。
func (s *StateBag) TryEnqueueSend(payload []byte) bool {
	select {
	case s.sendCh <- payload:
		return true
	default:
		return false
	}
}

// SendCh 发送队列（由 sendLoop 消费）。
func (s *StateBag) SendCh() <-chan []byte { return s.sendCh }

// Context 连接取消上下文（等价 C# Cts.Token）。
func (s *StateBag) Context() context.Context { return s.ctx }

// Cancel 等价 C# Cts.Cancel()（幂等）。
func (s *StateBag) Cancel() {
	s.cancel()
}

// HelloReceived 是否已收到 hello。
func (s *StateBag) HelloReceived() bool {
	s.gate.Lock()
	defer s.gate.Unlock()
	return s.helloReceived
}

// SetHelloReceived 赋值。
func (s *StateBag) SetHelloReceived(v bool) {
	s.gate.Lock()
	defer s.gate.Unlock()
	s.helloReceived = v
}

// HelloDeadline hello 截止时间（构造时定死）。
func (s *StateBag) HelloDeadline() time.Time { return s.helloDeadline }

// WriteMu 串行化对底层连接的数据帧写（gorilla 单写者）。
func (s *StateBag) WriteMu() *sync.Mutex { return &s.writeMu }

// PeerClosed 返回对端关闭信号 channel（close 帧到达或读循环结束时关闭）。
func (s *StateBag) PeerClosed() <-chan struct{} { return s.peerClosedCh }

// SignalPeerGone 幂等关闭对端关闭信号。
func (s *StateBag) SignalPeerGone() {
	s.peerOnce.Do(func() { close(s.peerClosedCh) })
}

// WriteData 在 writeMu 保护下发送一条数据帧文本
// （gorilla 要求单写者；直接写路径与 sendLoop 共享同一把锁）。
func (c *Connection) WriteData(payload []byte) error {
	c.State.writeMu.Lock()
	defer c.State.writeMu.Unlock()
	return c.Conn.WriteMessage(websocket.TextMessage, payload)
}

// Connection 对应 C# ConnectionContext：
// Hub 在转正时一次性赋值，此后不可变。
type Connection struct {
	ID         string
	Username   string
	ClientID   string
	ClientName string
	Conn       *websocket.Conn
	State      *StateBag

	hubMu sync.Mutex
	hub   HubRef
}

// NewConnection 构造（provisional 或转正连接）。
func NewConnection(id, username, clientID, clientName string, conn *websocket.Conn, cfg *config.RuntimeConfig) *Connection {
	return &Connection{
		ID:         id,
		Username:   username,
		ClientID:   clientID,
		ClientName: clientName,
		Conn:       conn,
		State:      NewStateBag(cfg),
	}
}

// AttachHub 转正：一次性赋值（幂等保护）。
func (c *Connection) AttachHub(h HubRef) {
	c.hubMu.Lock()
	defer c.hubMu.Unlock()
	if c.hub == nil {
		c.hub = h
	}
}

// Hub 当前 hub（未转正返回 nil）。
func (c *Connection) Hub() HubRef {
	c.hubMu.Lock()
	defer c.hubMu.Unlock()
	return c.hub
}

// RecoveryDecision 对应 C# RecoveryDecision。
type RecoveryDecision int

const (
	DecisionQueued RecoveryDecision = iota
	DecisionProcessNow
	DecisionQueueFull
)

// RecoveryClip 对应 C# RecoveryClip record。
type RecoveryClip struct {
	Clip       *protocol.ClientClip
	Connection *Connection
}

// UserJob 对应 C# UserJob 层级。
type UserJob interface{ isUserJob() }

// ClipJob 对应 C# ClipJob。
type ClipJob struct {
	Sender *Connection
	Clip   *protocol.ClientClip
}

// HelloJob 对应 C# HelloJob。
type HelloJob struct {
	Connection *Connection
	Hello      *protocol.ClientHello
}

// PongJob 对应 C# PongJob。
type PongJob struct {
	Connection *Connection
	Pong       *protocol.ClientPong
}

// DisconnectJob 对应 C# DisconnectJob。
type DisconnectJob struct {
	Connection *Connection
	Reason     string
}

func (ClipJob) isUserJob()       {}
func (HelloJob) isUserJob()      {}
func (PongJob) isUserJob()       {}
func (DisconnectJob) isUserJob() {}

// HubRef 是 hub 反向引用的最小接口（由 *hub.Hub 实现）。
type HubRef interface {
	RemoveConnection(c *Connection) bool
	ClassifyClip(clip *protocol.ClientClip, c *Connection) RecoveryDecision
	TryWriteJob(job UserJob) bool
}
