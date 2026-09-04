package core

import (
	"testing"
	"time"

	"github.com/stretchr/testify/assert"

	"github.com/long45343/TextCascade-Server/internal/protocol"
)

// U11
func TestWithVersionProducesNewImmutableRecord(t *testing.T) {
	original := &protocol.LatestText{
		Payload: "payload", Version: 7, Hash: "hash", Encrypted: true,
		FromClientID: "client", FromClientName: "name",
		UpdatedAtUtc: time.Date(2026, 8, 18, 8, 0, 0, 0, time.UTC),
	}

	updated := WithVersion(original, 8, time.Time{})
	assert.EqualValues(t, 8, updated.Version)
	assert.Equal(t, original.Payload, updated.Payload)
	assert.Equal(t, original.Hash, updated.Hash)
	assert.Equal(t, original.Encrypted, updated.Encrypted)
	assert.Equal(t, original.FromClientID, updated.FromClientID)
	assert.Equal(t, original.FromClientName, updated.FromClientName)
	assert.Equal(t, original.UpdatedAtUtc, updated.UpdatedAtUtc)
	assert.EqualValues(t, 7, original.Version)
	assert.NotSame(t, original, updated)

	withTime := WithVersion(original, 9, time.Date(2026, 8, 18, 9, 0, 0, 0, time.UTC))
	assert.EqualValues(t, 9, withTime.Version)
	assert.Equal(t, time.Date(2026, 8, 18, 9, 0, 0, 0, time.UTC), withTime.UpdatedAtUtc)
}
