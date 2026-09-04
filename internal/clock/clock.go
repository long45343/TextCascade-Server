// Package clock 提供 C# TimeProvider 的等价接缝（固定决策 F4）：
// 生产用 System，测试注入 fake。
package clock

import "time"

// Clock 是 C# TimeProvider.GetUtcNow 的 1:1 映射。
type Clock interface {
	Now() time.Time
}

type systemClock struct{}

func (systemClock) Now() time.Time { return time.Now().UTC() }

// System 是生产实现。
var System Clock = systemClock{}

// Fake 是测试注入的固定/步进时钟。
type Fake struct {
	Current time.Time
}

func NewFake(start time.Time) *Fake { return &Fake{Current: start.UTC()} }

func (f *Fake) Now() time.Time { return f.Current }

func (f *Fake) Advance(d time.Duration) { f.Current = f.Current.Add(d) }
