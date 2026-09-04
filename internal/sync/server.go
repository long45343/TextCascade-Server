// Package sync 是 SyncServer.cs 的迁移（IConnectionCoordinator 实现）。
// 含 §13.8 停机 close 握手等待无超时现状与 §13.9 队列满静默熔断现状（1:1 保留）。
package sync

import (
	"log/slog"
	"sync"
	"sync/atomic"
	"time"

	"github.com/gorilla/websocket"

	"github.com/long45343/TextCascade-Server/internal/auth"
	"github.com/long45343/TextCascade-Server/internal/clock"
	"github.com/long45343/TextCascade-Server/internal/config"
	"github.com/long45343/TextCascade-Server/internal/core"
	"github.com/long45343/TextCascade-Server/internal/hub"
	"github.com/long45343/TextCascade-Server/internal/logging"
	"github.com/long45343/TextCascade-Server/internal/models"
	"github.com/long45343/TextCascade-Server/internal/protocol"
	"github.com/long45343/TextCascade-Server/internal/state"
	"github.com/long45343/TextCascade-Server/internal/users"
)

// Server 对应 C# SyncServer。
type Server struct {
	registry *hub.Registry

	pendingMu     sync.Mutex
	pendingHellos []*models.Connection

	hasher auth.PasswordHasher
	clk    clock.Clock
	store  *state.Store

	lookup atomic.Pointer[map[string]users.UserRecord]

	loginDummyHash string
	logger         *slog.Logger
	processStart   time.Time
	cfg            *config.RuntimeConfig
	limiter        *core.Limiter
}

// New 对应 C# SyncServer 构造。
func New(cfg *config.RuntimeConfig, f *users.UsersFile, store *state.Store, hasher auth.PasswordHasher, clk clock.Clock, logger *slog.Logger) *Server {
	lookup := f.BuildLookup()
	s := &Server{
		registry:     hub.NewRegistry(),
		hasher:       hasher,
		clk:          clk,
		store:        store,
		logger:       logger,
		processStart: clk.Now(),
		cfg:          cfg,
		limiter:      core.NewLimiter(),
	}
	s.lookup.Store(&lookup)
	s.loginDummyHash = hasher.Hash("textcascade-login-timing-dummy", auth.Params{
		MemoryKiB:   cfg.Auth.Argon2MemoryKiB,
		Iterations:  cfg.Auth.Argon2Iterations,
		Parallelism: cfg.Auth.Argon2Parallelism,
	})
	return s
}

// Config 服务器配置。
func (s *Server) Config() *config.RuntimeConfig { return s.cfg }

// Clock 时钟。
func (s *Server) Clock() clock.Clock { return s.clk }

// Limiter 登录限速器。
func (s *Server) Limiter() *core.Limiter { return s.limiter }

// Logger 日志器（hub.Coordinator 接口实现）。
func (s *Server) Logger() *slog.Logger { return s.logger }

// ProcessStart 进程启动时间。
func (s *Server) ProcessStart() time.Time { return s.processStart }

// Store 运行时状态存储。
func (s *Server) Store() *state.Store { return s.store }

// Registry hub 注册表（扫描器回收恢复窗口用）。
func (s *Server) Registry() *hub.Registry { return s.registry }

// UserLookup 原子读取当前用户表。
func (s *Server) UserLookup() map[string]users.UserRecord {
	return *s.lookup.Load()
}

// ReplaceUserLookup 对应 C# ReplaceUserLookup（atomic.Pointer 等价 Volatile.Write）。
func (s *Server) ReplaceUserLookup(f *users.UsersFile) {
	replacement := f.BuildLookup()
	s.lookup.Store(&replacement)
}

// LoginDeps 为 auth.HandleLogin 装配协作者。
func (s *Server) LoginDeps() *auth.LoginDeps {
	return &auth.LoginDeps{
		Limiter: s.limiter,
		Lookup:  s.UserLookup,
		Clock:   s.clk,
		Hasher:  s.hasher,
		Params: auth.Params{
			MemoryKiB:   s.cfg.Auth.Argon2MemoryKiB,
			Iterations:  s.cfg.Auth.Argon2Iterations,
			Parallelism: s.cfg.Auth.Argon2Parallelism,
		},
		DummyHash: s.loginDummyHash,
		Logger:    s.logger,
	}
}

// GetOrCreateHub 对应 C# GetOrCreateHub：初始版本 = store.GetVersion。
func (s *Server) GetOrCreateHub(username string) *hub.Hub {
	initialVersion := s.store.GetVersion(username)
	h := s.registry.GetOrAdd(username, func(name string) *hub.Hub {
		return hub.New(name, s.cfg, s.processStart, s, s.store, initialVersion)
	})
	h.StartIfIdle()
	return h
}

// ScanHeartbeats 对应 C# ScanHeartbeats（1s 扫描器调用）。
func (s *Server) ScanHeartbeats(now time.Time) {
	timeout := s.cfg.Limits.HeartbeatTimeoutSeconds
	for _, h := range s.registry.All() {
		var timedOut []*models.Connection

		h.LockScan()
		pingInterval := time.Duration(h.Config().Limits.HeartbeatIntervalSeconds) * time.Second
		pingBytes := protocol.MarshalPing(now)
		for i := len(h.ConnectionsUnlocked()) - 1; i >= 0; i-- {
			connection := h.ConnectionsUnlocked()[i]
			if !connection.State.HelloReceived() && !now.Before(connection.State.HelloDeadline()) {
				timedOut = append(timedOut, connection)
				continue
			}

			if connection.State.HelloReceived() && now.Sub(connection.State.LastPingAt()) >= pingInterval {
				connection.State.SetLastPingAt(now)
				connection.State.MarkPingAwaitingPong()
				if !connection.State.TryEnqueueSend(pingBytes) && connection.State.MarkClosed() {
					connection.State.Cancel()
				}
			}

			if now.Sub(connection.State.LastSeen()) >= time.Duration(timeout)*time.Second {
				timedOut = append(timedOut, connection)
			}

			h.MarkActivityForScan(now)
		}
		h.UnlockScan()

		if timedOut != nil {
			for _, connection := range timedOut {
				if !connection.State.HelloReceived() {
					s.enqueueHelloTimeout(connection)
				} else {
					s.CancelConnection(connection, "heartbeat_timeout")
				}
			}
		}

		if h.IsEmpty() && now.Sub(h.LastActivityAt()) >= 10*time.Minute {
			s.registry.RemoveIfEmpty(h, false)
		}
	}

	var expired []*models.Connection
	s.pendingMu.Lock()
	for _, pending := range s.pendingHellos {
		if pending.State.HelloReceived() {
			continue
		}
		if !now.Before(pending.State.HelloDeadline()) {
			expired = append(expired, pending)
		}
	}
	s.pendingMu.Unlock()
	for _, connection := range expired {
		s.enqueueHelloTimeout(connection)
	}
}

// RebuildHub 对应 C# RebuildHub：取消该用户全部连接并重建；进程存活。
func (s *Server) RebuildHub(h *hub.Hub) {
	if !s.registry.Remove(h) {
		return
	}

	for _, connection := range h.Connections() {
		s.CancelConnection(connection, "user_loop_failed")
	}

	s.registry.RemoveIfEmpty(h, true)
}

// RemoveEmptyHubAfterRecovery 对应 C# RemoveEmptyHubAfterRecovery（allowDuringRecovery=true）。
func (s *Server) RemoveEmptyHubAfterRecovery(h *hub.Hub) {
	s.registry.RemoveIfEmpty(h, true)
}

// RegisterPendingHello 对应同名方法。
func (s *Server) RegisterPendingHello(connection *models.Connection) {
	s.pendingMu.Lock()
	s.pendingHellos = append(s.pendingHellos, connection)
	s.pendingMu.Unlock()
}

// UnregisterPendingHello 对应同名方法。
func (s *Server) UnregisterPendingHello(connection *models.Connection) {
	s.pendingMu.Lock()
	for i, pending := range s.pendingHellos {
		if pending == connection {
			s.pendingHellos = append(s.pendingHellos[:i], s.pendingHellos[i+1:]...)
			break
		}
	}
	s.pendingMu.Unlock()
}

func (s *Server) enqueueHelloTimeout(connection *models.Connection) {
	if !connection.State.TryStartHelloTimeout() {
		return
	}

	s.UnregisterPendingHello(connection)
	go s.closeAfterHelloTimeout(connection)
}

// closeAfterHelloTimeout 对应 C# CloseAfterHelloTimeoutAsync（time.AfterFunc 等价）。
func (s *Server) closeAfterHelloTimeout(connection *models.Connection) {
	defer func() {
		s.CancelConnection(connection, "hello_timeout")
	}()

	if connection.State.IsClosed() {
		return
	}

	errorBytes := protocol.MarshalError(&protocol.Error{Code: protocol.ErrHelloTimeout, Message: "Hello timeout."})
	if err := connection.WriteData(errorBytes); err != nil {
		s.EnqueueImmediateClose(connection, "server_busy")
		return
	}
	time.Sleep(100 * time.Millisecond)
	if err := connection.Conn.WriteControl(websocket.CloseMessage,
		websocket.FormatCloseMessage(websocket.ClosePolicyViolation, "hello_timeout"),
		time.Now().Add(5*time.Second)); err != nil {
		s.EnqueueImmediateClose(connection, "server_busy")
	}
}

// CancelConnection 对应 C# CancelConnection：MarkClosed 守卫行为保留
// （含 §13.9 静默熔断现状——本方法产生 disconnect 安全事件，队列满路径不产生）。
func (s *Server) CancelConnection(connection *models.Connection, reason string) {
	s.UnregisterPendingHello(connection)
	if !connection.State.MarkClosed() {
		return
	}
	connection.State.Cancel()
	// 解除读循环阻塞（等价 C# ReceiveAsync 因 CTS 取消而抛出）。
	_ = connection.Conn.UnderlyingConn().Close()
	if h := connection.Hub(); h != nil {
		h.RemoveConnection(connection)
		logging.SecurityEvent(s.logger, "disconnect",
			logging.Field{Key: "username", Value: connection.Username},
			logging.Field{Key: "clientId", Value: connection.ClientID},
			logging.Field{Key: "connectionId", Value: connection.ID},
			logging.Field{Key: "reason", Value: reason})
	}
}

// EnqueueImmediateClose 对应 C# EnqueueImmediateClose：
// 发送队列拥塞时不冲刷 error/close 帧；直接中止底层 socket（Socket.Abort 等价）。
// 不产生 disconnect 安全事件（§13.9）。
func (s *Server) EnqueueImmediateClose(connection *models.Connection, reason string) {
	_ = reason
	if !connection.State.MarkClosed() {
		return
	}
	connection.State.Cancel()
	_ = connection.Conn.UnderlyingConn().Close()
	if h := connection.Hub(); h != nil {
		h.RemoveConnection(connection)
	}
}

// Shutdown 对应 C# ShutdownAsync：bye → 1001 → drain → 取消全部连接 → 同步 flush。
// 34 秒无超时 close 握手现状原样保留（§13.8）。
func (s *Server) Shutdown(drain time.Duration, now time.Time) {
	bye := protocol.MarshalBye("server_shutdown")
	var closeDone []chan struct{}
	for _, h := range s.registry.All() {
		for _, connection := range h.Connections() {
			if connection.State.IsClosed() {
				continue
			}
			if connection.State.TryEnqueueSend(bye) {
				done := make(chan struct{})
				closeDone = append(closeDone, done)
				go func(conn *models.Connection, done chan struct{}) {
					defer close(done)
					closeConnection(conn, websocket.CloseGoingAway, "server_shutdown")
				}(connection, done)
			}
		}
	}

	// Spec §7：广播 bye 后先以 1001 关闭，再等待短暂 drain。
	for _, done := range closeDone {
		<-done
	}
	time.Sleep(drain)
	for _, h := range s.registry.All() {
		for _, connection := range h.Connections() {
			s.CancelConnection(connection, "server_shutdown")
		}
	}

	s.store.Flush()
}

// closeConnection 对应 C# CloseConnectionAsync：发送 close 帧并等待对端回应
// （现状：无超时，见 §13.8）。对端回应或 TCP 断开经由 peerClosed 信号解除等待。
func closeConnection(connection *models.Connection, status int, reason string) {
	_ = connection.Conn.WriteControl(websocket.CloseMessage,
		websocket.FormatCloseMessage(status, reason),
		time.Now().Add(5*time.Second))
	<-connection.State.PeerClosed()
}
