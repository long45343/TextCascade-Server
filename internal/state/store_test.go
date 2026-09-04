package state

import (
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

func tempStateFile(t *testing.T) string {
	t.Helper()
	return filepath.Join(t.TempDir(), "state.json")
}

// ---- RuntimeStateAndProtocolTests（state 部分）----

func TestStateStorePersistsHighestVersionAtomically(t *testing.T) {
	path := tempStateFile(t)

	first, err := NewStore(path, 0, nil)
	require.NoError(t, err)
	first.SaveVersion("alice", 7)
	first.Flush()
	first.Stop()

	second, err := NewStore(path, 0, nil)
	require.NoError(t, err)
	second.SaveVersion("alice", 5)
	second.Flush()
	assert.EqualValues(t, 7, second.GetVersion("alice"))
	second.Stop()

	third, err := NewStore(path, 0, nil)
	require.NoError(t, err)
	assert.EqualValues(t, 7, third.GetVersion("alice"))
	third.Stop()
}

func TestStateStoreFlushesPeriodicallyInBackground(t *testing.T) {
	path := tempStateFile(t)

	store, err := NewStore(path, 50*time.Millisecond, nil)
	require.NoError(t, err)
	store.SaveVersion("alice", 12)
	assert.EqualValues(t, 12, store.GetVersion("alice"))

	// 等待后台 tick
	deadline := time.Now().Add(2 * time.Second)
	for {
		raw, err := os.ReadFile(path)
		if err == nil && strings.Contains(string(raw), "alice") {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("background flush did not happen")
		}
		time.Sleep(10 * time.Millisecond)
	}

	reloaded, err := NewStore(path, 0, nil)
	require.NoError(t, err)
	assert.EqualValues(t, 12, reloaded.GetVersion("alice"))
	reloaded.Stop()
	store.Stop()
}

func TestStateStoreConcurrentSaveVersionMaintainsHighestValue(t *testing.T) {
	path := tempStateFile(t)

	store, err := NewStore(path, 0, nil)
	require.NoError(t, err)

	var wg sync.WaitGroup
	for i := 1; i < 100; i++ {
		i := i
		wg.Add(1)
		go func() {
			defer wg.Done()
			store.SaveVersion("alice", uint64(i))
			store.SaveVersion("bob", uint64(100-i))
		}()
	}
	wg.Wait()

	assert.EqualValues(t, 99, store.GetVersion("alice"))
	assert.EqualValues(t, 99, store.GetVersion("bob"))

	store.Flush()
	store.Stop()

	reloaded, err := NewStore(path, 0, nil)
	require.NoError(t, err)
	assert.EqualValues(t, 99, reloaded.GetVersion("alice"))
	assert.EqualValues(t, 99, reloaded.GetVersion("bob"))
	reloaded.Stop()
}

func TestStateStoreRejectsInvalidFile(t *testing.T) {
	path := tempStateFile(t)
	require.NoError(t, os.WriteFile(path, []byte(`{"entries":[{"username":"alice","version":0}]}`), 0o644))
	_, err := NewStore(path, 0, nil)
	assert.Error(t, err)
}
