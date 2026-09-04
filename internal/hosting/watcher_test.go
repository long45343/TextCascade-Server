package hosting_test

import (
	"bytes"
	"log/slog"
	"os"
	"path/filepath"
	"sync"
	"testing"
	"time"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"

	"github.com/long45343/TextCascade-Server/internal/auth"
	"github.com/long45343/TextCascade-Server/internal/clock"
	"github.com/long45343/TextCascade-Server/internal/config"
	"github.com/long45343/TextCascade-Server/internal/hosting"
	"github.com/long45343/TextCascade-Server/internal/state"
	syncserver "github.com/long45343/TextCascade-Server/internal/sync"
	"github.com/long45343/TextCascade-Server/internal/users"
)

const watcherValidHash = "$argon2id$v=19$m=19456,t=2,p=1$c2FsdA$aG9zdA"

// fastHasher 以极小参数执行真实 Argon2id（等价 C# 测试注入 FastHasher）。
type fastHasher struct{}

func (fastHasher) Hash(password string, params auth.Params) string {
	return auth.NewArgon2Hasher().Hash(password, auth.Params{MemoryKiB: 64, Iterations: 1, Parallelism: 1})
}
func (fastHasher) Verify(password, encodedHash string) bool {
	return auth.NewArgon2Hasher().Verify(password, encodedHash)
}
func (fastHasher) NeedsRehash(encodedHash string, params auth.Params) bool { return false }

var _ auth.PasswordHasher = fastHasher{}

func newWatcherServer(t *testing.T, dir string, initial *users.UsersFile) (*syncserver.Server, *config.RuntimeConfig) {
	t.Helper()
	tempUsers := filepath.Join(dir, "users.json")
	tempState := filepath.Join(dir, "state.json")
	if initial != nil {
		require.NoError(t, users.Save(tempUsers, initial))
	}
	cfg := config.Defaults()
	cfg.TokenSecret = make([]byte, 32)
	cfg.Files = config.FilesConfig{UsersFile: tempUsers, StateFile: tempState}
	store, err := state.NewStore(tempState, 0, nil)
	require.NoError(t, err)
	t.Cleanup(store.Stop)
	logger := slog.New(slog.NewTextHandler(&bytes.Buffer{}, nil))
	srv := syncserver.New(&cfg, initial, store, fastHasher{}, clock.System, logger)
	return srv, &cfg
}

func waitUntil(t *testing.T, timeout time.Duration, condition func() bool) {
	t.Helper()
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		if condition() {
			return
		}
		time.Sleep(20 * time.Millisecond)
	}
	assert.True(t, condition(), "Condition was not met within timeout.")
}

func TestReloadReplacesUserLookupAfterSave(t *testing.T) {
	dir := t.TempDir()
	initial := &users.UsersFile{NextTokenVersion: 2, Users: []users.UserRecord{{Username: "alice", PasswordHash: watcherValidHash, TokenVersion: 1}}}
	srv, cfg := newWatcherServer(t, dir, initial)

	watcher := hosting.NewWatcher(cfg.Files.UsersFile, srv, slog.New(slog.NewTextHandler(&bytes.Buffer{}, nil)))
	watcher.Start()
	defer watcher.Close()

	assert.True(t, srv.UserLookup()["alice"].Username == "alice")
	_, hasBob := srv.UserLookup()["bob"]
	assert.False(t, hasBob)

	// 添加 bob 并保存。
	updated := &users.UsersFile{NextTokenVersion: 3, Users: []users.UserRecord{
		{Username: "alice", PasswordHash: watcherValidHash, TokenVersion: 1},
		{Username: "bob", PasswordHash: watcherValidHash, TokenVersion: 2},
	}}
	require.NoError(t, users.Save(cfg.Files.UsersFile, updated))

	waitUntil(t, 3*time.Second, func() bool {
		_, ok := srv.UserLookup()["bob"]
		return ok
	})
	assert.True(t, srv.UserLookup()["alice"].Username == "alice")
}

func TestInvalidReloadRetainsPreviousLookup(t *testing.T) {
	dir := t.TempDir()
	initial := &users.UsersFile{NextTokenVersion: 2, Users: []users.UserRecord{{Username: "alice", PasswordHash: watcherValidHash, TokenVersion: 1}}}
	srv, cfg := newWatcherServer(t, dir, initial)

	watcher := hosting.NewWatcher(cfg.Files.UsersFile, srv, slog.New(slog.NewTextHandler(&bytes.Buffer{}, nil)))
	watcher.Start()
	defer watcher.Close()

	// 写入非法 JSON。
	require.NoError(t, os.WriteFile(cfg.Files.UsersFile, []byte("invalid json content!@#$"), 0o644))
	time.Sleep(200 * time.Millisecond)

	// 旧表仍保留 alice。
	assert.True(t, srv.UserLookup()["alice"].Username == "alice")

	// 用合法文件（charlie）恢复。
	recovered := &users.UsersFile{NextTokenVersion: 4, Users: []users.UserRecord{{Username: "charlie", PasswordHash: watcherValidHash, TokenVersion: 3}}}
	require.NoError(t, users.Save(cfg.Files.UsersFile, recovered))

	waitUntil(t, 3*time.Second, func() bool {
		_, ok := srv.UserLookup()["charlie"]
		return ok
	})
}

func TestConcurrentReloadObserversAlwaysSeeCompleteDictionary(t *testing.T) {
	dir := t.TempDir()
	usersA := &users.UsersFile{NextTokenVersion: 3, Users: []users.UserRecord{
		{Username: "alice", PasswordHash: watcherValidHash, TokenVersion: 1},
		{Username: "bob", PasswordHash: watcherValidHash, TokenVersion: 2},
	}}
	srv, _ := newWatcherServer(t, dir, usersA)
	usersB := &users.UsersFile{NextTokenVersion: 5, Users: []users.UserRecord{
		{Username: "charlie", PasswordHash: watcherValidHash, TokenVersion: 3},
		{Username: "david", PasswordHash: watcherValidHash, TokenVersion: 4},
	}}

	stop := make(chan struct{})
	var wg sync.WaitGroup
	wg.Add(2)

	// 读取方：任意时刻必须看到完整的 A 或 B。
	go func() {
		defer wg.Done()
		for {
			select {
			case <-stop:
				return
			default:
			}
			lookup := srv.UserLookup()
			isA := len(lookup) == 2 && lookup["alice"].Username == "alice" && lookup["bob"].Username == "bob"
			isB := len(lookup) == 2 && lookup["charlie"].Username == "charlie" && lookup["david"].Username == "david"
			assert.True(t, isA || isB, "Observed incomplete or mixed dictionary.")
		}
	}()

	// 写入方：交替替换。
	go func() {
		defer wg.Done()
		toggle := false
		for {
			select {
			case <-stop:
				return
			default:
			}
			if toggle {
				srv.ReplaceUserLookup(usersA)
			} else {
				srv.ReplaceUserLookup(usersB)
			}
			toggle = !toggle
		}
	}()

	time.Sleep(1 * time.Second)
	close(stop)
	wg.Wait()
}

func TestWatcherDisposeIsIdempotent(t *testing.T) {
	dir := t.TempDir()
	srv, cfg := newWatcherServer(t, dir, &users.UsersFile{NextTokenVersion: 1})

	watcher := hosting.NewWatcher(cfg.Files.UsersFile, srv, slog.New(slog.NewTextHandler(&bytes.Buffer{}, nil)))
	watcher.Start()
	watcher.Close()
	watcher.Close() // 不应 panic
}
