package cli

import (
	"encoding/base64"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"

	"github.com/long45343/TextCascade-Server/internal/auth"
	"github.com/long45343/TextCascade-Server/internal/users"
)

const cliValidHash = "$argon2id$v=19$m=19456,t=2,p=1$c2FsdA$aG9zdA"

// staticPasswordHasher 对应 C# StaticPasswordHasher。
type staticPasswordHasher struct{}

func (staticPasswordHasher) Hash(password string, params auth.Params) string {
	prefix := []byte(password)
	if len(prefix) > 4 {
		prefix = prefix[:4]
	}
	return "$argon2id$v=19$m=19456,t=2,p=1$" +
		base64.StdEncoding.EncodeToString(prefix) + "$" +
		base64.StdEncoding.EncodeToString([]byte("hashbytes"))
}

func (h staticPasswordHasher) Verify(password, encodedHash string) bool {
	return encodedHash == h.Hash(password, auth.Params{})
}

func (staticPasswordHasher) NeedsRehash(encodedHash string, params auth.Params) bool { return false }

var _ auth.PasswordHasher = staticPasswordHasher{}

// withStdin 以管道替换 os.Stdin 运行 CLI（对应 C# Console.SetIn）。
func withStdin(t *testing.T, stdinLine string, args []string) int {
	t.Helper()
	r, w, err := os.Pipe()
	require.NoError(t, err)
	if stdinLine != "" {
		_, err := w.WriteString(stdinLine + "\n")
		require.NoError(t, err)
	}
	_ = w.Close()

	original := os.Stdin
	os.Stdin = r
	t.Cleanup(func() {
		os.Stdin = original
		_ = r.Close()
	})
	return Run(args, staticPasswordHasher{})
}

func writeUsersFile(t *testing.T, path, json string) {
	t.Helper()
	require.NoError(t, os.WriteFile(path, []byte(json), 0o644))
}

// configFor 写一个最小 TOML 固定 users_file / state_file，返回其路径。
func configFor(t *testing.T, usersPath string) string {
	t.Helper()
	statePath := filepath.Join(filepath.Dir(usersPath), "state.json")
	tomlPath := filepath.Join(filepath.Dir(usersPath), "textcascade.toml")
	content := fmt.Sprintf("[files]\nusers_file = %q\nstate_file = %q\n", usersPath, statePath)
	require.NoError(t, os.WriteFile(tomlPath, []byte(content), 0o644))
	return tomlPath
}

// ---- CliWatermarkTests ----

// U13
func TestAddUserAllocatesFromWatermarkIncrements(t *testing.T) {
	dir := t.TempDir()
	usersPath := filepath.Join(dir, "users.json")
	writeUsersFile(t, usersPath, `{
  "nextTokenVersion": 7,
  "users": [
    {"username": "old", "passwordHash": "`+cliValidHash+`", "tokenVersion": 3, "disabled": false}
  ]
}`)
	configPath := configFor(t, usersPath)

	exit := withStdin(t, "test-password", []string{"user", "add", "--username", "newuser", "--password-stdin", "--config", configPath})
	assert.Equal(t, Ok, exit)

	loaded, err := users.Load(usersPath)
	require.NoError(t, err)
	var added *users.UserRecord
	for i := range loaded.Users {
		if loaded.Users[i].Username == "newuser" {
			added = &loaded.Users[i]
		}
	}
	require.NotNil(t, added, "newuser should exist")
	assert.EqualValues(t, 7, added.TokenVersion)
	assert.EqualValues(t, 8, loaded.NextTokenVersion)
	for _, u := range loaded.Users {
		if u.Username == "old" {
			assert.EqualValues(t, 3, u.TokenVersion)
		}
	}
}

// U14
func TestDeleteUserRecreateSameNameGetsFreshHigherVersion(t *testing.T) {
	dir := t.TempDir()
	usersPath := filepath.Join(dir, "users.json")
	writeUsersFile(t, usersPath, `{
  "nextTokenVersion": 5,
  "users": [
    {"username": "alice", "passwordHash": "`+cliValidHash+`", "tokenVersion": 2, "disabled": false}
  ]
}`)
	configPath := configFor(t, usersPath)

	assert.Equal(t, Ok, Run([]string{"user", "delete", "--username", "alice", "--config", configPath}, staticPasswordHasher{}))
	loaded, err := users.Load(usersPath)
	require.NoError(t, err)
	assert.Empty(t, loaded.Users)

	assert.Equal(t, Ok, withStdin(t, "test-password", []string{"user", "add", "--username", "alice", "--password-stdin", "--config", configPath}))

	loaded, err = users.Load(usersPath)
	require.NoError(t, err)
	require.Len(t, loaded.Users, 1)
	assert.Equal(t, "alice", loaded.Users[0].Username)
	assert.EqualValues(t, 5, loaded.Users[0].TokenVersion)
	assert.NotEqualValues(t, 2, loaded.Users[0].TokenVersion)
	assert.EqualValues(t, 6, loaded.NextTokenVersion)
}

// U15
func TestRevokeTokensSetsWatermarkIncrements(t *testing.T) {
	dir := t.TempDir()
	usersPath := filepath.Join(dir, "users.json")
	writeUsersFile(t, usersPath, `{
  "nextTokenVersion": 9,
  "users": [
    {"username": "bob", "passwordHash": "`+cliValidHash+`", "tokenVersion": 4, "disabled": false}
  ]
}`)
	configPath := configFor(t, usersPath)

	assert.Equal(t, Ok, Run([]string{"user", "revoke-tokens", "--username", "bob", "--config", configPath}, staticPasswordHasher{}))

	loaded, err := users.Load(usersPath)
	require.NoError(t, err)
	for _, u := range loaded.Users {
		if u.Username == "bob" {
			assert.EqualValues(t, 9, u.TokenVersion)
		}
	}
	assert.EqualValues(t, 10, loaded.NextTokenVersion)
}

// U16
func TestAddUserAtLongMaxWatermarkFailsFastFileUnchanged(t *testing.T) {
	dir := t.TempDir()
	usersPath := filepath.Join(dir, "users.json")
	originalJSON := `{
  "nextTokenVersion": 9223372036854775807,
  "users": [
    {"username": "old", "passwordHash": "` + cliValidHash + `", "tokenVersion": 1, "disabled": false}
  ]
}`
	writeUsersFile(t, usersPath, originalJSON)
	configPath := configFor(t, usersPath)

	assert.Equal(t, Error, withStdin(t, "test-password", []string{"user", "add", "--username", "newuser", "--password-stdin", "--config", configPath}))

	data, err := os.ReadFile(usersPath)
	require.NoError(t, err)
	assert.Equal(t, originalJSON, string(data))
	assertNoTmpFiles(t, dir)
}

// U17
func TestRevokeAtLongMaxWatermarkFailsFast(t *testing.T) {
	dir := t.TempDir()
	usersPath := filepath.Join(dir, "users.json")
	originalJSON := `{
  "nextTokenVersion": 9223372036854775807,
  "users": [
    {"username": "bob", "passwordHash": "` + cliValidHash + `", "tokenVersion": 1, "disabled": false}
  ]
}`
	writeUsersFile(t, usersPath, originalJSON)
	configPath := configFor(t, usersPath)

	assert.Equal(t, Error, Run([]string{"user", "revoke-tokens", "--username", "bob", "--config", configPath}, staticPasswordHasher{}))
	data, err := os.ReadFile(usersPath)
	require.NoError(t, err)
	assert.Equal(t, originalJSON, string(data))
}

func assertNoTmpFiles(t *testing.T, dir string) {
	t.Helper()
	entries, err := os.ReadDir(dir)
	require.NoError(t, err)
	for _, entry := range entries {
		assert.False(t, strings.HasSuffix(entry.Name(), ".tmp"), "leftover tmp file: %s", entry.Name())
	}
}

// ---- CliPasswordInputTests ----

func TestDetectsPasswordStdinFlag(t *testing.T) {
	assert.True(t, hasPasswordStdin([]string{"add", "--username", "alice", "--password-stdin"}))
	assert.False(t, hasPasswordStdin([]string{"add", "--username", "alice"}))
}

func TestPasswordStdinReadsOneLineWithoutConsoleKeyInput(t *testing.T) {
	r, w, err := os.Pipe()
	require.NoError(t, err)
	_, err = io.WriteString(w, "secret-password\n")
	require.NoError(t, err)
	_ = w.Close()

	original := os.Stdin
	os.Stdin = r
	t.Cleanup(func() {
		os.Stdin = original
		_ = r.Close()
	})

	password, err := readPassword("Password: ", []string{"add", "--username", "alice", "--password-stdin"})
	require.NoError(t, err)
	assert.Equal(t, "secret-password", password)
}

func TestPasswordStdinRejectsEmptyInput(t *testing.T) {
	r, w, err := os.Pipe()
	require.NoError(t, err)
	_ = w.Close()

	original := os.Stdin
	os.Stdin = r
	t.Cleanup(func() {
		os.Stdin = original
		_ = r.Close()
	})

	_, err = readPassword("Password: ", []string{"hash", "--password-stdin"})
	assert.Error(t, err)
}

// ---- SingleInstanceLockTests ----

func TestAcquireCreatesLockBesideUsersFile(t *testing.T) {
	tempDir := t.TempDir()
	usersFile := filepath.Join(tempDir, "users.json")
	lockPath, err := CreateLockPath(usersFile)
	require.NoError(t, err)

	assert.True(t, strings.HasSuffix(lockPath, "users.json.lock"))
	assert.Equal(t, tempDir, filepath.Dir(lockPath))
}

func TestSecondProcessCannotAcquireSameUsersFileLock(t *testing.T) {
	tempDir := t.TempDir()
	lockPath := filepath.Join(tempDir, "users.json.lock")

	handle1, err := Acquire(lockPath, 10_000_000) // 10ms
	require.NoError(t, err)
	require.NotNil(t, handle1)

	handle2, err := Acquire(lockPath, 10_000_000)
	require.NoError(t, err)
	assert.Nil(t, handle2)

	handle1.Release()

	handle3, err := Acquire(lockPath, 10_000_000)
	require.NoError(t, err)
	assert.NotNil(t, handle3)
	if handle3 != nil {
		handle3.Release()
	}
}

func TestStaleLockIsRecovered(t *testing.T) {
	tempDir := t.TempDir()
	lockPath := filepath.Join(tempDir, "users.json.lock")
	require.NoError(t, os.WriteFile(lockPath, []byte("999999"), 0o644))

	handle, err := Acquire(lockPath, 10_000_000)
	require.NoError(t, err)
	assert.NotNil(t, handle)
	if handle != nil {
		handle.Release()
	}
}

func TestAcquireRejectsPathWithoutDirectory(t *testing.T) {
	_, err := Acquire("users.json.lock", 10_000_000)
	assert.Error(t, err)
}

func TestDifferentUsersFilesCanLockIndependently(t *testing.T) {
	tempDir := t.TempDir()
	lockPath1 := filepath.Join(tempDir, "users1.json.lock")
	lockPath2 := filepath.Join(tempDir, "users2.json.lock")

	handle1, err := Acquire(lockPath1, 10_000_000)
	require.NoError(t, err)
	require.NotNil(t, handle1)
	defer handle1.Release()

	handle2, err := Acquire(lockPath2, 10_000_000)
	require.NoError(t, err)
	require.NotNil(t, handle2)
	defer handle2.Release()
}
