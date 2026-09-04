// Package hub 是 UserHub.cs / UserRegistry.cs / UserJobs.cs / IConnectionCoordinator.cs 的迁移。
// 本文件为 Coordinator 接口（由 sync.Server 实现）。
package hub

import (
	"log/slog"

	"github.com/long45343/TextCascade-Server/internal/models"
)

// Coordinator 对应 C# IConnectionCoordinator；方法集一致。
type Coordinator interface {
	Logger() *slog.Logger
	CancelConnection(connection *models.Connection, reason string)
	RebuildHub(h *Hub)
	RemoveEmptyHubAfterRecovery(h *Hub)
}
