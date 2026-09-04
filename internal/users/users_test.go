package users

import (
	"os"
	"path/filepath"
	"testing"

	"github.com/stretchr/testify/assert"
	"github.com/stretchr/testify/require"
)

const validHash = "$argon2id$v=19$m=19456,t=2,p=1$c2FsdA$aG9zdA"

// ---- UsersFileTests ----

func TestValidateRejectsWhenNextTokenVersionMissing(t *testing.T) {
	f := &UsersFile{NextTokenVersion: 0, Users: []UserRecord{{Username: "alice", PasswordHash: validHash, TokenVersion: 1}}}
	assert.Error(t, f.Validate())
}

func TestValidateRejectsWhenTokenVersionNotLessThanWatermark(t *testing.T) {
	f := &UsersFile{NextTokenVersion: 3, Users: []UserRecord{{Username: "alice", PasswordHash: validHash, TokenVersion: 3}}}
	assert.Error(t, f.Validate())
}

func TestValidateRejectsDuplicateUsernames(t *testing.T) {
	f := &UsersFile{NextTokenVersion: 3, Users: []UserRecord{
		{Username: "alice", PasswordHash: validHash, TokenVersion: 1},
		{Username: "alice", PasswordHash: validHash, TokenVersion: 2},
	}}
	assert.Error(t, f.Validate())
}

func TestValidateRejectsBadHash(t *testing.T) {
	f := &UsersFile{NextTokenVersion: 2, Users: []UserRecord{{Username: "alice", PasswordHash: "not-a-hash", TokenVersion: 1}}}
	assert.Error(t, f.Validate())
}

func TestRoundTrip(t *testing.T) {
	path := filepath.Join(t.TempDir(), "users.json")
	f := &UsersFile{NextTokenVersion: 2, Users: []UserRecord{{Username: "alice", PasswordHash: validHash, TokenVersion: 1}}}
	require.NoError(t, Save(path, f))
	loaded, err := Load(path)
	require.NoError(t, err)
	assert.EqualValues(t, 2, loaded.NextTokenVersion)
	assert.Len(t, loaded.Users, 1)
}

func TestBuildUserLookupIsReadOnly(t *testing.T) {
	f := &UsersFile{NextTokenVersion: 2, Users: []UserRecord{{Username: "alice", PasswordHash: validHash, TokenVersion: 1}}}
	lookup := f.BuildLookup()
	assert.Contains(t, lookup, "alice")
}

// Load 严格性：未知字段 / 重复字段 / 非 UTF-8 拒绝。
func TestLoadRejectsUnknownField(t *testing.T) {
	path := filepath.Join(t.TempDir(), "users.json")
	content := `{"nextTokenVersion":2,"users":[{"username":"alice","passwordHash":"` + validHash + `","tokenVersion":1,"disabled":false}],"extra":1}`
	require.NoError(t, writeFile(path, content))
	_, err := Load(path)
	assert.Error(t, err)
}

func TestLoadRejectsDuplicateField(t *testing.T) {
	path := filepath.Join(t.TempDir(), "users.json")
	content := `{"nextTokenVersion":2,"nextTokenVersion":2,"users":[]}`
	require.NoError(t, writeFile(path, content))
	_, err := Load(path)
	assert.Error(t, err)
}

func TestLoadMissingReturnsEmpty(t *testing.T) {
	f, err := Load(filepath.Join(t.TempDir(), "missing.json"))
	require.NoError(t, err)
	// C# UsersFile.NextTokenVersion 属性初始化器默认为 1。
	assert.EqualValues(t, 1, f.NextTokenVersion)
	assert.Empty(t, f.Users)
}

func writeFile(path, content string) error {
	return os.WriteFile(path, []byte(content), 0o644)
}
