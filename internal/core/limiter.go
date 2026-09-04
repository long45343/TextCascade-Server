// Package core 是 Core.cs 的迁移：limiter.go（SlidingWindowLoginLimiter）。
package core

import (
	"time"

	"github.com/long45343/TextCascade-Server/internal/config"
)

// Limiter 对应 C# SlidingWindowLoginLimiter：双维度滑动窗口，任一超限拒绝。
type Limiter struct {
	gate    chan struct{}
	windows map[string]*window
}

type window struct {
	times []time.Time
}

// NewLimiter 构造。
func NewLimiter() *Limiter {
	return &Limiter{gate: make(chan struct{}, 1), windows: make(map[string]*window)}
}

func (l *Limiter) lock()   { l.gate <- struct{}{} }
func (l *Limiter) unlock() { <-l.gate }

// TryConsumeLoginLimit：IP 与用户名双窗口，任一超限拒绝；成功仅清用户窗口。
func (l *Limiter) TryConsumeLoginLimit(ip, username string, now time.Time, cfg *config.RuntimeConfig) bool {
	l.lock()
	defer l.unlock()

	l.removeExpiredLocked(now.Add(-time.Minute))
	ipAllowed := l.tryConsume("ip:"+ip, cfg.RateLimit.LoginIpPerMinute, now, cfg.RateLimit.MaxKeys, true)
	if !ipAllowed {
		return false
	}

	userAllowed := l.tryConsume("user:"+username, cfg.RateLimit.LoginUserPerMinute, now, cfg.RateLimit.MaxKeys, true)
	return ipAllowed && userAllowed
}

// ResetUserWindow 对应 C# ResetUserLoginLimit。
func (l *Limiter) ResetUserWindow(username string) {
	l.lock()
	defer l.unlock()
	delete(l.windows, "user:"+username)
}

// GetWindowCount 供同包测试直调（C# internal ForTest 钩子）。
func (l *Limiter) GetWindowCount(key string) int {
	l.lock()
	defer l.unlock()
	if w, ok := l.windows[key]; ok {
		return len(w.times)
	}
	return 0
}

// HasWindowKey 供同包测试直调。
func (l *Limiter) HasWindowKey(key string) bool {
	l.lock()
	defer l.unlock()
	_, ok := l.windows[key]
	return ok
}

// RemoveExpiredForTest 供同包测试直调。
func (l *Limiter) RemoveExpiredForTest(cutoff time.Time) {
	l.lock()
	defer l.unlock()
	l.removeExpiredLocked(cutoff)
}

// EnqueueForTest 供同包测试直调。
func (l *Limiter) EnqueueForTest(key string, timestamp time.Time) {
	l.lock()
	defer l.unlock()
	w, ok := l.windows[key]
	if !ok {
		w = &window{}
		l.windows[key] = w
	}
	w.times = append(w.times, timestamp)
}

func (l *Limiter) tryConsume(key string, limit int, now time.Time, maxKeys int, allowNewKey bool) bool {
	cutoff := now.Add(-time.Minute)
	w, ok := l.windows[key]
	if !ok {
		if !allowNewKey || len(l.windows) >= maxKeys {
			l.removeExpiredLocked(cutoff)
			if len(l.windows) >= maxKeys {
				return false
			}
		}
		w = &window{}
		l.windows[key] = w
	}

	for len(w.times) > 0 && !w.times[0].After(cutoff) {
		w.times = w.times[1:]
	}

	if len(w.times) >= limit {
		return false
	}

	w.times = append(w.times, now)
	return true
}

func (l *Limiter) removeExpiredLocked(cutoff time.Time) {
	var emptyKeys []string
	for key, w := range l.windows {
		for len(w.times) > 0 && !w.times[0].After(cutoff) {
			w.times = w.times[1:]
		}
		if len(w.times) == 0 {
			emptyKeys = append(emptyKeys, key)
		}
	}
	for _, key := range emptyKeys {
		delete(l.windows, key)
	}
}
