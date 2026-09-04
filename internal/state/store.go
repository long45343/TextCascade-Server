// Package state 是 RuntimeStateStore.cs 的迁移：
// {"entries":[{"username":...,"version":...}]} 格式不变；5s 刷盘循环；脏位快照。
package state

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"log/slog"
	"os"
	"sort"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// Store 对应 C# RuntimeStateStore。
type Store struct {
	path      string
	logger    *slog.Logger
	gate      sync.Mutex
	versions  map[string]uint64
	isDirty   atomic.Int32
	writeGate sync.Mutex
	stopCh    chan struct{}
	loopDone  chan struct{}
	stopped   bool
}

// NewStore 构造并启动 flush 循环（flushInterval ≤ 0 时不启动，供测试）。
func NewStore(path string, flushInterval time.Duration, logger *slog.Logger) (*Store, error) {
	versions, err := load(path)
	if err != nil {
		return nil, err
	}
	s := &Store{
		path:     path,
		logger:   logger,
		versions: versions,
	}
	if flushInterval > 0 {
		s.stopCh = make(chan struct{})
		s.loopDone = make(chan struct{})
		go s.runFlushLoop(flushInterval)
	}
	return s, nil
}

// GetVersion 对应 C# GetVersion。
func (s *Store) GetVersion(username string) uint64 {
	s.gate.Lock()
	defer s.gate.Unlock()
	return s.versions[username]
}

// SaveVersion 对应 C# SaveVersion：单调 max 合并，防乱序回退。
func (s *Store) SaveVersion(username string, version uint64) {
	s.gate.Lock()
	if version > s.versions[username] {
		s.versions[username] = version
	}
	s.gate.Unlock()
	s.isDirty.Store(1)
}

// Flush 对应 C# Flush：脏位交换后原子写盘；IO 失败回滚脏位并告警。
func (s *Store) Flush() bool {
	if s.isDirty.Swap(0) == 0 {
		return false
	}

	s.writeGate.Lock()
	defer s.writeGate.Unlock()

	s.gate.Lock()
	entries := make([]stateEntry, 0, len(s.versions))
	for username, version := range s.versions {
		entries = append(entries, stateEntry{Username: username, Version: version})
	}
	s.gate.Unlock()
	sort.Slice(entries, func(i, j int) bool { return entries[i].Username < entries[j].Username })

	if err := writeAtomic(s.path, entries); err != nil {
		s.isDirty.Store(1)
		if s.logger != nil {
			s.logger.Warn("Failed to write runtime state file; will retry in next flush cycle. path="+s.path, "error", err.Error())
		}
		return false
	}
	return true
}

func (s *Store) runFlushLoop(interval time.Duration) {
	defer close(s.loopDone)
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for {
		select {
		case <-s.stopCh:
			return
		case <-ticker.C:
			s.Flush()
		}
	}
}

// Stop 对应 C# Dispose：停机同步 flush。
func (s *Store) Stop() {
	s.gate.Lock()
	if s.stopped {
		s.gate.Unlock()
		return
	}
	s.stopped = true
	stopCh, loopDone := s.stopCh, s.loopDone
	s.gate.Unlock()

	if stopCh != nil {
		close(stopCh)
		<-loopDone
	}
	s.Flush()
}

type stateEntry struct {
	Username string `json:"username"`
	Version  uint64 `json:"version"`
}

type stateFile struct {
	Entries []stateEntry `json:"entries"`
}

// load 对应 C# Load：结构非法（重复键/空 username/零版本）启动 fail-fast。
func load(path string) (map[string]uint64, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return map[string]uint64{}, nil
		}
		return nil, err
	}

	var state stateFile
	if err := json.Unmarshal(raw, &state); err != nil {
		return nil, fmt.Errorf("Invalid runtime state file '%s': %v", path, err)
	}

	result := make(map[string]uint64, len(state.Entries))
	for _, entry := range state.Entries {
		if strings.TrimSpace(entry.Username) == "" || entry.Version == 0 {
			return nil, fmt.Errorf("Invalid runtime state file '%s': State file contains duplicate, empty, or zero versions.", path)
		}
		if _, dup := result[entry.Username]; dup {
			return nil, fmt.Errorf("Invalid runtime state file '%s': State file contains duplicate, empty, or zero versions.", path)
		}
		result[entry.Username] = entry.Version
	}
	return result, nil
}

// writeAtomic 对应 C# WriteAtomic：临时文件 + rename + fsync。
func writeAtomic(path string, entries []stateEntry) error {
	var b strings.Builder
	b.WriteString("{\n  \"entries\": ")
	if len(entries) == 0 {
		b.WriteString("[]\n}")
	} else {
		b.WriteString("[\n")
		for i, entry := range entries {
			b.WriteString("    {\"username\": ")
			usernameJSON, err := json.Marshal(entry.Username)
			if err != nil {
				return err
			}
			b.Write(usernameJSON)
			b.WriteString(", \"version\": ")
			b.WriteString(strconv.FormatUint(entry.Version, 10))
			b.WriteString("}")
			if i < len(entries)-1 {
				b.WriteString(",")
			}
			b.WriteString("\n")
		}
		b.WriteString("  ]\n}")
	}

	suffix := make([]byte, 16)
	if _, err := rand.Read(suffix); err != nil {
		return err
	}
	temporary := path + "." + hex.EncodeToString(suffix) + ".tmp"

	file, err := os.OpenFile(temporary, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o644)
	if err != nil {
		return err
	}
	if _, err := file.WriteString(b.String()); err != nil {
		file.Close()
		os.Remove(temporary)
		return err
	}
	if err := file.Sync(); err != nil {
		file.Close()
		os.Remove(temporary)
		return err
	}
	if err := file.Close(); err != nil {
		os.Remove(temporary)
		return err
	}

	if err := os.Rename(temporary, path); err != nil {
		os.Remove(temporary)
		return err
	}
	return nil
}
