// Package hosting：ServerHost.cs（进程装配半边）→ run.go。
// 装配顺序 Load → Env → Validate → LoadUsers → state store → cert.Load →
// watcher.Start → http.Server(ListenAndServeTLS)；错误打印与退出码一致。
package hosting

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"os"
	"os/signal"
	"strconv"
	"syscall"
	"time"

	"github.com/long45343/TextCascade-Server/internal/auth"
	"github.com/long45343/TextCascade-Server/internal/clock"
	"github.com/long45343/TextCascade-Server/internal/config"
	"github.com/long45343/TextCascade-Server/internal/logging"
	"github.com/long45343/TextCascade-Server/internal/state"
	"github.com/long45343/TextCascade-Server/internal/sync"
	"github.com/long45343/TextCascade-Server/internal/users"
)

// 退出码，与 C# ServerHost.Ok / Error 一致。
const (
	ExitOK    = 0
	ExitError = 1
)

// Run 对应 C# ServerHost.RunServer。
func Run(args []string) int {
	if len(args) > 0 && args[0] == "--config" && len(args) < 2 {
		fmt.Fprintln(os.Stderr, "Configuration error: --config requires a path.")
		return ExitError
	}

	configPath := "textcascade.toml"
	if len(args) > 0 && args[0] == "--config" {
		configPath = args[1]
	}

	cfg, usersFile, store, err := loadRuntime(configPath)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Configuration error: %v\n", err)
		return ExitError
	}

	certificate, err := LoadCertificate(cfg.Server.CertificatePath)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Configuration error: %v\n", err)
		return ExitError
	}

	logger := newLogger()
	srv := sync.New(cfg, usersFile, store, auth.NewArgon2Hasher(), clock.System, logger)

	watcher := NewWatcher(cfg.Files.UsersFile, srv, logger)
	watcher.Start()
	defer watcher.Close()

	// 优雅停机：signal.NotifyContext(SIGTERM, SIGINT)；流程 1:1：
	// bye → 1001 → drain 2s → 取消全部连接 → 同步 flush。
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	mux := http.NewServeMux()
	mux.HandleFunc("GET /health", handleHealth)
	mux.HandleFunc("POST /api/v1/login", func(w http.ResponseWriter, r *http.Request) {
		auth.HandleLogin(w, r, cfg, srv.LoginDeps())
	})
	mux.HandleFunc("GET /api/v1/sync", func(w http.ResponseWriter, r *http.Request) {
		HandleSync(w, r, cfg, srv)
	})

	tlsConfig := &tls.Config{
		Certificates: []tls.Certificate{certificate},
		// Q11：仅 HTTP/1.1（RFC6455 升级仅存在于 HTTP/1.1）。
		NextProtos: []string{"http/1.1"},
		// MinVersion 不显式设置：跟随 Go/OS 默认（对齐 §13.4 现状）。
	}
	httpServer := &http.Server{
		Addr:      net.JoinHostPort(cfg.Server.Bind, strconv.Itoa(cfg.Server.Port)),
		Handler:   mux,
		TLSConfig: tlsConfig,
		ErrorLog:  nil,
	}

	scannerCtx, scannerStop := context.WithCancel(context.Background())
	scannerDone := make(chan struct{})
	go func() {
		defer close(scannerDone)
		RunScanner(scannerCtx, srv, clock.System, logger)
	}()

	serveErr := make(chan error, 1)
	go func() {
		serveErr <- httpServer.ListenAndServeTLS("", "")
	}()

	select {
	case <-ctx.Done():
		scannerStop()
		<-scannerDone
		srv.Shutdown(2*time.Second, clock.System.Now())
		_ = httpServer.Close()
		store.Stop()
		return ExitOK
	case err := <-serveErr:
		scannerStop()
		<-scannerDone
		store.Stop()
		if err != nil && err != http.ErrServerClosed {
			fmt.Fprintf(os.Stderr, "%v\n", err)
		}
		return ExitError
	}
}

// loadRuntime 对应 C# RunServer 前半段：默认值 → TOML → 环境变量 → 校验 →
// users.json → state store。
func loadRuntime(configPath string) (*config.RuntimeConfig, *users.UsersFile, *state.Store, error) {
	cfg, err := config.LoadTOML(configPath, config.Defaults())
	if err != nil {
		return nil, nil, nil, err
	}
	cfg, err = config.ApplyEnv(cfg)
	if err != nil {
		return nil, nil, nil, err
	}
	if err := cfg.Validate(); err != nil {
		return nil, nil, nil, err
	}

	usersFile, err := users.Load(cfg.Files.UsersFile)
	if err != nil {
		return nil, nil, nil, err
	}

	store, err := state.NewStore(cfg.Files.StateFile, 5*time.Second, nil)
	if err != nil {
		return nil, nil, nil, err
	}
	return &cfg, usersFile, store, nil
}

func newLogger() *slog.Logger {
	return slog.New(logging.NewHandler())
}

func handleHealth(w http.ResponseWriter, _ *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusOK)
	_ = json.NewEncoder(w).Encode(struct {
		Status string `json:"status"`
	}{Status: "ok"})
}
