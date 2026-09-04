// Package cli 是 Cli.cs 的迁移：user add/passwd/disable/enable/delete/revoke-tokens/list/hash。
// 水位分配、溢出放弃、字节级文件保留行为一致；文案一致。
package cli

import (
	"bufio"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"golang.org/x/term"

	"github.com/long45343/TextCascade-Server/internal/auth"
	"github.com/long45343/TextCascade-Server/internal/config"
	"github.com/long45343/TextCascade-Server/internal/users"
)

// 退出码，与 C# Cli.Ok / Cli.Error 一致。
const (
	Ok    = 0
	Error = 1
)

// Run 对应 C# RunCli。
func Run(args []string, hasher auth.PasswordHasher) int {
	if len(args) == 0 || args[0] != "user" {
		return printUsage()
	}

	if hasher == nil {
		hasher = auth.NewArgon2Hasher()
	}

	rest := args[1:]
	rest, explicitConfigPath, ok := tryExtractConfigOption(rest)
	if !ok {
		fmt.Fprintln(os.Stderr, "--config requires a path.")
		return Error
	}

	configPath := explicitConfigPath
	if configPath == "" {
		if environmentConfig := os.Getenv("TEXTCASCADE_CONFIG"); environmentConfig != "" {
			configPath = environmentConfig
		} else {
			configPath = "textcascade.toml"
		}
	}

	cfg, err := loadConfig(configPath)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Configuration error: %v\n", err)
		return Error
	}

	lockPath, err := CreateLockPath(cfg.Files.UsersFile)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Unable to acquire users file lock: %v\n", err)
		return Error
	}
	lockHandle, err := Acquire(lockPath, 100*time.Millisecond)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Unable to acquire users file lock: %v\n", err)
		return Error
	}
	if lockHandle == nil {
		fmt.Fprintln(os.Stderr, "Another TextCascade CLI process is running.")
		return Error
	}
	defer lockHandle.Release()

	if len(rest) == 0 {
		return printUsage()
	}
	switch rest[0] {
	case "add":
		return commandAddUser(rest, hasher, cfg)
	case "passwd":
		return commandPasswd(rest, hasher, cfg)
	case "disable":
		return commandSetDisabled(rest, true, cfg)
	case "enable":
		return commandSetDisabled(rest, false, cfg)
	case "delete":
		return commandDeleteUser(rest, cfg)
	case "revoke-tokens":
		return commandRevokeTokens(rest, cfg)
	case "list":
		return commandListUsers(cfg)
	case "hash":
		return commandHashPassword(rest, hasher, cfg)
	default:
		return printUsage()
	}
}

func loadConfig(configPath string) (*config.RuntimeConfig, error) {
	cfg, err := config.LoadTOML(configPath, config.Defaults())
	if err != nil {
		return nil, err
	}
	cfg, err = config.ApplyEnv(cfg)
	if err != nil {
		return nil, err
	}
	return &cfg, nil
}

// CreateLockPath 对应 C# CreateLockPath：users.json 同目录 <file>.lock。
func CreateLockPath(usersFile string) (string, error) {
	fullUsersPath, err := filepath.Abs(usersFile)
	if err != nil {
		return "", err
	}
	directory := filepath.Dir(fullUsersPath)
	if strings.TrimSpace(directory) == "" {
		return "", errors.New("users.json path must include a parent directory.")
	}
	fileName := filepath.Base(fullUsersPath)
	return filepath.Join(directory, fileName+".lock"), nil
}

func printUsage() int {
	fmt.Fprintln(os.Stderr, "Usage: TextCascade.Server user <command> [options]")
	fmt.Fprintln(os.Stderr, "Commands: add, passwd, disable, enable, delete, revoke-tokens, list, hash")
	fmt.Fprintln(os.Stderr, "All commands accept --config <path>; fallback order is --config, TEXTCASCADE_CONFIG, then textcascade.toml.")
	return Error
}

// tryExtractConfigOption 对应 C# TryExtractConfigOption：--config <path> 与 --config=<path>。
func tryExtractConfigOption(args []string) (rest []string, configPath string, ok bool) {
	var remaining []string
	for index := 0; index < len(args); index++ {
		if args[index] == "--config" {
			if index+1 >= len(args) {
				return nil, "", false
			}
			index++
			configPath = args[index]
		} else if strings.HasPrefix(args[index], "--config=") {
			configPath = strings.TrimPrefix(args[index], "--config=")
		} else {
			remaining = append(remaining, args[index])
		}
	}
	return remaining, configPath, true
}

func commandAddUser(args []string, hasher auth.PasswordHasher, cfg *config.RuntimeConfig) int {
	username, found := tryGetOption(args, "username")
	if !found {
		fmt.Fprintln(os.Stderr, "--username is required")
		return Error
	}

	password, err := readPassword("Password: ", args)
	if err != nil {
		panic(err)
	}
	confirm := password
	if !hasPasswordStdin(args) {
		confirm, err = readPassword("Confirm: ", args)
		if err != nil {
			panic(err)
		}
	}
	if password != confirm {
		fmt.Fprintln(os.Stderr, "Passwords do not match.")
		return Error
	}

	usersPath := cfg.Files.UsersFile
	usersFile := loadForWrite(usersPath)
	for _, user := range usersFile.Users {
		if user.Username == username {
			fmt.Fprintf(os.Stderr, "User %s already exists.\n", username)
			return Error
		}
	}

	hash := hasher.Hash(password, createArgon2Params(cfg))
	tokenVersion := usersFile.NextTokenVersion
	if tokenVersion <= 0 || tokenVersion == 9223372036854775807 {
		fmt.Fprintln(os.Stderr, "nextTokenVersion overflow; refusing to add user.")
		return Error
	}

	usersFile.Users = append(usersFile.Users, users.UserRecord{Username: username, PasswordHash: hash, TokenVersion: tokenVersion})
	next, err := incrementWatermark(usersFile.NextTokenVersion)
	if err != nil {
		panic(err)
	}
	usersFile.NextTokenVersion = next
	if err := users.Save(usersPath, usersFile); err != nil {
		fmt.Fprintf(os.Stderr, "Configuration error: %v\n", err)
		return Error
	}
	fmt.Printf("Added user %s (tokenVersion %d).\n", username, tokenVersion)
	return Ok
}

func commandPasswd(args []string, hasher auth.PasswordHasher, cfg *config.RuntimeConfig) int {
	username, found := tryGetOption(args, "username")
	if !found {
		fmt.Fprintln(os.Stderr, "--username is required")
		return Error
	}

	usersPath := cfg.Files.UsersFile
	usersFile := loadForWrite(usersPath)
	index := indexOfUser(usersFile, username)
	if index < 0 {
		fmt.Fprintf(os.Stderr, "User %s not found.\n", username)
		return Error
	}

	password, err := readPassword("New password: ", args)
	if err != nil {
		panic(err)
	}
	hash := hasher.Hash(password, createArgon2Params(cfg))
	usersFile.Users[index].PasswordHash = hash
	if err := users.Save(usersPath, usersFile); err != nil {
		fmt.Fprintf(os.Stderr, "Configuration error: %v\n", err)
		return Error
	}
	fmt.Printf("Password updated for %s.\n", username)
	return Ok
}

func commandSetDisabled(args []string, disabled bool, cfg *config.RuntimeConfig) int {
	username, found := tryGetOption(args, "username")
	if !found {
		fmt.Fprintln(os.Stderr, "--username is required")
		return Error
	}

	usersPath := cfg.Files.UsersFile
	usersFile := loadForWrite(usersPath)
	index := indexOfUser(usersFile, username)
	if index < 0 {
		fmt.Fprintf(os.Stderr, "User %s not found.\n", username)
		return Error
	}

	usersFile.Users[index].Disabled = disabled
	if err := users.Save(usersPath, usersFile); err != nil {
		fmt.Fprintf(os.Stderr, "Configuration error: %v\n", err)
		return Error
	}
	state := "disabled"
	if !disabled {
		state = "enabled"
	}
	fmt.Printf("User %s %s.\n", username, state)
	return Ok
}

func commandDeleteUser(args []string, cfg *config.RuntimeConfig) int {
	username, found := tryGetOption(args, "username")
	if !found {
		fmt.Fprintln(os.Stderr, "--username is required")
		return Error
	}

	usersPath := cfg.Files.UsersFile
	usersFile := loadForWrite(usersPath)
	index := indexOfUser(usersFile, username)
	if index < 0 {
		fmt.Fprintf(os.Stderr, "User %s not found.\n", username)
		return Error
	}

	usersFile.Users = append(usersFile.Users[:index], usersFile.Users[index+1:]...)
	if err := users.Save(usersPath, usersFile); err != nil {
		fmt.Fprintf(os.Stderr, "Configuration error: %v\n", err)
		return Error
	}
	fmt.Printf("Deleted user %s.\n", username)
	return Ok
}

func commandRevokeTokens(args []string, cfg *config.RuntimeConfig) int {
	username, found := tryGetOption(args, "username")
	if !found {
		fmt.Fprintln(os.Stderr, "--username is required")
		return Error
	}

	usersPath := cfg.Files.UsersFile
	usersFile := loadForWrite(usersPath)
	index := indexOfUser(usersFile, username)
	if index < 0 {
		fmt.Fprintf(os.Stderr, "User %s not found.\n", username)
		return Error
	}

	newVersion := usersFile.NextTokenVersion
	if newVersion <= 0 || newVersion == 9223372036854775807 {
		fmt.Fprintln(os.Stderr, "nextTokenVersion overflow; refusing to revoke tokens.")
		return Error
	}

	usersFile.Users[index].TokenVersion = newVersion
	next, err := incrementWatermark(usersFile.NextTokenVersion)
	if err != nil {
		panic(err)
	}
	usersFile.NextTokenVersion = next
	if err := users.Save(usersPath, usersFile); err != nil {
		fmt.Fprintf(os.Stderr, "Configuration error: %v\n", err)
		return Error
	}
	fmt.Printf("Revoked tokens for %s (new tokenVersion %d).\n", username, newVersion)
	return Ok
}

func commandListUsers(cfg *config.RuntimeConfig) int {
	usersFile := loadForWrite(cfg.Files.UsersFile)
	fmt.Printf("nextTokenVersion: %d\n", usersFile.NextTokenVersion)
	fmt.Println("username,disabled,tokenVersion")
	for _, user := range usersFile.Users {
		fmt.Printf("%s,%v,%d\n", user.Username, user.Disabled, user.TokenVersion)
	}
	return Ok
}

func commandHashPassword(args []string, hasher auth.PasswordHasher, cfg *config.RuntimeConfig) int {
	password, err := readPassword("Password: ", args)
	if err != nil {
		panic(err)
	}
	hash := hasher.Hash(password, createArgon2Params(cfg))
	fmt.Println(hash)
	return Ok
}

func loadForWrite(path string) *users.UsersFile {
	if _, err := os.Stat(path); err != nil {
		if os.IsNotExist(err) {
			return &users.UsersFile{NextTokenVersion: 1}
		}
		panic(err)
	}
	usersFile, err := users.Load(path)
	if err != nil {
		// C# 侧 LoadUsers 异常直接崩溃进程；此处保持同语义。
		panic(err)
	}
	return usersFile
}

func indexOfUser(usersFile *users.UsersFile, username string) int {
	for i, user := range usersFile.Users {
		if user.Username == username {
			return i
		}
	}
	return -1
}

// incrementWatermark 对应 C# IncrementWatermark（checked）：溢出显式放弃。
func incrementWatermark(current int64) (int64, error) {
	if current == 9223372036854775807 {
		return 0, errors.New("overflow")
	}
	return current + 1, nil
}

// createArgon2Params 对应 C# CreateArgon2Config。
func createArgon2Params(cfg *config.RuntimeConfig) auth.Params {
	return auth.Params{
		MemoryKiB:   cfg.Auth.Argon2MemoryKiB,
		Iterations:  cfg.Auth.Argon2Iterations,
		Parallelism: cfg.Auth.Argon2Parallelism,
	}
}

func tryGetOption(args []string, name string) (string, bool) {
	flag := "--" + name
	for i := 0; i < len(args)-1; i++ {
		if args[i] == flag {
			return args[i+1], len(args[i+1]) > 0
		}
	}
	return "", false
}

func hasPasswordStdin(args []string) bool {
	return hasFlag(args, "password-stdin")
}

func hasFlag(args []string, name string) bool {
	flag := "--" + name
	for _, arg := range args {
		if arg == flag {
			return true
		}
	}
	return false
}

// readPassword 对应 C# ReadPassword：--password-stdin 或 x/term 交互输入。
func readPassword(prompt string, args []string) (string, error) {
	if hasPasswordStdin(args) {
		reader := bufio.NewReader(os.Stdin)
		line, err := reader.ReadString('\n')
		if err != nil && line == "" {
			fmt.Fprintln(os.Stderr, "--password-stdin requires one non-empty line.")
			return "", errors.New("--password-stdin requires one non-empty line.")
		}
		line = strings.TrimRight(line, "\r\n")
		if line == "" {
			fmt.Fprintln(os.Stderr, "--password-stdin requires one non-empty line.")
			return "", errors.New("--password-stdin requires one non-empty line.")
		}
		return line, nil
	}

	fmt.Print(prompt)
	password, err := term.ReadPassword(int(os.Stdin.Fd()))
	fmt.Println()
	if err != nil {
		return "", err
	}
	return string(password), nil
}
