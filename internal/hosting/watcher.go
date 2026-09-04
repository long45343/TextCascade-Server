// Package hosting：UserFileWatcher.cs → watcher.go。
// fsnotify Changed/Created/Deleted/Renamed + 250ms 防抖 + 30s ticker 兜底无条件重载；
// 3 次 50ms 退避；全败保留旧表 + warning；成功 → srv.ReplaceUserLookup。
package hosting

import (
	"log/slog"
	"os"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/fsnotify/fsnotify"

	syncserver "github.com/long45343/TextCascade-Server/internal/sync"
	"github.com/long45343/TextCascade-Server/internal/users"
)

// Watcher 对应 C# UserFileWatcher。
type Watcher struct {
	usersPath    string
	server       *syncserver.Server
	logger       *slog.Logger
	debounce     time.Duration
	pollFallback time.Duration

	mu            sync.Mutex
	reloadPending bool
	disposed      bool

	reloadQueued atomic.Int32

	fsWatcher *fsnotify.Watcher
	stopCh    chan struct{}
	stopDone  chan struct{}
	started   bool
}

// NewWatcher 对应 C# 构造器（默认防抖 250ms / 兜底 30s）。
func NewWatcher(usersPath string, server *syncserver.Server, logger *slog.Logger) *Watcher {
	absolute, err := filepath.Abs(usersPath)
	if err != nil {
		absolute = usersPath
	}
	return &Watcher{
		usersPath:    absolute,
		server:       server,
		logger:       logger,
		debounce:     250 * time.Millisecond,
		pollFallback: 30 * time.Second,
	}
}

// Start 对应 C# Start。
func (w *Watcher) Start() {
	w.mu.Lock()
	if w.started || w.disposed {
		w.mu.Unlock()
		return
	}
	w.started = true
	w.mu.Unlock()

	directory := filepath.Dir(w.usersPath)
	if info, err := os.Stat(directory); err != nil || !info.IsDir() {
		_ = os.MkdirAll(directory, 0o755)
	}

	watcher, err := fsnotify.NewWatcher()
	if err != nil {
		w.logger.Warn("Users file watcher error encountered.", "error", err.Error())
		return
	}
	if err := watcher.Add(directory); err != nil {
		w.logger.Warn("Users file watcher error encountered.", "error", err.Error())
		_ = watcher.Close()
		return
	}
	w.fsWatcher = watcher

	w.stopCh = make(chan struct{})
	w.stopDone = make(chan struct{})
	go w.eventLoop(watcher)
	go w.pollLoop()
}

func (w *Watcher) eventLoop(watcher *fsnotify.Watcher) {
	defer close(w.stopDone)
	for {
		select {
		case <-w.stopCh:
			return
		case event, ok := <-watcher.Events:
			if !ok {
				return
			}
			// Rename 事件的 Name 为旧路径（等价 C# RenamedEventArgs.OldFullPath）；
			// 改名为 usersPath 的新路径由 Created 事件覆盖。
			if w.matchesUsersPath(event.Name) {
				w.scheduleReload()
			}
		case err, ok := <-watcher.Errors:
			if !ok {
				return
			}
			w.logger.Warn("Users file watcher error encountered.", "error", err.Error())
		}
	}
}

func (w *Watcher) matchesUsersPath(path string) bool {
	if runtime.GOOS == "windows" {
		return strings.EqualFold(filepath.Clean(path), w.usersPath)
	}
	return filepath.Clean(path) == w.usersPath
}

func (w *Watcher) pollLoop() {
	ticker := time.NewTicker(w.pollFallback)
	defer ticker.Stop()
	for {
		select {
		case <-w.stopCh:
			return
		case <-ticker.C:
			w.scheduleReload()
		}
	}
}

// scheduleReload 复刻 C# ScheduleReload：已有防抖等待时合并（不重置）；
// 防抖结束后 reloadQueued CAS 保证至多一个 reload 并发执行。
func (w *Watcher) scheduleReload() {
	w.mu.Lock()
	if w.disposed {
		w.mu.Unlock()
		return
	}
	if w.reloadPending {
		w.mu.Unlock()
		return
	}
	w.reloadPending = true
	w.mu.Unlock()

	time.AfterFunc(w.debounce, func() {
		w.mu.Lock()
		w.reloadPending = false
		disposed := w.disposed
		w.mu.Unlock()
		if disposed {
			return
		}

		if w.reloadQueued.CompareAndSwap(0, 1) {
			defer w.reloadQueued.Store(0)
			w.reload()
		}
	})
}

// reload 对应 C# ReloadAsync：3 次 50ms 退避；全败保留旧表 + warning。
func (w *Watcher) reload() {
	var loaded *users.UsersFile
	var lastError error

	for attempt := 0; attempt < 3; attempt++ {
		usersFile, err := users.Load(w.usersPath)
		if err == nil {
			loaded = usersFile
			lastError = nil
			break
		}
		lastError = err
		time.Sleep(time.Duration(50*(attempt+1)) * time.Millisecond)
	}

	if loaded != nil {
		w.server.ReplaceUserLookup(loaded)
		w.logger.Info("Users file reloaded. users=" + strconv.Itoa(len(loaded.Users)))
	} else if lastError != nil {
		w.logger.Warn("Users file reload failed; retaining previous users. path=" + w.usersPath + " error=" + lastError.Error())
	}
}

// Close 对应 C# Dispose。
func (w *Watcher) Close() {
	w.mu.Lock()
	if w.disposed {
		w.mu.Unlock()
		return
	}
	w.disposed = true
	stopCh, stopDone := w.stopCh, w.stopDone
	w.mu.Unlock()

	if stopCh != nil {
		close(stopCh)
		<-stopDone
	}
	if w.fsWatcher != nil {
		_ = w.fsWatcher.Close()
	}
}
