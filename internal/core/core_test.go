package core

import (
	"strings"
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"

	"github.com/long45343/TextCascade-Server/internal/config"
	"github.com/long45343/TextCascade-Server/internal/protocol"
)

func newCoreConfig() *config.RuntimeConfig {
	cfg := config.Defaults()
	return &cfg
}

// ---- ClipAndCoreTests ----

func TestNextVersionIncrements(t *testing.T) {
	assert.EqualValues(t, 1, NextVersion(0))
	assert.EqualValues(t, 42, NextVersion(41))
	assert.Panics(t, func() { NextVersion(^uint64(0)) })
}

func TestSeenIdRingDetectsDuplicates(t *testing.T) {
	ring := NewRing(4)
	assert.False(t, ring.TryDuplicate("a"))
	assert.True(t, ring.TryDuplicate("a"))
	assert.False(t, ring.TryDuplicate("b"))
	assert.True(t, ring.TryDuplicate("b"))
}

func TestSeenIdRingEvictsOldEntries(t *testing.T) {
	ring := NewRing(2)
	ring.Remember("a", nil)
	ring.Remember("b", nil)
	assert.False(t, ring.TryDuplicate("c"))
	assert.False(t, ring.TryDuplicate("a"))
}

func TestSeenIdRingRetainsOriginalResultForDuplicateAck(t *testing.T) {
	ring := NewRing(2)
	original := &protocol.LatestText{Payload: "first", Version: 7, Hash: "hash", Encrypted: true, FromClientID: "client", FromClientName: "name", UpdatedAtUtc: time.Now().UTC()}

	ring.Remember("clip-1", original)

	result, ok := ring.TryGet("clip-1")
	require.True(t, ok)
	assert.Equal(t, original, result)
}

func TestSeenIdRingTreatsSameIdWithChangedContentAsNewClip(t *testing.T) {
	ring := NewRing(4)
	original := &protocol.LatestText{Payload: "first", Version: 1, Hash: "hash-1", Encrypted: false, FromClientID: "client", FromClientName: "name", UpdatedAtUtc: time.Now().UTC()}
	ring.Remember("same-id", original)

	_, unchanged := ring.IsUnchangedDuplicate("same-id", "second", "hash-2", false)
	assert.False(t, unchanged)

	latest, unchanged := ring.IsUnchangedDuplicate("same-id", "first", "hash-1", false)
	assert.True(t, unchanged)
	assert.Equal(t, original, latest)
}

func TestTokenBucketRefillsOverTime(t *testing.T) {
	now := time.Unix(1760000000, 0).UTC()
	bucket := NewBucket(2, 2.0, now)
	assert.True(t, bucket.TryAcquire(now))
	assert.True(t, bucket.TryAcquire(now))
	assert.False(t, bucket.TryAcquire(now))
	assert.True(t, bucket.TryAcquire(now.Add(time.Second)))
}

func TestCheckFrameSizeRespectsLimits(t *testing.T) {
	cfg := newCoreConfig()
	assert.True(t, protocol.CheckFrameSize(cfg.Limits.MaxFrameBytes, cfg))
	assert.False(t, protocol.CheckFrameSize(cfg.Limits.MaxFrameBytes+1, cfg))
	assert.False(t, protocol.CheckFrameSize(0, cfg))
}

func TestCheckPayloadSizeRespectsMaxTextBytes(t *testing.T) {
	cfg := newCoreConfig()
	within := strings.Repeat("x", cfg.Limits.MaxTextBytes)
	over := strings.Repeat("x", cfg.Limits.MaxTextBytes+1)
	assert.True(t, protocol.CheckPayloadSize(within, cfg))
	assert.False(t, protocol.CheckPayloadSize(over, cfg))
}

func TestValidateClipMessageRejectsEmptyPayload(t *testing.T) {
	cfg := newCoreConfig()
	clip := &protocol.ClientClip{ID: "id", Payload: "", Encrypted: true, Hash: "hash"}
	assert.False(t, protocol.ValidateClip(clip, cfg))
}

func TestValidateClipMessageRejectsOversizedID(t *testing.T) {
	cfg := newCoreConfig()
	id := strings.Repeat("i", protocol.MaxIdBytes+1)
	clip := &protocol.ClientClip{ID: id, Payload: "payload", Encrypted: true, Hash: "hash"}
	assert.False(t, protocol.ValidateClip(clip, cfg))
}

func TestSelectSnapshotWinnerIgnoresZeroVersion(t *testing.T) {
	ts := time.Unix(1760000000, 0).UTC()
	helloZero := &protocol.ClientHello{ClientID: "a", ClientName: "n", LastServerVersion: 0, Snapshot: &protocol.ClipSnapshot{Payload: "p", Encrypted: true, Hash: "h", LocalModifiedAtUtc: ts}}
	helloOne := &protocol.ClientHello{ClientID: "b", ClientName: "n", LastServerVersion: 1, Snapshot: &protocol.ClipSnapshot{Payload: "p2", Encrypted: true, Hash: "h2", LocalModifiedAtUtc: ts}}
	winner := SelectSnapshotWinner([]*protocol.ClientHello{helloZero, helloOne})
	require.NotNil(t, winner)
	assert.Equal(t, "p2", winner.Snapshot.Payload)
	assert.EqualValues(t, 1, winner.Version)
}

func TestSelectSnapshotWinnerReturnsNullWhenNoPositiveVersion(t *testing.T) {
	ts := time.Unix(1760000000, 0).UTC()
	helloZero := &protocol.ClientHello{ClientID: "a", ClientName: "n", LastServerVersion: 0, Snapshot: &protocol.ClipSnapshot{Payload: "p", Encrypted: true, Hash: "h", LocalModifiedAtUtc: ts}}
	assert.Nil(t, SelectSnapshotWinner([]*protocol.ClientHello{helloZero}))
}

func TestSelectSnapshotWinnerBreaksTiesByTimeThenClientID(t *testing.T) {
	earlier := time.Unix(1760000000, 0).UTC()
	later := time.Unix(1760000010, 0).UTC()
	sameVersion := uint64(5)
	a := &protocol.ClientHello{ClientID: "aaa", ClientName: "n", LastServerVersion: sameVersion, Snapshot: &protocol.ClipSnapshot{Payload: "pa", Encrypted: true, Hash: "h", LocalModifiedAtUtc: earlier}}
	b := &protocol.ClientHello{ClientID: "zzz", ClientName: "n", LastServerVersion: sameVersion, Snapshot: &protocol.ClipSnapshot{Payload: "pb", Encrypted: true, Hash: "h", LocalModifiedAtUtc: later}}
	winner := SelectSnapshotWinner([]*protocol.ClientHello{a, b})
	require.NotNil(t, winner)
	assert.Equal(t, "pb", winner.Snapshot.Payload)

	a2 := &protocol.ClientHello{ClientID: "aaa", ClientName: "n", LastServerVersion: sameVersion, Snapshot: &protocol.ClipSnapshot{Payload: "pa", Encrypted: true, Hash: "h", LocalModifiedAtUtc: later}}
	b2 := &protocol.ClientHello{ClientID: "zzz", ClientName: "n", LastServerVersion: sameVersion, Snapshot: &protocol.ClipSnapshot{Payload: "pb", Encrypted: true, Hash: "h", LocalModifiedAtUtc: later}}
	winner2 := SelectSnapshotWinner([]*protocol.ClientHello{a2, b2})
	require.NotNil(t, winner2)
	assert.Equal(t, "pb", winner2.Snapshot.Payload)
}

func TestSeenIdRingMaintainsFifoEvictionAfterRepeatedIDs(t *testing.T) {
	ring := NewRing(2)
	result1 := &protocol.LatestText{Payload: "p1", Version: 1, Hash: "h1", FromClientID: "c1", FromClientName: "n1", UpdatedAtUtc: time.Now().UTC()}
	result2 := &protocol.LatestText{Payload: "p2", Version: 2, Hash: "h2", FromClientID: "c2", FromClientName: "n2", UpdatedAtUtc: time.Now().UTC()}
	result3 := &protocol.LatestText{Payload: "p3", Version: 3, Hash: "h3", FromClientID: "c3", FromClientName: "n3", UpdatedAtUtc: time.Now().UTC()}

	ring.Remember("a", result1)
	ring.Remember("b", nil)
	ring.Remember("a", result2)
	ring.Remember("c", nil)

	// slot0: "a"(result1→result2)，slot1: "b"→"c"（b 被淘汰）。
	result := NewRing(2) // 防御性：继续用原 ring 推进
	_ = result
	ring.Remember("d", result3) // 覆写 slot0（原 "a"），淘汰 "a"

	_, ok := ring.TryGet("a")
	assert.False(t, ok)
	resD, ok := ring.TryGet("d")
	require.True(t, ok)
	assert.Equal(t, result3, resD)
}

func TestSeenIdRingRepeatedIDKeepsLatestResultBeforeEviction(t *testing.T) {
	ring := NewRing(4)
	old := &protocol.LatestText{Payload: "old", Version: 1, Hash: "h", FromClientID: "c", FromClientName: "n", UpdatedAtUtc: time.Now().UTC()}
	latest := &protocol.LatestText{Payload: "new", Version: 2, Hash: "h", FromClientID: "c", FromClientName: "n", UpdatedAtUtc: time.Now().UTC()}

	ring.Remember("same", old)
	ring.Remember("same", latest)

	actual, ok := ring.TryGet("same")
	require.True(t, ok)
	assert.Equal(t, latest, actual)
}

func TestSeenIdRingEvictsOldestWhenFull(t *testing.T) {
	ring := NewRing(3)
	ring.Remember("a", nil)
	ring.Remember("b", nil)
	ring.Remember("c", nil)
	ring.Remember("d", nil)

	_, ok := ring.TryGet("a")
	assert.False(t, ok)
	for _, id := range []string{"b", "c", "d"} {
		_, ok := ring.TryGet(id)
		assert.True(t, ok, id)
	}
}

func TestSeenIdRingUsesOrdinalComparison(t *testing.T) {
	ring := NewRing(4)
	ring.Remember("A", nil)
	assert.False(t, ring.TryDuplicate("a"))
	assert.True(t, ring.TryDuplicate("A"))
}
