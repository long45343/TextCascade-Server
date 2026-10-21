# TextCascade Server

[简体中文](#简体中文) | [English](#english)

轻量、可靠、高性能的剪切板同步服务端。

基于 Go 标准库 `net/http` 与 `gorilla/websocket` 原生 WebSocket,通过 TLS 加密,使用无状态 token 认证与 tokenVersion 撤销机制。服务端重启后,客户端可在恢复窗口内上报 snapshot,随后只获取最新值。早期C#版本可无痛迁移。

---

## 简体中文

### 特性

- **安全优先**:禁止明文 HTTP 登录;密码使用 Argon2(id)，同时客户端可以设置加密参数。
- **性能优秀**:见perf.md
- **静态单二进制**:Go 自包含编译,目标机无需任何运行时。

### 技术栈

| 项 | 值 |
|---|---|
| 语言/工具链 | Go 1.27(静态编译,无运行时依赖) |
| Web 框架 | `net/http` + `gorilla/websocket` |
| 配置 | TOML(`pelletier/go-toml/v2`)+ 环境变量覆盖 |
| 密码哈希 | Argon2(id)(`golang.org/x/crypto/argon2`) |
| 单实例锁 | `gofrs/flock` |
| 用户文件监听 | `fsnotify` + 250ms 防抖 + 30s 轮询兜底 |
| 用户存储 | `users.json` |
| 协议子协议 | `textcascade.v1` |
| 产品版本 | SemVer,当前 `1.0.0` |

### 仓库结构

```
TextCascade-Server/                (go 分支,纯 Go 仓库)
├── cmd/server/main.go             入口:serve / user CLI 分发
├── internal/config/               TOML 配置、默认值、环境变量覆盖与校验
├── internal/users/                users.json 严格加载、校验与原子保存
├── internal/auth/                 Argon2 哈希器、token 签发校验、登录端点
├── internal/protocol/             JSON 协议解析/序列化与 token 级预扫描器
├── internal/core/                 滑动窗口限流、令牌桶、去重环形队列
├── internal/state/                版本号落盘(textcascade.state.json)
├── internal/hub/                  UserHub 单消费者循环、恢复窗口、注册表
├── internal/sync/                 SyncServer 协调器与优雅停机
├── internal/hosting/              证书、端点、连接处理、文件监听、心跳扫描
├── internal/models/               连接上下文、状态与任务模型
├── internal/cli/                  user 子命令与单实例锁
├── internal/logging/              结构化安全日志与脱敏
├── testdata/contract-samples/     协议契约样本(与实现无关,逐字节锁定)
└── deploy/                        systemd unit、示例 TOML 与空 users.json
```

### 快速开始

1. **构建**
   ```
   go build -trimpath -o TextCascade.Server ./cmd/server
   ```

2. **添加用户**
   ```
   ./TextCascade.Server user add --config /etc/textcascade/textcascade.toml --username alice
   ```
   CLI 子命令:`add`、`passwd`、`disable`、`enable`、`delete`、`revoke-tokens`、`list`、`hash`。

3. **运行服务**
   ```
   ./TextCascade.Server serve --config /etc/textcascade/textcascade.toml
   ```

   必须提供 token secret 环境变量(长度 >= 32 字节)和 TLS 证书;缺失或不合规时启动失败。

### 证书类型支持

| 格式 | 支持 | 说明 |
|---|---|---|
| `.pem` / `.crt` bundle + 同名 `.key` 边车 | ✅ | 叶证书在前,其余为链;`.key` 缺省时在 PEM 内查找私钥 |
| 单文件 PEM(证书 + 私钥合包) | ✅ | 证书块在前,私钥块(PKCS8 / PKCS1 / EC)在后 |
| 无密码 `.pfx` / `.p12` | ✅ | 私钥仅驻留内存 |
| 带密码 PFX / PKCS12 | ❌ | 不支持 |

### 从 C# 版迁移

- `users.json` / `textcascade.state.json` / `textcascade.toml` / PEM 证书格式不变,原样沿用。
- **存量密码哈希直接兼容**:Argon2id 为标准算法,Go 版可验证 C# 版创建的全部存量哈希(已在生产切换时实测),用户无感迁移,无需重置密码。
- 存量 token 继续有效(同一 secret、标准 HMAC);如需强制重新登录,对目标用户执行 `user revoke-tokens`。
- systemd 单元沿用,仅替换二进制路径。
- 其余线协议、错误码、关闭码、默认值零偏差;仅 TLS ALPN 只广播 `http/1.1`。

### 配置

配置优先级:内置安全默认值 < TOML 文件 < 环境变量覆盖。启动时强校验,非法值 fail-fast。

示例 `textcascade.toml`:
```toml
[server]
bind = "0.0.0.0"
port = 8443
certificate_path = "certs/server.pem"

[auth]
token_ttl_days = 30
token_secret_env = "TEXTCASCADE_TOKEN_SECRET"
argon2_memory_kib = 19456
argon2_iterations = 2
argon2_parallelism = 1

[limits]
max_text_bytes = 524288
max_frame_bytes = 589824
send_queue_capacity = 16
hello_timeout_seconds = 5
heartbeat_interval_seconds = 30
heartbeat_timeout_seconds = 60
snapshot_window_seconds = 3
snapshot_total_bytes = 4194304

[rate_limit]
login_ip_per_minute = 10
login_user_per_minute = 5
clip_burst = 10
clip_tokens_per_second = 2

[files]
users_file = "users.json"
state_file = "textcascade.state.json"
```

关键规则:
- `token_secret_env` 指向环境变量名,secret 不写入 TOML;长度 < 32 字节则启动失败。
- CLI 配置回退顺序为 `--config`、`TEXTCASCADE_CONFIG`、当前目录 `textcascade.toml`;`TEXTCASCADE_USERS_FILE` 与 `TEXTCASCADE_STATE_FILE` 仍可覆盖 TOML。
- TLS 始终启用;证书仅支持无密码格式(见上方证书支持矩阵)。
- `max_frame_bytes` 必须大于 `max_text_bytes`(差额留给协议头)。
- 所有容量与时间配置必须 > 0,心跳超时必须大于心跳间隔。

### HTTP / WebSocket 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/health` | 健康检查 |
| POST | `/api/v1/login` | 登录,换取 Bearer token |
| GET | `/api/v1/sync` | WebSocket 升级;子协议 `textcascade.v1`,Authorization 携带 token |

WebSocket 流程:升级前验 token → 升级后在 `hello_timeout_seconds` 内收 `hello`(注册设备 + 上报 snapshot)→ 验证通过后进入广播列表 → clip 广播给除发送者外连接并向发送者回 ACK → 心跳 ping/pong。

### 并发模型

1. 每连接独立读循环 goroutine:收帧、解析、验证,投递用户级 job 到 UserHub 无界队列。
2. 每用户单消费者 `RunUserLoop`:串行处理 clip、连接、断开与恢复 job;异常 recover 后自动 RebuildHub。
3. 广播只序列化一次 UTF-8 字节,同一份字节投递到每连接有界发送 Channel。
4. 慢连接发送队列满立即取消该连接,不等 drain,不补发应用层 error 或 close frame。

### 测试

```
go test ./...            # 单元 + 契约全矩阵
go test ./internal/hosting/ -run Network   # 真实 TLS 网络集成
```

测试覆盖协议解析与逐字节序列化契约、配置、登录限流、token 服务、用户文件、心跳与恢复、WebSocket 集成与真实 TLS 网络集成。

### 下载与发布

GitHub Release 提供两种静态单文件包,目标机无需任何运行时:

- `TextCascade.Server-<version>-windows-x64.zip`
- `TextCascade.Server-<version>-linux-x64.tar.gz`

包内附带主程序、配置模板;Linux 包另附 systemd unit。每次 Release 同时提供 SHA-256 校验文件。

推送 `v*.*.*` 标签(如 `v0.5.0`)会自动执行测试、构建双平台单文件包、生成校验和并发布 GitHub Release。`go`/`main` 分支和 Pull Request 会自动执行 gofmt/vet/build/test CI。

### 生产部署(systemd)

一键安装(推荐):脚本与发行包都来自 GitHub。

```bash
curl -fsSL https://raw.githubusercontent.com/long45343/TextCascade-Server/go/install.sh \
  -o /tmp/tc-install.sh
sudo sh /tmp/tc-install.sh install --cert fullchain.pem --key privkey.pem --username alice
```

不加 `--version` 时:在交互终端下会列出仓库全部 Release(**新 → 旧**,标注日期、预发布与缺失 `linux-x64` 资产的版本),让你输入序号选择,回车取最新正式版;`-y` 或在无终端的环境(CI、管道)中则直接取最新正式版。先看有哪些版本:

```bash
sh /tmp/tc-install.sh list            # 只列版本,不安装;加 --prerelease 连预发布一起列
```

脚本会完成:按 GitHub API 解析版本 → 下载 Release 资产并校验 SHA-256 → 建系统用户与目录 → 安装证书 → 生成 token secret → 写配置与 systemd 单元 → 启动 → 创建首个用户 → 健康检查与一次真实登录验收。

其他动作:

```bash
sudo sh /tmp/tc-install.sh upgrade                 # 升级到最新正式版
sudo sh /tmp/tc-install.sh upgrade --version 1.0.0-Alpha
sudo sh /tmp/tc-install.sh status
sudo sh /tmp/tc-install.sh uninstall [--purge [--delete-user]]
```

要点:
- 不要用 `curl ... | sh`:脚本需要用标准输入接收密码。先下载到文件再执行。
- 已存在的 secret、`users.json`、证书、配置与 systemd 单元默认一律保留;二进制与单元按内容比对,内容相同则不替换、不重启,因此可反复执行做修复。
- `upgrade` 保留全部数据,旧二进制留作 `TextCascade.Server.previous`。
- **只支持 1.0.0-Alpha 起的 Go 静态版本**。0.5.0 及更早的 Release 是 .NET 单文件程序,需要 .NET 运行时,脚本会检测并拒绝。
- 可选:`list`/`--list`(列出可用版本)、`--prerelease`(列表与"最新"都含预发布)、`--repo OWNER/REPO`、`--mirror https://ghfast.top`(GitHub 下载加速)、`--from-file`(离线包)、`--no-start`、`--force`。
- 内网无法访问 GitHub 时:`TEXTCASCADE_GITHUB_MIRROR` / `TEXTCASCADE_GITHUB_TOKEN` 环境变量等价于 `--mirror` / token 注入;也可用 `--base-url` 指向自建文件服务器(`<base>/latest.txt` + `<base>/<version>/...`)。
- 仅支持 systemd ≥ 235 的 Linux;发行包目前只提供 `x64`,ARM 主机需自行交叉编译。

完整选项见 `sh install.sh --help`。手工部署参考 `deploy/textcascade-server.service`:
- 以专用系统用户 `textcascade` 运行;先执行 `useradd --system --home /opt/textcascade-server --shell /usr/sbin/nologin textcascade`。
- 以 systemd 托管,`Restart=on-failure`,开启 `ProtectSystem`/`ProtectHome`/`PrivateTmp` 等加固项。
- 配置与 users.json 放 `/etc/textcascade/`,运行状态放 `/var/lib/textcascade/`,程序放 `/opt/textcascade-server/`;目录属主设为 `textcascade:textcascade`。
- token secret 由 `/etc/textcascade/textcascade.env` 注入。

### 生产部署(Docker)

镜像为 distroless 静态构建(约 11 MB,无 shell,UID 65532),支持 `linux/amd64` 与 `linux/arm64`。

```bash
docker pull ghcr.io/long45343/textcascade-server:latest

# 仓库已提供 docker/config 骨架 / The repo ships a docker/config skeleton
cp your.pem docker/config/certs/server.pem
cp your.key docker/config/certs/server.key
cp docker/textcascade.env.example docker/textcascade.env
sed -i "s|^TEXTCASCADE_TOKEN_SECRET=.*|TEXTCASCADE_TOKEN_SECRET=$(openssl rand -base64 48)|" docker/textcascade.env
chmod 600 docker/textcascade.env
chown -R 65532:65532 docker/config

docker compose up -d
docker exec -it textcascade-server /TextCascade.Server user add \
  --config /etc/textcascade/textcascade.toml --username alice
```

- 配置目录 `./docker/config` bind 到 `/etc/textcascade`,运行状态用 named volume `textcascade-state`。`users.json` 以「临时文件 + rename」写入,必须目录挂载而非单文件挂载。
- 容器以 `read_only` 根文件系统 + `cap_drop: ALL` + `no-new-privileges` 运行,只写上述两个卷。
- 启动时校验数据目录存在且可写,不满足则 fail-fast 并报出具体路径。
- 登录限流按 `RemoteAddr` 计,**不要部署在 HTTP 层反代之后**(会合并成同一来源);直连发布端口或四层转发均保留源 IP。
- 证书替换后 `docker compose restart server` 生效。
- `linux/arm64` 镜像可构建与发布,但**未经 ARM 实机验证**。

完整说明(卷与权限、备份恢复、升级、证书轮换、故障排查、镜像加速)见 [docs/docker.md](docs/docker.md)。

### 许可

见仓库 [LICENSE](LICENSE)(GPL-3.0)。

---

### 发版与维护规范

- 每个用户可见版本发布前，需将 CHANGELOG.md 中的 [Unreleased] 部分归档为对应版本号与发版日期，并维护底部的 compare 链接。
- 版本号遵循 [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html)，版本变更记录遵循 [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)。

---

## English

A lightweight, reliable, high-performance clipboard sync server.

Built on Go's `net/http` with native `gorilla/websocket` connections, TLS-terminated, stateless-token auth with tokenVersion revocation. After a server restart, clients report a snapshot within a recovery window, then receive only the latest value. The early C# version can be migrated painlessly.

### Highlights

- **Security-first**: no plaintext HTTP login; Argon2(id) password hashing, while clients can configure their own encryption parameters.
- **Excellent performance**: see [perf.md](perf.md).
- **Static single binary**: self-contained Go build; no runtime prerequisites.

### Tech Stack

| Item | Value |
|---|---|
| Language / toolchain | Go 1.27 (static binary, no runtime) |
| Web framework | `net/http` + `gorilla/websocket` |
| Config | TOML (`pelletier/go-toml/v2`) + env overrides |
| Password hash | Argon2(id) (`golang.org/x/crypto/argon2`) |
| Single-instance lock | `gofrs/flock` |
| Users file watching | `fsnotify` + 250ms debounce + 30s poll fallback |
| User store | `users.json` |
| Subprotocol | `textcascade.v1` |
| Version | SemVer, currently `1.0.0` |

### Quick Start

1. **Build**
   ```
   go build -trimpath -o TextCascade.Server ./cmd/server
   ```

2. **Add a user**
   ```
   ./TextCascade.Server user add --config /etc/textcascade/textcascade.toml --username alice
   ```
   CLI subcommands: `add`, `passwd`, `disable`, `enable`, `delete`, `revoke-tokens`, `list`, `hash`.

3. **Run**
   ```
   ./TextCascade.Server serve --config /etc/textcascade/textcascade.toml
   ```

   A token-secret env var (>= 32 bytes) and a TLS certificate are required; startup fails if missing or invalid.

### Certificate Type Support

| Format | Supported | Notes |
|---|---|---|
| `.pem` / `.crt` bundle + sibling `.key` | ✅ | Leaf first, then chain; private key looked up inside the PEM when `.key` is absent |
| Single PEM (cert + key combined) | ✅ | Cert block(s) first, then key block (PKCS8 / PKCS1 / EC) |
| Password-less `.pfx` / `.p12` | ✅ | Private key kept in memory only |
| Password-protected PFX / PKCS12 | ❌ | Not supported |

### Migrating from the C# server

- `users.json` / `textcascade.state.json` / `textcascade.toml` / PEM certificates are unchanged and can be carried over as-is.
- **Existing password hashes are directly compatible**: Argon2id is a standardized algorithm and the Go build verifies every hash created by the C# build (verified during the production switchover) — users migrate transparently, no password resets.
- Existing tokens remain valid (same secret, standard HMAC); to force re-login for a user, run `user revoke-tokens`.
- The systemd unit carries over; only the binary path changes.
- Everything else on the wire is byte-identical; the only declarative difference is ALPN advertising `http/1.1`.

### Configuration

Precedence: built-in safe defaults < TOML file < env overrides. Validated strictly at startup; invalid values fail fast. See the TOML example in the Chinese section above — it is identical for both languages.

Key rules:
- `token_secret_env` names an env var; the secret is never written to TOML and must be >= 32 bytes.
- CLI config fallback is `--config`, then `TEXTCASCADE_CONFIG`, then `textcascade.toml`; `TEXTCASCADE_USERS_FILE` and `TEXTCASCADE_STATE_FILE` still override TOML.
- TLS is always on; only password-less certs are supported (see the matrix above).
- `max_frame_bytes` must exceed `max_text_bytes` (the difference covers the JSON header).
- All capacity/time values must be > 0; heartbeat timeout must exceed the interval.

### HTTP / WebSocket API

| Method | Path | Description |
|---|---|---|
| GET | `/health` | Health check |
| POST | `/api/v1/login` | Login, returns a Bearer token |
| GET | `/api/v1/sync` | WebSocket upgrade; subprotocol `textcascade.v1`, Authorization carries the token |

WebSocket flow: validate token before upgrade -> receive `hello` within `hello_timeout_seconds` (register device + report snapshot) -> join broadcast set on success -> clip broadcasts to all connections except the sender, with an ACK back to the sender -> heartbeat ping/pong.

### Concurrency Model

1. Each connection has an independent read-loop goroutine: receive frame, parse, validate, enqueue a user-level job to the UserHub unbounded queue.
2. Each user has a single-consumer `RunUserLoop`: serially processes clip, connect, disconnect and recovery jobs; a panicking loop recovers and rebuilds the hub automatically.
3. Broadcast serializes UTF-8 bytes once and dispatches the same bytes to each connection's bounded send channel.
4. A slow connection whose send queue is full is cancelled immediately; no drain, no app-layer error or close frame replay.

### Tests

```
go test ./...
go test ./internal/hosting/ -run Network
```

Covers protocol parsing and byte-exact serialization contracts, config, login limiting, token service, users file, heartbeat and recovery, WebSocket integration, and real-TLS network integration.

### Downloads and Releases

GitHub Releases provides two static single-file archives. No runtime is required on the target machine:

- `TextCascade.Server-<version>-windows-x64.zip`
- `TextCascade.Server-<version>-linux-x64.tar.gz`

Each archive contains the executable and config template; the Linux archive also includes the systemd unit. Every Release includes a SHA-256 checksum file.

Pushing a `v*.*.*` tag (for example `v0.5.0`) runs tests, builds both single-file archives, generates checksums, and publishes a GitHub Release. Pushes to `go`/`main` and pull requests run gofmt/vet/build/test CI automatically.

### Production (systemd)

One-command install (recommended); both the script and the archives come from GitHub.

```bash
curl -fsSL https://raw.githubusercontent.com/long45343/TextCascade-Server/go/install.sh \
  -o /tmp/tc-install.sh
sudo sh /tmp/tc-install.sh install --cert fullchain.pem --key privkey.pem --username alice
```

Without `--version` it lists every release in the repo (newest first, with dates, prerelease markers, and any version missing a `linux-x64` asset) and asks you to pick a number when stdin is a terminal; Enter takes the latest stable release. With `-y` or no terminal (CI, pipes) it takes the latest stable release automatically. To just see the versions:

```bash
sh /tmp/tc-install.sh list            # list only, no install; add --prerelease to include prereleases
```

The script then resolves the version, downloads the release asset and verifies its SHA-256, creates the system user and directories, installs the certificate, generates the token secret, writes the config and systemd unit, starts the service, creates the first user, and verifies health plus a real login.

Other actions:

```bash
sudo sh /tmp/tc-install.sh upgrade                 # upgrade to the latest stable release
sudo sh /tmp/tc-install.sh upgrade --version 1.0.0-Alpha
sudo sh /tmp/tc-install.sh status
sudo sh /tmp/tc-install.sh uninstall [--purge [--delete-user]]
```

Notes:
- Do not pipe the script into `sh` (`curl ... | sh`): it needs stdin for the password. Download it first.
- Existing secret, `users.json`, certificate, config, and systemd unit are preserved by default; the binary and unit are compared by content, so an unchanged install is neither replaced nor restarted. Safe to re-run for repair.
- `upgrade` keeps all data and saves the old binary as `TextCascade.Server.previous`.
- **Go static builds from 1.0.0-Alpha onward only.** Releases 0.5.0 and earlier are .NET single-file apps that need the .NET runtime; the script detects and rejects them.
- Optional: `list`/`--list` (list available versions), `--prerelease` (include prereleases in both the list and "latest"), `--repo OWNER/REPO`, `--mirror https://ghfast.top` (GitHub download accelerator), `--from-file` (offline archive), `--no-start`, `--force`.
- When GitHub is unreachable: `TEXTCASCADE_GITHUB_MIRROR` / `TEXTCASCADE_GITHUB_TOKEN` are equivalent to `--mirror` and a token, and `--base-url` points at a self-hosted file server (`<base>/latest.txt` plus `<base>/<version>/...`).
- Requires systemd ≥ 235; release archives currently ship `x64` only, so ARM hosts need to cross-compile.

See `sh install.sh --help` for every option. Manual deployment reference: `deploy/textcascade-server.service`:
- Run as the dedicated `textcascade` system user; create it with `useradd --system --home /opt/textcascade-server --shell /usr/sbin/nologin textcascade`.
- Managed by systemd with `Restart=on-failure` and hardening flags (`ProtectSystem`, `ProtectHome`, `PrivateTmp`).
- Config and `users.json` under `/etc/textcascade/`, runtime state under `/var/lib/textcascade/`, and binaries under `/opt/textcascade-server/`; set directory ownership to `textcascade:textcascade`.
- Token secret injected via `/etc/textcascade/textcascade.env`.

### Production (Docker)

The image is a distroless static build (~11 MB, no shell, UID 65532) for `linux/amd64` and `linux/arm64`.

```bash
docker pull ghcr.io/long45343/textcascade-server:latest

# 仓库已提供 docker/config 骨架 / The repo ships a docker/config skeleton
cp your.pem docker/config/certs/server.pem
cp your.key docker/config/certs/server.key
cp docker/textcascade.env.example docker/textcascade.env
sed -i "s|^TEXTCASCADE_TOKEN_SECRET=.*|TEXTCASCADE_TOKEN_SECRET=$(openssl rand -base64 48)|" docker/textcascade.env
chmod 600 docker/textcascade.env
chown -R 65532:65532 docker/config

docker compose up -d
docker exec -it textcascade-server /TextCascade.Server user add \
  --config /etc/textcascade/textcascade.toml --username alice
```

- `./docker/config` is bind-mounted to `/etc/textcascade`; runtime state lives in the `textcascade-state` named volume. `users.json` is written atomically (temp file + rename), so mount the directory, never a single file.
- The container runs with a `read_only` root filesystem, `cap_drop: ALL` and `no-new-privileges`, writing only the two locations above.
- Startup verifies the data directories exist and are writable, failing fast with the offending path.
- Login rate limiting keys on `RemoteAddr` and **has no `X-Forwarded-For` support**: do not deploy behind an HTTP-layer reverse proxy (all clients would collapse into one source). Publishing the port directly or layer-4 forwarding preserves the source IP.
- Replacing the certificate takes effect after `docker compose restart server`.
- The `linux/arm64` image builds and is published but **has not been verified on ARM hardware**.

Full details (volumes and permissions, backup/restore, upgrades, certificate rotation, troubleshooting, registry mirrors) are in [docs/docker.md](docs/docker.md).

### License

See [LICENSE](LICENSE) (GPL-3.0).

### Release & Maintenance Guidelines

- Before releasing any user-visible version, update CHANGELOG.md by moving the [Unreleased] section to the target version number and release date, along with the compare link at the bottom.
- Versioning adheres to [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html) and change records follow [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
