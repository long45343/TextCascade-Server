package models

import (
	"testing"

	"github.com/stretchr/testify/assert"

	"github.com/long45343/TextCascade-Server/internal/config"
)

func newStateBag() *StateBag {
	cfg := config.Defaults()
	return NewStateBag(&cfg)
}

// ---- ConnectionStateTests ----

func TestUnsolicitedPongIsRejectedUntilPingIsAwaited(t *testing.T) {
	state := newStateBag()
	assert.False(t, state.TryTakePongAwaiting())
}

func TestExpectedPongIsAcceptedOnceAndThenRejectedAgain(t *testing.T) {
	state := newStateBag()

	state.MarkPingAwaitingPong()
	assert.True(t, state.TryTakePongAwaiting())
	assert.False(t, state.TryTakePongAwaiting())
}

// MarkClosed CAS 守卫。
func TestMarkClosedIsIdempotent(t *testing.T) {
	state := newStateBag()
	assert.True(t, state.MarkClosed())
	assert.False(t, state.MarkClosed())
	assert.True(t, state.IsClosed())
}

// TryStartHelloTimeout。
func TestTryStartHelloTimeout(t *testing.T) {
	state := newStateBag()
	assert.True(t, state.TryStartHelloTimeout())
	assert.False(t, state.TryStartHelloTimeout())

	closed := newStateBag()
	closed.MarkClosed()
	assert.False(t, closed.TryStartHelloTimeout())
}

// TryEnqueueSend：满即 false。
func TestTryEnqueueSendFull(t *testing.T) {
	cfg := config.Defaults()
	cfg.Limits.SendQueueCapacity = 1
	state := NewStateBag(&cfg)
	assert.True(t, state.TryEnqueueSend([]byte("a")))
	assert.False(t, state.TryEnqueueSend([]byte("b")))
}
