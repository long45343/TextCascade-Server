// Package core：ring.go（SeenIdRing，Dictionary + FIFO 环形淘汰）。
package core

import "github.com/long45343/TextCascade-Server/internal/protocol"

// Ring 对应 C# SeenIdRing。
type Ring struct {
	gate            chan struct{}
	entries         map[string]*protocol.LatestText
	insertionOrder  []string
	nextInsertIndex int
}

// NewRing 构造；capacity 非正 panic（C# ArgumentOutOfRangeException）。
func NewRing(capacity int) *Ring {
	if capacity <= 0 {
		panic("capacity must be positive")
	}
	return &Ring{
		gate:           make(chan struct{}, 1),
		entries:        make(map[string]*protocol.LatestText, capacity),
		insertionOrder: make([]string, capacity),
	}
}

func (r *Ring) lock()   { r.gate <- struct{}{} }
func (r *Ring) unlock() { <-r.gate }

// TryDuplicate 对应 C# TryDuplicate：已存在返回 true；否则记住（结果为 nil）返回 false。
func (r *Ring) TryDuplicate(id string) bool {
	r.lock()
	defer r.unlock()
	if _, exists := r.entries[id]; exists {
		return true
	}
	r.rememberInternal(id, nil)
	return false
}

// TryGet 对应 C# TryGetResult（TryGetResult(id, out r)）。
func (r *Ring) TryGet(id string) (*protocol.LatestText, bool) {
	r.lock()
	defer r.unlock()
	latest, ok := r.entries[id]
	return latest, ok
}

// Remember 对应 C# RememberId(id, result)。
func (r *Ring) Remember(id string, latest *protocol.LatestText) {
	r.lock()
	defer r.unlock()
	r.rememberInternal(id, latest)
}

// IsUnchangedDuplicate 对应 C# IsUnchangedDuplicate：返回 true 时 latest 必非 nil。
func (r *Ring) IsUnchangedDuplicate(id, payload, hash string, encrypted bool) (*protocol.LatestText, bool) {
	r.lock()
	defer r.unlock()
	remembered, ok := r.entries[id]
	if !ok {
		return nil, false
	}
	return remembered, remembered != nil &&
		remembered.Payload == payload &&
		remembered.Hash == hash &&
		remembered.Encrypted == encrypted
}

func (r *Ring) rememberInternal(id string, latest *protocol.LatestText) {
	evictedID := r.insertionOrder[r.nextInsertIndex]
	if evictedID != "" && evictedID != id {
		delete(r.entries, evictedID)
	}
	r.insertionOrder[r.nextInsertIndex] = id
	r.nextInsertIndex = (r.nextInsertIndex + 1) % len(r.insertionOrder)
	r.entries[id] = latest
}
