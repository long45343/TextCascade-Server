package hosting

import (
	"os"
	"path/filepath"
	"runtime"
	"testing"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

// 启动自检：目录存在且可写时放行，并清理探测临时文件。
func TestEnsureWritableDirAcceptsWritableDirectory(t *testing.T) {
	directory := t.TempDir()
	target := filepath.Join(directory, "users.json")

	require.NoError(t, ensureWritableDir("files.users_file", target))

	entries, err := os.ReadDir(directory)
	require.NoError(t, err)
	assert.Empty(t, entries, "probe file must be removed")
}

// 目录不存在时必须在启动阶段失败，而不是等到首次写盘时静默降级。
func TestEnsureWritableDirRejectsMissingDirectory(t *testing.T) {
	target := filepath.Join(t.TempDir(), "absent", "users.json")

	err := ensureWritableDir("files.users_file", target)
	require.Error(t, err)
	assert.Contains(t, err.Error(), "does not exist")
	assert.Contains(t, err.Error(), "files.users_file")
}

// 只读目录同样必须 fail-fast。
func TestEnsureWritableDirRejectsReadOnlyDirectory(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("Windows 不提供 Unix 权限位语义")
	}
	if os.Geteuid() == 0 {
		t.Skip("root 可绕过权限位，跳过")
	}

	directory := filepath.Join(t.TempDir(), "readonly")
	require.NoError(t, os.Mkdir(directory, 0o555))
	t.Cleanup(func() { _ = os.Chmod(directory, 0o755) })

	err := ensureWritableDir("files.state_file", filepath.Join(directory, "state.json"))
	require.Error(t, err)
	assert.Contains(t, err.Error(), "not writable")
}
