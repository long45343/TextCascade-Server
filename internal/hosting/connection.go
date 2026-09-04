// Package hosting 是 Hosting 层（ConnectionHandler.cs / SyncEndpoint.cs /
// UserFileWatcher.cs / HeartbeatScannerService.cs）与 ServerHost.cs 的迁移。
// 本文件为 ConnectionHandler.cs → connection.go。
package hosting

import (
	"context"
	"errors"
	"io"
	"time"

	"github.com/gorilla/websocket"

	"github.com/long45343/TextCascade-Server/internal/auth"
	"github.com/long45343/TextCascade-Server/internal/config"
	"github.com/long45343/TextCascade-Server/internal/logging"
	"github.com/long45343/TextCascade-Server/internal/models"
	"github.com/long45343/TextCascade-Server/internal/protocol"
	"github.com/long45343/TextCascade-Server/internal/sync"
)

var (
	errFrameTooLarge = errors.New("frame too large")
	errCanceled      = errors.New("operation canceled")
)

const writeWait = 5 * time.Second

// RunConnection 对应 C# ConnectionHandler.RunAsync：
// 转正（Hub 赋值）、注册 hello 截止、读写循环并等待退出。
func RunConnection(provisional *models.Connection, payload auth.TokenPayload, cfg *config.RuntimeConfig, srv *sync.Server) {
	srv.RegisterPendingHello(provisional)
	ctx := provisional.State.Context()

	received, err := receiveFrameChecked(provisional, cfg.Limits.MaxFrameBytes, ctx)
	switch {
	case errors.Is(err, errFrameTooLarge):
		errorBytes := protocol.MarshalError(&protocol.Error{Code: protocol.ErrFrameTooLarge, Message: "frame_too_large"})
		sendAndClosePreHello(provisional, errorBytes, websocket.CloseMessageTooBig, "frame_too_large", srv)
		return
	case errors.Is(err, errCanceled):
		// Hello 超时由统一心跳扫描器负责；此处 socket 因其他原因被取消（如停机），
		// 落入统一清理路径。
		srv.CancelConnection(provisional, "cancelled")
		return
	case err != nil:
		srv.CancelConnection(provisional, "socket_error")
		return
	case received.Type == models.FrameClose:
		// CloseOutputAsync(1000, "client_closed")
		_ = provisional.Conn.WriteControl(websocket.CloseMessage,
			websocket.FormatCloseMessage(websocket.CloseNormalClosure, "client_closed"), time.Now().Add(writeWait))
		srv.CancelConnection(provisional, "closed")
		return
	}

	parse, parseErr := protocol.ParseClientMessage(received.Payload, cfg)
	if parseErr != nil || parse.Kind != protocol.KindHello {
		var referenceID *string
		if parseErr != nil {
			referenceID = parseErr.ReferenceID
		}
		errorBytes := protocol.MarshalError(&protocol.Error{
			Code:        protocol.ErrInvalidMessage,
			Message:     "Expected a valid hello message.",
			ReferenceID: referenceID,
		})
		sendAndClosePreHello(provisional, errorBytes, websocket.ClosePolicyViolation, "invalid_hello", srv)
		return
	}
	hello := parse.Hello

	hub := srv.GetOrCreateHub(payload.Subject)
	// C# 转正时创建全新 ConnectionContext（新 StateBag：新队列/新 CTS/新 hello 截止）。
	connection := models.NewConnection(provisional.ID, payload.Subject, hello.ClientID, hello.ClientName, provisional.Conn, cfg)
	connection.AttachHub(hub)
	hub.AddConnection(connection)
	logging.SecurityEvent(srv.Logger(), "connect",
		logging.Field{Key: "username", Value: connection.Username},
		logging.Field{Key: "clientId", Value: connection.ClientID},
		logging.Field{Key: "connectionId", Value: connection.ID})
	connection.State.SetHelloReceived(true)
	connection.State.SetLastSeen(time.Now().UTC())
	srv.UnregisterPendingHello(provisional)
	if !hub.TryWriteJob(models.HelloJob{Connection: connection, Hello: hello}) {
		srv.CancelConnection(connection, "user_loop_unavailable")
		return
	}

	done := make(chan struct{})
	go func() {
		defer close(done)
		sendLoop(connection)
	}()
	go func() {
		readLoop(connection, cfg, srv)
		<-done
		srv.CancelConnection(connection, "disconnected")
	}()
}

func readAllLimited(reader interface{ Read([]byte) (int, error) }, maxBytes int) ([]byte, error) {
	payload := make([]byte, 0, 4096)
	buf := make([]byte, 16*1024)
	for {
		n, err := reader.Read(buf)
		if n > 0 {
			if len(payload)+n > maxBytes {
				return nil, errFrameTooLarge
			}
			payload = append(payload, buf[:n]...)
		}
		if err != nil {
			if errors.Is(err, io.EOF) {
				return payload, nil
			}
			return nil, err
		}
	}
}

// sendAndClosePreHello 对应 C# SendAndClosePreHelloAsync：
// 预 hello 阶段错误：invalid_message → 1008、超限 → 1009。
func sendAndClosePreHello(c *models.Connection, errorBytes []byte, closeCode int, reason string, srv *sync.Server) {
	defer func() {
		srv.CancelConnection(c, reason)
	}()

	if !c.State.IsClosed() {
		if err := c.WriteData(errorBytes); err != nil {
			srv.EnqueueImmediateClose(c, "server_busy")
			return
		}
		if err := c.Conn.WriteControl(websocket.CloseMessage,
			websocket.FormatCloseMessage(closeCode, reason), time.Now().Add(writeWait)); err != nil {
			srv.EnqueueImmediateClose(c, "server_busy")
		}
	}
}

// readLoop 对应 C# ReadLoopAsync：帧 → ParseClientMessage → 投递用户队列/本地错误处理。
func readLoop(c *models.Connection, cfg *config.RuntimeConfig, srv *sync.Server) {
	// 读循环以任何方式退出（对端 close、TCP 断开、错误）都解除停机握手等待。
	defer c.State.SignalPeerGone()
	ctx := c.State.Context()
	for {
		if ctx.Err() != nil || c.State.IsClosed() {
			return
		}

		received, err := receiveFrameChecked(c, cfg.Limits.MaxFrameBytes, ctx)
		if err != nil {
			if errors.Is(err, errFrameTooLarge) {
				oversized := protocol.MarshalError(&protocol.Error{Code: protocol.ErrFrameTooLarge, Message: "frame_too_large"})
				sendSafe(c, oversized, srv)
				select {
				case <-ctx.Done():
				case <-time.After(100 * time.Millisecond):
				}
				if !c.State.IsClosed() {
					_ = c.Conn.WriteControl(websocket.CloseMessage,
						websocket.FormatCloseMessage(websocket.CloseMessageTooBig, "frame_too_large"), time.Now().Add(writeWait))
				}
				srv.CancelConnection(c, "frame_too_large")
				return
			}
			// OperationCanceled / WebSocketException 同路退出。
			return
		}

		if received.Type == models.FrameClose {
			// CloseOutputAsync(1000, "client_closed")；不立即取消 CTS（§5.7 现状）。
			_ = c.Conn.WriteControl(websocket.CloseMessage,
				websocket.FormatCloseMessage(websocket.CloseNormalClosure, "client_closed"), time.Now().Add(writeWait))
			return
		}

		if !protocol.CheckFrameSize(len(received.Payload), cfg) {
			errorBytes := protocol.MarshalError(&protocol.Error{Code: protocol.ErrFrameTooLarge, Message: "frame_too_large"})
			sendSafe(c, errorBytes, srv)
			_ = c.Conn.WriteControl(websocket.CloseMessage,
				websocket.FormatCloseMessage(websocket.CloseMessageTooBig, "frame_too_large"), time.Now().Add(writeWait))
			srv.CancelConnection(c, "frame_too_large")
			return
		}

		parse, parseErr := protocol.ParseClientMessage(received.Payload, cfg)
		if parseErr != nil {
			codeName := parseErr.CodeName
			logging.SecurityEvent(srv.Logger(), "reject",
				logging.Field{Key: "username", Value: c.Username},
				logging.Field{Key: "code", Value: codeName},
				logging.Field{Key: "bytes", Value: len(received.Payload)})
			sendSafe(c, protocol.MarshalError(parseErr), srv)
			continue
		}

		switch parse.Kind {
		case protocol.KindClip:
			clip := parse.Clip
			hub := c.Hub()
			if hub == nil {
				srv.CancelConnection(c, "user_loop_unavailable")
				continue
			}

			decision := hub.ClassifyClip(clip, c)
			if decision == models.DecisionQueueFull {
				srv.CancelConnection(c, "recovery_queue_full")
			} else if decision == models.DecisionProcessNow && !hub.TryWriteJob(models.ClipJob{Sender: c, Clip: clip}) {
				srv.CancelConnection(c, "user_loop_unavailable")
			}
		case protocol.KindPong:
			if !c.State.TryTakePongAwaiting() {
				unsolicitedPong := protocol.MarshalError(&protocol.Error{
					Code:    protocol.ErrInvalidMessage,
					Message: "Pong received without an outstanding ping.",
				})
				sendSafe(c, unsolicitedPong, srv)
				continue
			}

			if hub := c.Hub(); hub == nil || !hub.TryWriteJob(models.PongJob{Connection: c, Pong: parse.Pong}) {
				srv.CancelConnection(c, "user_loop_unavailable")
			}
		}
	}
}

// receiveFrameChecked 与 receiveFrame 相同，但把对端 close 映射为 FrameClose
// （等价 C# ReceiveAsync 返回 MessageType.Close）。
func receiveFrameChecked(c *models.Connection, maxBytes int, ctx context.Context) (models.Frame, error) {
	messageType, reader, err := c.Conn.NextReader()
	if err != nil {
		var closeErr *websocket.CloseError
		if errors.As(err, &closeErr) {
			return models.Frame{Type: models.FrameClose}, nil
		}
		if ctx.Err() != nil {
			return models.Frame{}, errCanceled
		}
		return models.Frame{}, err
	}
	_ = messageType

	if ctx.Err() != nil {
		return models.Frame{}, errCanceled
	}

	payload, err := readAllLimited(reader, maxBytes)
	if err != nil {
		if errors.Is(err, errFrameTooLarge) {
			return models.Frame{}, errFrameTooLarge
		}
		return models.Frame{}, err
	}
	return models.Frame{Type: models.FrameData, Payload: payload}, nil
}

// sendLoop 对应 C# ConnectionSendLoopAsync：唯一写者；ctx.Done / 出队写出；
// 取消与非取消异常同路清理。
func sendLoop(c *models.Connection) {
	defer c.State.SignalPeerGone()
	ctx := c.State.Context()
	for {
		select {
		case <-ctx.Done():
			return
		case payload, ok := <-c.State.SendCh():
			if !ok {
				return
			}
			c.State.WriteMu().Lock()
			err := c.Conn.WriteMessage(websocket.TextMessage, payload)
			c.State.WriteMu().Unlock()
			if err != nil {
				return
			}
		}
	}
}

// sendSafe 对应 C# SendSafeAsync。
func sendSafe(c *models.Connection, payload []byte, srv *sync.Server) {
	if !c.State.TryEnqueueSend(payload) {
		srv.EnqueueImmediateClose(c, "server_busy")
	}
}
