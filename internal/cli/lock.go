// Package cli：SingleInstanceLock → lock.go。
// gofrs/flock：OpenOrCreate + 独占语义等价 FileShare.None（进程死亡 OS 释放）；
// PID 仅诊断写入；3 次重试后优雅失败。
package cli

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/gofrs/flock"
)

// Handle 对应 C# SingleInstanceLockHandle。
type Handle struct {
	flock *flock.Flock
	path  string
}

// Release 对应 C# Dispose：释放锁并尽力删除锁文件（IO 失败忽略）。
func (h *Handle) Release() {
	if h == nil {
		return
	}
	_ = h.flock.Unlock()
	_ = os.Remove(h.path)
}

// Acquire 对应 C# SingleInstanceLock.Acquire：3 次重试，全部失败返回 nil（非错误）。
func Acquire(lockPath string, pollDelay time.Duration) (*Handle, error) {
	if strings.TrimSpace(lockPath) == "" {
		return nil, errors.New("Lock path must not be empty.")
	}

	directory := filepath.Dir(lockPath)
	// C# GetDirectoryName 对纯文件名返回空串；Go filepath.Dir 返回 "."，
	// 故以"不含路径分隔符"判定等价形态。
	if strings.TrimSpace(directory) == "" || !strings.ContainsAny(lockPath, "/\\") {
		return nil, errors.New("Lock path must include a directory.")
	}

	if info, err := os.Stat(directory); err != nil || !info.IsDir() {
		return nil, fmt.Errorf("Directory '%s' does not exist.", directory)
	}

	if pollDelay <= 0 {
		pollDelay = 100 * time.Millisecond
	}

	for attempt := 0; attempt < 3; attempt++ {
		fl := flock.New(lockPath)
		locked, err := fl.TryLock()
		if err != nil {
			// 权限/路径类错误向上传播（等价 C# 非 IOException 不被吞）。
			if isRetryableIOError(err) {
				time.Sleep(pollDelay)
				continue
			}
			return nil, err
		}
		if locked {
			// PID 仅诊断写入；Windows 独占锁下二次打开会失败，尽力而为即可
			//（等价 C# 通过同一句柄写入的诊断行为，锁语义不受影响）。
			_ = os.WriteFile(lockPath, []byte(strconv.Itoa(os.Getpid())), 0o644)
			return &Handle{flock: fl, path: lockPath}, nil
		}
		time.Sleep(pollDelay)
	}

	return nil, nil
}

// isRetryableIOError 判断是否为文件被占用类错误（等价 C# IOException 捕获后重试）。
func isRetryableIOError(err error) bool {
	var pathErr *os.PathError
	if errors.As(err, &pathErr) {
		// Windows 共享冲突 / Unix EWOULDBLOCK 等均以 PathError 形式出现。
		return true
	}
	return false
}
