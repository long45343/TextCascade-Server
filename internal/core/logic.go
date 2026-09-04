// Package core：logic.go（CoreLogic / SnapshotWinner）。
package core

import (
	"math"
	"sort"
	"time"

	"github.com/long45343/TextCascade-Server/internal/protocol"
)

// Winner 对应 C# SnapshotWinner record。
type Winner struct {
	Snapshot   *protocol.ClipSnapshot
	Version    uint64
	ClientID   string
	ClientName string
}

// NextVersion 对应 C# CoreLogic.NextVersion。
// F7：Go uint64 自增静默 wrap，cur == math.MaxUint64 时显式 panic（→ recover → RebuildHub）。
func NextVersion(cur uint64) uint64 {
	if cur == math.MaxUint64 {
		panic("Version overflow.")
	}
	return cur + 1
}

// WithVersion 对应 C# CoreLogic.WithVersion（不可变替换）；now 为零值时保留原 UpdatedAtUtc。
func WithVersion(latest *protocol.LatestText, next uint64, now time.Time) *protocol.LatestText {
	updated := *latest
	updated.Version = next
	if !now.IsZero() {
		updated.UpdatedAtUtc = now
	}
	return &updated
}

// SelectSnapshotWinner 对应 C# CoreLogic.SelectSnapshotWinner：三规则——
// 版本最大 → localModifiedAtUtc 最新 → clientId 字典序更大（Ordinal）。
func SelectSnapshotWinner(hellos []*protocol.ClientHello) *Winner {
	var candidates []*Winner
	for _, hello := range hellos {
		if hello.LastServerVersion > 0 && hello.Snapshot != nil {
			candidates = append(candidates, &Winner{
				Snapshot:   hello.Snapshot,
				Version:    hello.LastServerVersion,
				ClientID:   hello.ClientID,
				ClientName: hello.ClientName,
			})
		}
	}
	if len(candidates) == 0 {
		return nil
	}

	// C# OrderByDescending 为稳定排序；多键比较展开为单一 less。
	sort.SliceStable(candidates, func(i, j int) bool {
		a, b := candidates[i], candidates[j]
		if a.Version != b.Version {
			return a.Version > b.Version
		}
		if !a.Snapshot.LocalModifiedAtUtc.Equal(b.Snapshot.LocalModifiedAtUtc) {
			return a.Snapshot.LocalModifiedAtUtc.After(b.Snapshot.LocalModifiedAtUtc)
		}
		return a.ClientID > b.ClientID
	})
	return candidates[0]
}
