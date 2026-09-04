// Package core：bucket.go（TokenBucket）。
package core

import (
	"math"
	"time"
)

// Bucket 对应 C# TokenBucket：补币算法一致。
type Bucket struct {
	gate            chan struct{}
	tokens          float64
	lastRefill      time.Time
	capacity        int
	tokensPerSecond float64
}

// NewBucket 构造；burst 或 tokensPerSecond 非正 panic（C# ArgumentOutOfRangeException）。
func NewBucket(burst int, tokensPerSecond float64, now time.Time) *Bucket {
	if burst <= 0 {
		panic("burst must be positive")
	}
	if tokensPerSecond <= 0 {
		panic("tokensPerSecond must be positive")
	}
	return &Bucket{
		gate:            make(chan struct{}, 1),
		tokens:          float64(burst),
		lastRefill:      now,
		capacity:        burst,
		tokensPerSecond: tokensPerSecond,
	}
}

// Capacity 容量。
func (b *Bucket) Capacity() int { return b.capacity }

// TokensPerSecond 速率。
func (b *Bucket) TokensPerSecond() float64 { return b.tokensPerSecond }

func (b *Bucket) lock()   { b.gate <- struct{}{} }
func (b *Bucket) unlock() { <-b.gate }

// TryAcquire 对应 C# TryAcquire。
func (b *Bucket) TryAcquire(now time.Time) bool {
	b.lock()
	defer b.unlock()

	if now.Before(b.lastRefill) {
		return false
	}

	b.tokens = math.Min(float64(b.capacity), b.tokens+now.Sub(b.lastRefill).Seconds()*b.tokensPerSecond)
	b.lastRefill = now
	if b.tokens < 1 {
		return false
	}

	b.tokens -= 1
	return true
}
