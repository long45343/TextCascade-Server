// Package hub：registry.go（UserRegistry：ConcurrentDictionary → mutex map）。
package hub

import "time"

// nowUTC 等价 C# DateTimeOffset.UtcNow（UserRegistry 内部使用）。
func nowUTC() time.Time { return time.Now().UTC() }

// Registry 对应 C# UserRegistry。
type Registry struct {
	gate chan struct{}
	hubs map[string]*Hub
}

// NewRegistry 构造。
func NewRegistry() *Registry {
	return &Registry{gate: make(chan struct{}, 1), hubs: make(map[string]*Hub)}
}

func (r *Registry) lock()   { r.gate <- struct{}{} }
func (r *Registry) unlock() { <-r.gate }

// GetOrAdd 对应 C# GetOrAdd（互斥防并发建 hub）。
func (r *Registry) GetOrAdd(username string, factory func(string) *Hub) *Hub {
	r.lock()
	defer r.unlock()
	if hub, ok := r.hubs[username]; ok {
		return hub
	}
	hub := factory(username)
	r.hubs[username] = hub
	return hub
}

// TryGet 对应 C# TryGetValue。
func (r *Registry) TryGet(username string) (*Hub, bool) {
	r.lock()
	defer r.unlock()
	hub, ok := r.hubs[username]
	return hub, ok
}

// RemoveIfEmpty 对应 C# RemoveIfEmpty。
func (r *Registry) RemoveIfEmpty(h *Hub, allowDuringRecovery bool) {
	if !h.IsEmpty() {
		return
	}
	if !allowDuringRecovery && h.IsRecoveryWindowOpen(nowUTC()) {
		return
	}
	r.lock()
	defer r.unlock()
	if r.hubs[h.username] == h {
		delete(r.hubs, h.username)
	}
}

// Remove 对应 C# Remove（仅当映射仍指向该 hub 时移除）。
func (r *Registry) Remove(h *Hub) bool {
	r.lock()
	defer r.unlock()
	if r.hubs[h.username] == h {
		delete(r.hubs, h.username)
		return true
	}
	return false
}

// All 返回全部 hub（无序，等价 ConcurrentDictionary 遍历）。
func (r *Registry) All() []*Hub {
	r.lock()
	defer r.unlock()
	all := make([]*Hub, 0, len(r.hubs))
	for _, hub := range r.hubs {
		all = append(all, hub)
	}
	return all
}
