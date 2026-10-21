# Docker 部署

[English](#english) | [简体中文](#简体中文)

TextCascade Server 以 distroless 静态镜像分发。本文覆盖从首次部署到日常运维的全部细节；只想快速跑起来请看 README 的 Docker 一节。

---

## 简体中文

### 镜像概览

| 项 | 值 |
|---|---|
| 运行时基底 | `gcr.io/distroless/static-debian12:nonroot`（无 shell、无包管理器） |
| 镜像大小 | 约 11 MB |
| 运行用户 | UID/GID `65532` |
| 暴露端口 | `8443`（仅 HTTPS，无明文入口） |
| 架构 | `linux/amd64`、`linux/arm64`（见下方平台声明） |
| 数据位置 | `/etc/textcascade`（配置、`users.json`、证书）<br>`/var/lib/textcascade`（状态文件） |
| 默认配置 | `/opt/textcascade/textcascade.toml`（镜像内） |

**平台声明**：`linux/arm64` 镜像能够构建并发布，但作者没有 ARM Linux 设备，**未经实机运行验证**。在 ARM 上部署前请自行验证；如有问题请提 issue 并附 `docker inspect` 与日志。

### 目录布局

```
docker/
├── textcascade.env          # token secret，权限 600，绝不入库
└── config/                  # bind 到 /etc/textcascade
    ├── textcascade.toml
    ├── users.json           # 首次由 CLI 创建
    ├── certs/
    │   ├── server.pem
    │   └── server.key       # 同名 .key 边车
    └── ...
# /var/lib/textcascade 使用 named volume textcascade-state
```

`users.json` 与状态文件都采用「临时文件 + rename」原子写入，因此**配置目录必须以目录形式挂载**；单文件挂载会让容器内的文件监听（fsnotify）失效。

### 首次部署

```bash
# 仓库已提供 docker/config 骨架（含 textcascade.toml 与 certs/）。
cd /path/to/TextCascade-Server

# 1. TLS 证书：PEM 证书 + 同名 .key 边车（也支持无密码 .pfx/.p12）
cp /path/to/your/server.pem docker/config/certs/server.pem
cp /path/to/your/server.key docker/config/certs/server.key

# 2. 配置已在 docker/config/textcascade.toml，按需修改（路径须为容器内路径）

# 3. token secret：至少 32 字节，推荐 openssl rand -base64 48
cp docker/textcascade.env.example docker/textcascade.env
sed -i "s|^TEXTCASCADE_TOKEN_SECRET=.*|TEXTCASCADE_TOKEN_SECRET=$(openssl rand -base64 48)|" docker/textcascade.env
chmod 600 docker/textcascade.env

# 4. 目录属主必须与容器用户一致，否则写盘失败
chown -R 65532:65532 docker/config

# 5. 启动
docker compose up -d

# 6. 建第一个用户
docker exec -it textcascade-server /TextCascade.Server user add \
  --config /etc/textcascade/textcascade.toml --username alice
```

自签证书（仅测试环境）：

```bash
openssl req -x509 -newkey rsa:2048 -days 365 -nodes \
  -keyout docker/config/certs/server.key \
  -out docker/config/certs/server.pem \
  -subj "/CN=your-host"
chown 65532:65532 docker/config/certs/*
```

### 卷与权限

| 路径 | 类型 | 属主要求 | 说明 |
|---|---|---|---|
| `/etc/textcascade` | bind `./docker/config` | `65532:65532` | 需要宿主直接编辑；`users.json` 变更后自动热重载 |
| `/var/lib/textcascade` | named volume | 自动继承镜像属主 | 纯写入，无需宿主干预 |

容器以 `read_only: true` 运行，只有上述两个位置可写。若未来新增写路径，会立刻以运行时错误暴露，而不是静默失败。

启动时会校验两个目录存在且可写；不满足则退出码 1 并打印具体路径：

```
Configuration error: Directory '/var/lib/textcascade' for files.state_file is not writable: open ...: read-only file system
```

出现该报错时检查：卷是否挂载、`chown 65532:65532` 是否执行。

> **为什么这里报"不可写"而不是"不存在"**：镜像预建了 `/etc/textcascade` 与 `/var/lib/textcascade`（distroless 没有 mkdir），所以正常镜像里这两个目录总是存在。漏挂卷时它们仍然存在，只是 `read_only: true` 使其不可写——这正是自检要拦的情况。
>
> **务必保留 `read_only: true`。** 若去掉它又不挂 state 卷，目录落在容器可写层：自检通过、服务正常运行，但 `docker rm` 或重建后状态丢失。这是唯一无法被自检覆盖的形态，文档在此显式声明。

### 用户管理

镜像内没有 shell，但可以直接执行主程序：

```bash
# 交互式（会提示输入密码）
docker exec -it textcascade-server /TextCascade.Server user add \
  --config /etc/textcascade/textcascade.toml --username alice

# 脚本化：从 stdin 读密码
printf '%s\n' "$PASSWORD" | docker exec -i textcascade-server /TextCascade.Server user add \
  --config /etc/textcascade/textcascade.toml --username alice --password-stdin

# 其他子命令
docker exec textcascade-server /TextCascade.Server user list \
  --config /etc/textcascade/textcascade.toml
docker exec -it textcascade-server /TextCascade.Server user passwd \
  --config /etc/textcascade/textcascade.toml --username alice
```

可用子命令：`add`、`passwd`、`disable`、`enable`、`delete`、`revoke-tokens`、`list`、`hash`。

写入 `users.json` 后约 250ms 内服务自动重载，无需重启。CLI 与服务同时写时会争用 `users.json.lock`；CLI 重试 3 次后失败，稍后重试即可。

### token secret

`TEXTCASCADE_TOKEN_SECRET` 是签发和校验登录令牌的根密钥：

- 变更它会让**所有已签发的令牌立刻失效**，全部设备需要重新登录；
- 泄露它等同于可伪造任意用户身份，无需知道任何密码；
- 必须至少 32 字节，否则启动失败。

通过 `docker/textcascade.env` 注入。该值会出现在容器进程环境与 `docker inspect` 的输出中；能看到这些的前提是拥有宿主 root 或 docker 组权限，而这些人本就具备完全控制权。若你的威胁模型不接受这一点，可改用外部 secret 管理器渲染该文件后启动。

### 证书轮换

证书只在启动时加载一次。替换文件后重启容器即生效：

```bash
cp new/server.pem docker/config/certs/server.pem
cp new/server.key docker/config/certs/server.key
docker compose restart server
```

重启会造成一次秒级断连；客户端会通过恢复窗口自动续上。

certbot 自动化示例：

```bash
certbot renew --deploy-hook 'cp /etc/letsencrypt/live/example.com/{fullchain.pem,privkey.pem} /srv/textcascade/docker/config/certs/ && \
  mv /srv/textcascade/docker/config/certs/privkey.pem /srv/textcascade/docker/config/certs/server.key && \
  mv /srv/textcascade/docker/config/certs/fullchain.pem /srv/textcascade/docker/config/certs/server.pem && \
  docker compose -f /srv/textcascade/compose.yaml restart server'
```

### 反向代理与真实客户端 IP

登录限流按 `RemoteAddr` 计算来源 IP，**不支持 `X-Forwarded-For`**。

compose 示例直接发布 `8443:8443`（iptables DNAT），源 IP 得以保留，限流准确。

**不要在 HTTP 层反向代理（nginx `proxy_pass`、Caddy、Traefik）之后部署。** 那样所有登录都会被识别为来自代理 IP，`rate_limit.login_ip_per_minute` 会合并成对全体用户生效的单一额度，且不会报错。若确需域名或统一入口，请用四层转发（nginx `stream`、L4 负载均衡），它不解析 HTTP 因而保留源 IP。

### 备份与恢复

```bash
# 配置与用户表（bind）
tar -czf backup-config-$(date +%F).tar.gz -C docker config

# 运行状态（named volume）
docker run --rm -v textcascade-state:/data -v "$PWD":/backup alpine:3.23 \
  tar -czf /backup/backup-state-$(date +%F).tar.gz -C /data .
```

恢复时把配置解包回 `docker/config`（并重新 `chown -R 65532:65532`），状态卷解包回 `textcascade-state`，然后 `docker compose up -d`。

状态文件丢失的后果是版本号归零，客户端需要重新走一次恢复窗口，不影响用户与密码。

### 升级

```bash
docker compose pull
docker compose up -d
```

卷与配置不变。升级前建议先备份。回滚：把 `compose.yaml` 的 `image` 固定到旧版本标签后 `up -d`。

版本可用两种方式查看：

```bash
docker exec textcascade-server /TextCascade.Server --version
docker inspect --format '{{index .Config.Labels "org.opencontainers.image.version"}}' textcascade-server
```

### 从 systemd 迁移

两边的数据格式完全一致，直接搬文件即可：

| 原路径 | 新位置 |
|---|---|
| `/etc/textcascade/textcascade.toml` | `docker/config/textcascade.toml` |
| `/etc/textcascade/users.json` | `docker/config/users.json` |
| `/etc/textcascade/textcascade.env` | `docker/textcascade.env` |
| `/etc/cert/*.pem` | `docker/config/certs/` |
| `/var/lib/textcascade/textcascade.state.json` | named volume（可先跳过，让它重建） |

配置内的绝对路径要改成容器内路径（`/etc/textcascade/...`、`/var/lib/textcascade/...`）。

### 常见故障

| 现象 | 原因与处理 |
|---|---|
| `Directory '...' for files.state_file does not exist.` | 卷未挂载。检查 compose 的 `volumes` 是否包含 `textcascade-state`。 |
| `Directory '/etc/textcascade' for files.users_file is not writable` | 宿主配置目录属主不对。`chown -R 65532:65532 docker/config`。 |
| `TLS certificate file was not found.` | 证书路径不存在。确认 `docker/config/certs/server.pem` 存在，且 TOML 的 `certificate_path` 指向容器内路径。 |
| `TLS certificate must use .pem, .crt, .pfx, or .p12 extension.` | 扩展名不支持。 |
| `TLS PFX must include an unencrypted private key` | PFX 带密码，本服务不支持带密码 PFX。 |
| `Token secret from ... must be at least 32 bytes.` | `docker/textcascade.env` 内容不足 32 字节或变量名拼错。 |
| 登录一直 429 | 之前失败尝试触发了限流，等 1 分钟；或你部署在 HTTP 反代之后（见上文）。 |
| 刚建完用户就登录返回 401 | `users.json` 变更经约 250ms 防抖后才被 watcher 重载。稍等片刻重试即可，无需重启。 |
| 改了 `users.json` 不生效 | 挂载成了单文件而非目录，热重载失效。改为目录挂载。 |
| `Another TextCascade CLI process is running.` | 并发 CLI 争用锁，稍后重试。 |

### 镜像加速（中国大陆）

`ghcr.io` 与 `gcr.io` 在部分网络下不可达。两个基础镜像都可通过 `--build-arg` 换成镜像站：

```bash
# 拉取已发布镜像时使用镜像站
docker pull docker.m.daocloud.io/ghcr.io/long45343/textcascade-server:latest
docker tag  docker.m.daocloud.io/ghcr.io/long45343/textcascade-server:latest \
            ghcr.io/long45343/textcascade-server:latest

# 本地构建时同时替换 builder 与 runtime 基底
docker build \
  --build-arg GO_IMAGE=docker.m.daocloud.io/library/golang:1.27-alpine \
  --build-arg RUNTIME_IMAGE=<可用的 distroless 镜像地址> \
  -t textcascade-server .
```

Dockerfile 不含 `# syntax=` 指令，因此构建时不需要访问 Docker Hub 拉取前端镜像。

若两个基底都无法获取，可在能联网的机器上 `docker save` 后传输：

```bash
docker save textcascade-server:latest | gzip > tc-image.tar.gz
scp tc-image.tar.gz root@host:/tmp/
ssh root@host 'gunzip -c /tmp/tc-image.tar.gz | docker load'
```

---

## English

TextCascade Server ships as a distroless static image. This document covers everything from first deployment to day-two operations; for a quick run see the Docker section in README.

### Image overview

| Item | Value |
|---|---|
| Runtime base | `gcr.io/distroless/static-debian12:nonroot` (no shell, no package manager) |
| Image size | ~11 MB |
| Runtime user | UID/GID `65532` |
| Exposed port | `8443` (HTTPS only, no plaintext entry point) |
| Architectures | `linux/amd64`, `linux/arm64` (see platform notice) |
| Data locations | `/etc/textcascade` (config, `users.json`, certificates)<br>`/var/lib/textcascade` (state file) |
| Default config | `/opt/textcascade/textcascade.toml` (inside the image) |

**Platform notice**: the `linux/arm64` image builds and is published, but the author has no ARM Linux hardware and it has **not been verified on real devices**. Validate before deploying on ARM; if you hit problems, open an issue with `docker inspect` output and logs.

### Directory layout

```
docker/
├── textcascade.env          # token secret, mode 600, never committed
└── config/                  # bind-mounted to /etc/textcascade
    ├── textcascade.toml
    ├── users.json           # created by the CLI on first use
    ├── certs/
    │   ├── server.pem
    │   └── server.key       # same-name .key sidecar
    └── ...
# /var/lib/textcascade uses the named volume textcascade-state
```

Both `users.json` and the state file are written atomically via "temp file + rename", so **the config directory must be mounted as a directory**; a single-file mount breaks the in-container fsnotify watcher.

### First deployment

```bash
# The repo ships a docker/config skeleton (textcascade.toml plus certs/).
cd /path/to/TextCascade-Server

# 1. TLS certificate: PEM plus a same-name .key sidecar (password-less .pfx/.p12 also supported)
cp /path/to/your/server.pem docker/config/certs/server.pem
cp /path/to/your/server.key docker/config/certs/server.key

# 2. Config already lives at docker/config/textcascade.toml; adjust as needed (use container paths)

# 3. Token secret: at least 32 bytes; openssl rand -base64 48 is recommended
cp docker/textcascade.env.example docker/textcascade.env
sed -i "s|^TEXTCASCADE_TOKEN_SECRET=.*|TEXTCASCADE_TOKEN_SECRET=$(openssl rand -base64 48)|" docker/textcascade.env
chmod 600 docker/textcascade.env

# 4. Directory ownership must match the container user, or writes fail
chown -R 65532:65532 docker/config

# 5. Start
docker compose up -d

# 6. Create the first user
docker exec -it textcascade-server /TextCascade.Server user add \
  --config /etc/textcascade/textcascade.toml --username alice
```

Self-signed certificate (test environments only):

```bash
openssl req -x509 -newkey rsa:2048 -days 365 -nodes \
  -keyout docker/config/certs/server.key \
  -out docker/config/certs/server.pem \
  -subj "/CN=your-host"
chown 65532:65532 docker/config/certs/*
```

### Volumes and permissions

| Path | Type | Ownership | Notes |
|---|---|---|---|
| `/etc/textcascade` | bind `./docker/config` | `65532:65532` | Edited directly on the host; `users.json` changes reload automatically |
| `/var/lib/textcascade` | named volume | inherited from image | Write-only; no host intervention |

The container runs with `read_only: true`; only the two locations above are writable. A future write path will surface immediately as a runtime error rather than failing silently.

At startup both directories are verified to exist and be writable; otherwise the process exits with code 1 and names the offending path:

```
Configuration error: Directory '/var/lib/textcascade' for files.state_file is not writable: open ...: read-only file system
```

When you see that, check that the volume is mounted and that `chown 65532:65532` was applied.

> **Why "not writable" rather than "does not exist"**: the image pre-creates `/etc/textcascade` and `/var/lib/textcascade` (distroless has no `mkdir`), so those directories always exist in a stock image. With a missing volume they still exist but `read_only: true` makes them unwritable — exactly the case the check is meant to catch.
>
> **Always keep `read_only: true`.** Drop it while also omitting the state volume and the directory lands in the container's writable layer: the startup check passes and the service runs, but state is lost on `docker rm` or a rebuild. That is the one shape the check cannot cover, and it is called out here explicitly.

### User management

The image has no shell, but the main binary can be executed directly:

```bash
# Interactive (prompts for the password)
docker exec -it textcascade-server /TextCascade.Server user add \
  --config /etc/textcascade/textcascade.toml --username alice

# Scripted: read the password from stdin
printf '%s\n' "$PASSWORD" | docker exec -i textcascade-server /TextCascade.Server user add \
  --config /etc/textcascade/textcascade.toml --username alice --password-stdin

# Other subcommands
docker exec textcascade-server /TextCascade.Server user list \
  --config /etc/textcascade/textcascade.toml
docker exec -it textcascade-server /TextCascade.Server user passwd \
  --config /etc/textcascade/textcascade.toml --username alice
```

Available subcommands: `add`, `passwd`, `disable`, `enable`, `delete`, `revoke-tokens`, `list`, `hash`.

The service reloads `users.json` within about 250 ms of a change; no restart needed. Concurrent CLI and service writes contend on `users.json.lock`; the CLI retries three times and then fails, so retry shortly after.

### Token secret

`TEXTCASCADE_TOKEN_SECRET` is the root key used to sign and verify login tokens:

- changing it **invalidates every issued token immediately**, forcing all devices to log in again;
- leaking it is equivalent to the ability to forge any user's identity without knowing any password;
- it must be at least 32 bytes or startup fails.

It is injected through `docker/textcascade.env`. The value appears in the container's process environment and in `docker inspect` output; reading those requires host root or docker group access, which already implies full control. If your threat model rejects that, render the file from an external secret manager before starting.

### Certificate rotation

The certificate is loaded once at startup. Replace the files and restart the container:

```bash
cp new/server.pem docker/config/certs/server.pem
cp new/server.key docker/config/certs/server.key
docker compose restart server
```

The restart causes a brief disconnect measured in seconds; clients reconnect through the recovery window.

certbot automation example:

```bash
certbot renew --deploy-hook 'cp /etc/letsencrypt/live/example.com/{fullchain.pem,privkey.pem} /srv/textcascade/docker/config/certs/ && \
  mv /srv/textcascade/docker/config/certs/privkey.pem /srv/textcascade/docker/config/certs/server.key && \
  mv /srv/textcascade/docker/config/certs/fullchain.pem /srv/textcascade/docker/config/certs/server.pem && \
  docker compose -f /srv/textcascade/compose.yaml restart server'
```

### Reverse proxies and real client IP

Login rate limiting derives the source IP from `RemoteAddr` and **does not support `X-Forwarded-For`**.

The compose example publishes `8443:8443` directly (iptables DNAT), which preserves the source IP and keeps the limiter accurate.

**Do not deploy behind an HTTP-layer reverse proxy** (nginx `proxy_pass`, Caddy, Traefik). All logins would then appear to come from the proxy, and `rate_limit.login_ip_per_minute` would collapse into one shared quota for everyone — silently. If you need a domain or a unified entry point, use layer-4 forwarding (nginx `stream`, an L4 load balancer), which does not parse HTTP and therefore preserves the source IP.

### Backup and restore

```bash
# Config and user table (bind)
tar -czf backup-config-$(date +%F).tar.gz -C docker config

# Runtime state (named volume)
docker run --rm -v textcascade-state:/data -v "$PWD":/backup alpine:3.23 \
  tar -czf /backup/backup-state-$(date +%F).tar.gz -C /data .
```

To restore, unpack the config into `docker/config` (and re-run `chown -R 65532:65532`), unpack the state volume into `textcascade-state`, then `docker compose up -d`.

Losing the state file resets version numbers, so clients go through one recovery window; users and passwords are unaffected.

### Upgrading

```bash
docker compose pull
docker compose up -d
```

Volumes and config are untouched. Back up first. To roll back, pin `image` in `compose.yaml` to the previous tag and `up -d`.

The version is visible two ways:

```bash
docker exec textcascade-server /TextCascade.Server --version
docker inspect --format '{{index .Config.Labels "org.opencontainers.image.version"}}' textcascade-server
```

### Migrating from systemd

Both deployments share identical data formats; move the files across:

| Original path | New location |
|---|---|
| `/etc/textcascade/textcascade.toml` | `docker/config/textcascade.toml` |
| `/etc/textcascade/users.json` | `docker/config/users.json` |
| `/etc/textcascade/textcascade.env` | `docker/textcascade.env` |
| `/etc/cert/*.pem` | `docker/config/certs/` |
| `/var/lib/textcascade/textcascade.state.json` | named volume (may be skipped; it rebuilds) |

Rewrite absolute paths inside the config to the container paths (`/etc/textcascade/...`, `/var/lib/textcascade/...`).

### Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `Directory '...' for files.state_file does not exist.` | Volume not mounted. Check that `volumes` in compose includes `textcascade-state`. |
| `Directory '/etc/textcascade' for files.users_file is not writable` | Host config directory has the wrong owner. `chown -R 65532:65532 docker/config`. |
| `TLS certificate file was not found.` | Certificate path missing. Confirm `docker/config/certs/server.pem` exists and the TOML `certificate_path` points at the container path. |
| `TLS certificate must use .pem, .crt, .pfx, or .p12 extension.` | Unsupported extension. |
| `TLS PFX must include an unencrypted private key` | Password-protected PFX is not supported. |
| `Token secret from ... must be at least 32 bytes.` | `docker/textcascade.env` is shorter than 32 bytes or the variable name is misspelled. |
| Logins keep returning 429 | Earlier failures hit the limiter; wait a minute. Or you deployed behind an HTTP reverse proxy (see above). |
| 401 right after creating a user | The `users.json` change is reloaded by the watcher after a 250 ms debounce. Retry in a moment; no restart needed. |
| `users.json` edits do not take effect | Mounted as a single file instead of a directory, breaking the watcher. Use a directory mount. |
| `Another TextCascade CLI process is running.` | Concurrent CLI lock contention; retry shortly. |

### Registry mirror (mainland China)

`ghcr.io` and `gcr.io` are unreachable from some networks. Both base images can be redirected with `--build-arg`:

```bash
# Pull a published image through a mirror
docker pull docker.m.daocloud.io/ghcr.io/long45343/textcascade-server:latest
docker tag  docker.m.daocloud.io/ghcr.io/long45343/textcascade-server:latest \
            ghcr.io/long45343/textcascade-server:latest

# When building locally, redirect both the builder and runtime bases
docker build \
  --build-arg GO_IMAGE=docker.m.daocloud.io/library/golang:1.27-alpine \
  --build-arg RUNTIME_IMAGE=<reachable distroless image> \
  -t textcascade-server .
```

The Dockerfile carries no `# syntax=` directive, so a build never needs to reach Docker Hub for a frontend image.

If neither base is obtainable, transfer the image from a machine that has network access:

```bash
docker save textcascade-server:latest | gzip > tc-image.tar.gz
scp tc-image.tar.gz root@host:/tmp/
ssh root@host 'gunzip -c /tmp/tc-image.tar.gz | docker load'
```
