// Package hosting：HeartbeatScannerService.cs → scanner.go。
// 1s ticker；扫描 panic recover 且记日志；停止时由 run.go 触发 Shutdown(2s)。
package hosting

import (
	"context"
	"log/slog"
	"time"

	"github.com/long45343/TextCascade-Server/internal/clock"
	"github.com/long45343/TextCascade-Server/internal/sync"
)

// RunScanner 对应 C# HeartbeatScannerService.ExecuteAsync。
func RunScanner(ctx context.Context, srv *sync.Server, clk clock.Clock, logger *slog.Logger) {
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			func() {
				defer func() {
					if r := recover(); r != nil {
						logger.Error("Heartbeat scan failed.", "exception", slog.AnyValue(r))
					}
				}()
				scan(srv, clk.Now())
			}()
		}
	}
}

func scan(srv *sync.Server, now time.Time) {
	srv.ScanHeartbeats(now)

	recoveryEnd := srv.ProcessStart().Add(time.Duration(srv.Config().Limits.SnapshotWindowSeconds) * time.Second)
	if now.Before(recoveryEnd) {
		return
	}

	for _, h := range srv.Registry().All() {
		h.CloseRecoveryWindow(now)
	}
}
