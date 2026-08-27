# TextCascade Server

[简体中文](#简体中文) | [English](#english)

轻量、可靠、高性能的文本最新值同步服务端。仅同步每个用户的当前文本剪贴板值,不保存历史,无需数据库。

基于 ASP.NET Core Minimal API 与 Kestrel 原生 WebSocket,通过 TLS 加密,使用无状态 token 认证与 tokenVersion 撤销机制。服务端重启后,客户端可在恢复窗口内上报 snapshot,随后只获取最新值。

---

## 简体中文

### 特性

- **仅同步最新值**:每用户只保存一份当前文本,不做历史记录、不补离线消息。
- **无数据库**:账号存于 `users.json`,文本与版本只在内存中;重启后由客户端上报恢复。
- **安全优先**:Kestrel 直接终止 TLS,禁止明文 HTTP 登录;密码使用 Argon2(id) 哈希;token 放 Authorization header,不进 URL。
- **高并发隔离**:每用户一个单消费者 `UserLoopAsync`,每连接独立读循环与发送 Channel;慢连接只积压自身队列,绝不拖垮全局。
- **限流防护**:登录 IP/用户滑动窗口限流,clip 令牌桶突发与速率限制。
- **明确错误语义**:协议错误显式返回,慢连接被隔离或断开,不发送 1013/4408 关闭码。
- **单进程托管**:生产环境由 systemd 或 Windows Service 托管并崩溃自动重启。

### 技术栈

| 项 | 值 |
|---|---|
| 目标框架 | `net10.0`(需预装 .NET 10 Runtime) |
| Web 框架 | ASP.NET Core Minimal API + Kestrel 原生 WebSocket |
| 配置 | TOML(`Tomlyn`)+ 环境变量覆盖 |
| 密码哈希 | Argon2(id)(`Isopoh.Cryptography.Argon2`) |
| 用户存储 | `users.json` |
| 协议子协议 | `textcascade.v1` |
| 产品版本 | SemVer,当前 `0.4.0` |

### 仓库结构

```
TextCascade-Server/
├── TextCascade.Server/            服务端源码
│   ├── Program.cs                 入口:serve / user CLI 分发
│   ├── ServerHost.cs              配置加载、证书、WebHost 构建、路由映射
│   ├── SyncServer.cs              核心协调器
│   ├── Hosting/                   端点、连接处理、心跳与文件监听
│   ├── Hub/                       UserHub、UserRegistry、协调器接口与任务
│   ├── Models/                    连接上下文、状态模型与接收消息
│   ├── Protocol.cs                JSON 协议模型与解析
│   ├── Auth.cs / AuthService.cs   token 签发、校验、登录限流
│   ├── Users.cs / Cli.cs          用户文件与 CLI(add/passwd/...)
│   ├── RuntimeConfig.cs          TOML 配置与默认值、环境变量覆盖
│   └── Core.cs                    限流、去重环形队列等基础工具
├── TextCascade.Server.Tests/      xUnit 测试
├── deploy/                        systemd unit、示例 TOML 与空 users.json
├── CHANGELOG.md                   版本变更记录
└── TextCascade.Server.slnx        解决方案
```

### 快速开始

1. **构建**
   ```
   dotnet build TextCascade.Server.csproj -c Release
   ```

2. **发布**(框架依赖单文件)
   ```
   dotnet publish TextCascade.Server.csproj -c Release -p:PublishSingleFile=true
   ```

3. **添加用户**
   ```
   dotnet TextCascade.Server.dll user add --config /etc/textcascade/textcascade.toml --username alice
   ```
   CLI 子命令:`add`、`passwd`、`disable`、`enable`、`delete`、`revoke-tokens`、`list`、`hash`。

4. **运行服务**
   ```
   dotnet TextCascade.Server.dll serve --config /etc/textcascade/textcascade.toml
   ```

   必须提供 token secret 环境变量(长度 >= 32 字节)和 TLS 证书;缺失或不合规时启动失败。

### 配置

配置优先级:内置安全默认值 < TOML 文件 < 环境变量覆盖。启动时强校验,非法值 fail-fast。

示例 `textcascade.toml`:
```toml
[server]
bind = "0.0.0.0"
port = 8443
certificate_path = "certs/server.pfx"

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
```

关键规则:
- `token_secret_env` 指向环境变量名,secret 不写入 TOML;长度 < 32 字节则启动失败。
- CLI 配置回退顺序为 `--config`、`TEXTCASCADE_CONFIG`、当前目录 `textcascade.toml`;`TEXTCASCADE_USERS_FILE` 与 `TEXTCASCADE_STATE_FILE` 仍可覆盖 TOML。
- TLS 始终启用;证书仅支持无密码格式(PEM bundle 或无密码 PFX),带密码 PFX 不支持。
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

1. 每连接独立读循环:收帧、解析、验证,投递用户级 job 到 UserHub Channel。
2. 每用户单消费者 `UserLoopAsync`:串行处理 clip、连接、断开与恢复 job。
3. 广播只序列化一次 UTF-8 字节,同一份字节投递到每连接有界发送 Channel。
4. 慢连接发送队列满立即取消该连接,不等 drain,不补发应用层 error 或 close frame。

### 测试

```
dotnet test
```

测试覆盖协议解析、配置、登录限流、token 服务、用户文件、clip 与核心逻辑。


### 下载与发布

GitHub Release 提供两种 Framework-dependent 单文件包,目标机需预装 .NET 10 Runtime:

- `TextCascade.Server-<version>-windows-x64.zip`
- `TextCascade.Server-<version>-linux-x64.tar.gz`

包内附带主程序、配置模板;Linux 包另附 systemd unit。每次 Release 同时提供 SHA-256 校验文件。

推送 `v*.*.*` 标签(如 `v0.3.0`)会自动执行测试、构建双平台单文件包、生成校验和并发布 GitHub Release。`main` 分支和 Pull Request 会自动执行 restore/build/test CI。
### 生产部署(systemd)

参考 `deploy/textcascade-server.service`:
- 以专用系统用户 `textcascade` 运行;先执行 `useradd --system --home /opt/textcascade-server --shell /usr/sbin/nologin textcascade`。
- 以 systemd 托管,`Restart=on-failure`,开启 `ProtectSystem`/`ProtectHome`/`PrivateTmp` 等加固项。
- 配置与 users.json 放 `/etc/textcascade/`,运行状态放 `/var/lib/textcascade/`,程序放 `/opt/textcascade-server/`;目录属主设为 `textcascade:textcascade`。
- token secret 由 `/etc/textcascade/textcascade.env` 注入。

### 许可

见仓库 LICENSE(如有)。

---

### 发版与维护规范

- 每个用户可见版本发布前，需将 CHANGELOG.md 中的 [Unreleased] 部分归档为对应版本号与发版日期，并维护底部的 compare 链接。
- 版本号遵循 [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html)，版本变更记录遵循 [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)。

---

## English

A lightweight, reliable, high-performance server that synchronizes only the latest text value per user. No history, no database.

Built on ASP.NET Core Minimal API with native Kestrel WebSockets, TLS-terminated, stateless-token auth with tokenVersion revocation. After a server restart, clients report a snapshot within a recovery window, then receive only the latest value.

### Highlights

- **Latest-value only**: one current text per user; no history, no offline backfill.
- **No database**: accounts in `users.json`, text and version in memory; clients recover after restart.
- **Security-first**: Kestrel terminates TLS directly; no plaintext HTTP login; Argon2(id) password hashing; token travels in the Authorization header, never the URL.
- **Concurrency isolation**: one single-consumer `UserLoopAsync` per user, per-connection read loop and bounded send channel; slow connections only back up their own queue.
- **Rate limiting**: sliding-window login limits per IP/user, token-bucket clip burst and rate control.
- **Explicit errors**: protocol errors are returned explicitly; slow peers are isolated or dropped; no 1013/4408 close codes.
- **Single-process hosting**: managed by systemd or a Windows Service with auto-restart.

### Tech Stack

| Item | Value |
|---|---|
| Target framework | `net10.0` (.NET 10 Runtime required) |
| Web framework | ASP.NET Core Minimal API + native Kestrel WebSocket |
| Config | TOML (`Tomlyn`) + env overrides |
| Password hash | Argon2(id) (`Isopoh.Cryptography.Argon2`) |
| User store | `users.json` |
| Subprotocol | `textcascade.v1` |
| Version | SemVer, currently `0.4.0` |

### Quick Start

1. **Build**
   ```
   dotnet build TextCascade.Server.csproj -c Release
   ```

2. **Publish** (framework-dependent single file)
   ```
   dotnet publish TextCascade.Server.csproj -c Release -p:PublishSingleFile=true
   ```

3. **Add a user**
   ```
   dotnet TextCascade.Server.dll user add --config /etc/textcascade/textcascade.toml --username alice
   ```
   CLI subcommands: `add`, `passwd`, `disable`, `enable`, `delete`, `revoke-tokens`, `list`, `hash`.

4. **Run**
   ```
   dotnet TextCascade.Server.dll serve --config /etc/textcascade/textcascade.toml
   ```

   A token-secret env var (>= 32 bytes) and a TLS certificate are required; startup fails if missing or invalid.

### Configuration

Precedence: built-in safe defaults < TOML file < env overrides. Validated strictly at startup; invalid values fail fast.

Example `textcascade.toml`:
```toml
[server]
bind = "0.0.0.0"
port = 8443
certificate_path = "certs/server.pfx"

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
state_file = "textcascade.state.json"
```

Key rules:
- `token_secret_env` names an env var; the secret is never written to TOML and must be >= 32 bytes.
- CLI config fallback is `--config`, then `TEXTCASCADE_CONFIG`, then `textcascade.toml`; `TEXTCASCADE_USERS_FILE` and `TEXTCASCADE_STATE_FILE` still override TOML.
- TLS is always on; only password-less certs are supported (PEM bundle or password-less PFX).
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

1. Each connection has an independent read loop: receive frame, parse, validate, enqueue a user-level job to the UserHub channel.
2. Each user has a single-consumer `UserLoopAsync`: serially processes clip, connect, disconnect and recovery jobs.
3. Broadcast serializes UTF-8 bytes once and dispatches the same bytes to each connection's bounded send channel.
4. A slow connection whose send queue is full is cancelled immediately; no drain, no app-layer error or close frame replay.

### Tests

```
dotnet test
```

Covers protocol parsing, config, login limiting, token service, users file, and clip/core logic.


### Downloads and Releases

GitHub Releases provides two framework-dependent single-file archives. The .NET 10 Runtime must be installed on the target machine:

- `TextCascade.Server-<version>-windows-x64.zip`
- `TextCascade.Server-<version>-linux-x64.tar.gz`

Each archive contains the executable and config template; the Linux archive also includes the systemd unit. Every Release includes a SHA-256 checksum file.

Pushing a `v*.*.*` tag (for example `v0.3.0`) runs tests, builds both single-file archives, generates checksums, and publishes a GitHub Release. Pushes to `main` and pull requests run restore/build/test CI automatically.
### Production (systemd)

See `deploy/textcascade-server.service`:
- Run as the dedicated `textcascade` system user; create it with `useradd --system --home /opt/textcascade-server --shell /usr/sbin/nologin textcascade`.
- Managed by systemd with `Restart=on-failure` and hardening flags (`ProtectSystem`, `ProtectHome`, `PrivateTmp`).
- Config and `users.json` under `/etc/textcascade/`, runtime state under `/var/lib/textcascade/`, and binaries under `/opt/textcascade-server/`; set directory ownership to `textcascade:textcascade`.
- Token secret injected via `/etc/textcascade/textcascade.env`.

### License

See the repository LICENSE if present.

### Release & Maintenance Guidelines

- Before releasing any user-visible version, update CHANGELOG.md by moving the [Unreleased] section to the target version number and release date, along with the compare link at the bottom.
- Versioning adheres to [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html) and change records follow [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

