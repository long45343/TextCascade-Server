// Package users 是 Users.cs 的 1:1 迁移：
// 严格 JSON（未知/重复字段拒绝，Q5 扫描器）、校验规则、原子保存、字节级文件保留。
package users

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"regexp"
	"strconv"
	"strings"

	"github.com/long45343/TextCascade-Server/internal/protocol"
)

// UserRecord 对应 C# UserRecord record。
type UserRecord struct {
	Username     string
	PasswordHash string
	TokenVersion int64
	Disabled     bool
}

// UsersFile 对应 C# UsersFile。
type UsersFile struct {
	NextTokenVersion int64
	Users            []UserRecord
}

var argon2HashRe = regexp.MustCompile(`^\$argon2id\$v=\d+\$m=\d+,t=\d+,p=\d+\$[A-Za-z0-9+/=]+\$[A-Za-z0-9+/=]+$`)

// Load 对应 C# LoadUsers；文件不存在返回空文件。
func Load(path string) (*UsersFile, error) {
	if _, err := os.Stat(path); err != nil {
		if os.IsNotExist(err) {
			// C# UsersFile.NextTokenVersion 属性初始化器默认为 1。
			return &UsersFile{NextTokenVersion: 1}, nil
		}
		return nil, err
	}

	raw, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}

	root, err := protocol.Decode(raw, 4)
	if err != nil {
		return nil, err
	}

	if !root.IsObject() || !hasUniqueProperties(root, "nextTokenVersion", "users") {
		return nil, fmt.Errorf("users.json must contain nextTokenVersion and users.")
	}

	watermark := root.Get("nextTokenVersion")
	if watermark == nil || !watermark.IsNumber() {
		return nil, fmt.Errorf("users.json must contain nextTokenVersion and users.")
	}
	nextTokenVersion, err := strconv.ParseInt(watermark.RawNumber(), 10, 64)
	if err != nil {
		return nil, fmt.Errorf("users.json must contain nextTokenVersion and users.")
	}

	usersElement := root.Get("users")
	if usersElement == nil || !usersElement.IsArray() {
		return nil, fmt.Errorf("users.json must contain nextTokenVersion and users.")
	}

	for _, userElement := range usersElement.Items() {
		if !userElement.IsObject() ||
			!hasUniqueProperties(userElement, "username", "passwordHash", "tokenVersion", "disabled") {
			return nil, fmt.Errorf("Each users.json entry must contain username, passwordHash, tokenVersion, and disabled.")
		}
		username := userElement.Get("username")
		passwordHash := userElement.Get("passwordHash")
		tokenVersion := userElement.Get("tokenVersion")
		disabled := userElement.Get("disabled")
		if username == nil || !username.IsString() ||
			passwordHash == nil || !passwordHash.IsString() ||
			tokenVersion == nil || !tokenVersion.IsNumber() ||
			disabled == nil || !disabled.IsBool() {
			return nil, fmt.Errorf("Each users.json entry must contain username, passwordHash, tokenVersion, and disabled.")
		}
		if _, err := strconv.ParseInt(tokenVersion.RawNumber(), 10, 64); err != nil {
			return nil, fmt.Errorf("Each users.json entry must contain username, passwordHash, tokenVersion, and disabled.")
		}
	}

	users := &UsersFile{NextTokenVersion: nextTokenVersion}
	for _, userElement := range usersElement.Items() {
		users.Users = append(users.Users, UserRecord{
			Username:     userElement.Get("username").Str(),
			PasswordHash: userElement.Get("passwordHash").Str(),
			TokenVersion: mustParseInt64(userElement.Get("tokenVersion").RawNumber()),
			Disabled:     userElement.Get("disabled").Bool(),
		})
	}

	if err := users.Validate(); err != nil {
		return nil, err
	}
	return users, nil
}

// hasUniqueProperties 复刻 C# HasUniqueProperties：每个键必须在 known 中且不重复。
func hasUniqueProperties(element *protocol.Node, known ...string) bool {
	knownSet := make(map[string]struct{}, len(known))
	for _, key := range known {
		knownSet[key] = struct{}{}
	}
	seen := make(map[string]struct{}, len(known))
	for _, member := range element.Members() {
		if _, ok := knownSet[member.Key]; !ok {
			return false
		}
		if _, dup := seen[member.Key]; dup {
			return false
		}
		seen[member.Key] = struct{}{}
	}
	return true
}

// Validate 对应 C# ValidateUsers；错误消息逐条一致。
func (f *UsersFile) Validate() error {
	if f.NextTokenVersion <= 0 {
		return fmt.Errorf("nextTokenVersion must be a positive 64-bit integer.")
	}

	seen := make(map[string]struct{}, len(f.Users))
	for _, user := range f.Users {
		if strings.TrimSpace(user.Username) == "" {
			return fmt.Errorf("Usernames must be non-empty and unique.")
		}
		if _, dup := seen[user.Username]; dup {
			return fmt.Errorf("Usernames must be non-empty and unique.")
		}
		seen[user.Username] = struct{}{}

		if user.TokenVersion <= 0 || user.TokenVersion >= f.NextTokenVersion {
			return fmt.Errorf("User tokenVersion must be positive and less than nextTokenVersion.")
		}

		if !argon2HashRe.MatchString(user.PasswordHash) {
			return fmt.Errorf("User %s has an invalid Argon2id hash.", user.Username)
		}
	}
	return nil
}

// BuildLookup 对应 C# BuildUserLookup。
func (f *UsersFile) BuildLookup() map[string]UserRecord {
	lookup := make(map[string]UserRecord, len(f.Users))
	for _, user := range f.Users {
		lookup[user.Username] = user
	}
	return lookup
}

// Save 对应 C# SaveUsers：Validate → 临时文件 + rename 原子替换 + fsync。
func Save(path string, f *UsersFile) error {
	if err := f.Validate(); err != nil {
		return err
	}

	jsonBytes, err := marshalUsersFile(f)
	if err != nil {
		return err
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
	if _, err := file.Write(jsonBytes); err != nil {
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

// marshalUsersFile 复刻 System.Text.Json WriteIndented + CamelCase 的输出形态：
// 2 空格缩进、字段序 nextTokenVersion → users（username/passwordHash/tokenVersion/disabled）。
func marshalUsersFile(f *UsersFile) ([]byte, error) {
	var b strings.Builder
	b.WriteString("{\n")
	b.WriteString("  \"nextTokenVersion\": ")
	b.WriteString(strconv.FormatInt(f.NextTokenVersion, 10))
	if len(f.Users) == 0 {
		b.WriteString(",\n  \"users\": []\n}")
		return []byte(b.String()), nil
	}
	b.WriteString(",\n  \"users\": [\n")
	for i, user := range f.Users {
		b.WriteString("    {\n")
		b.WriteString("      \"username\": ")
		s, err := json.Marshal(user.Username)
		if err != nil {
			return nil, err
		}
		b.Write(s)
		b.WriteString(",\n      \"passwordHash\": ")
		s, err = json.Marshal(user.PasswordHash)
		if err != nil {
			return nil, err
		}
		b.Write(s)
		b.WriteString(",\n      \"tokenVersion\": ")
		b.WriteString(strconv.FormatInt(user.TokenVersion, 10))
		b.WriteString(",\n      \"disabled\": ")
		b.WriteString(strconv.FormatBool(user.Disabled))
		b.WriteString("\n    }")
		if i < len(f.Users)-1 {
			b.WriteString(",")
		}
		b.WriteString("\n")
	}
	b.WriteString("  ]\n}")
	return []byte(b.String()), nil
}

// Copy 对应 C# Copy。
func Copy(source *UsersFile) *UsersFile {
	result := &UsersFile{NextTokenVersion: source.NextTokenVersion}
	result.Users = append(result.Users, source.Users...)
	return result
}

func mustParseInt64(raw string) int64 {
	value, _ := strconv.ParseInt(raw, 10, 64)
	return value
}
