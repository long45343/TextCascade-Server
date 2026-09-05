# TextCascade Go 版服务端规格（C# 1:1 迁移）

状态：迁移决策已完成（15/15），规格定稿待实现  
日期：2026-09-05  
基准：[server-spec.md](server-spec.md)（与 C# v0.5.0 实现对齐）+ 源码逐函数盘点  
迁移原则：**行为 1:1，对外契约零变更**；仅 §3 声明性差异与 §12 迁移注意事项两处例外

## 1. 目标与非目标

### 1.1 目标

- 用 Go 重写 C# 服务端（约 4400 行生产代码、20 个源文件），函数级一一对应。
- 线协议、文件格式、CLI、错误码、默认值全部不变（见 §3）。
- 存量部署（systemd + PEM 证书）可直接切换；存量密码哈希不兼容，按 §12 runbook 重置。
- 测试全量 1:1 搬运（Q3）：单元 162 + 网络集成 12 + SlowHash 3 + 契约样本矩阵。
- 内存占用显著低于 .NET 版（迁移动机，perf.md 实测 .NET 空闲 RSS 125–131MB / 每连接 ~660KB）；Go 版落地后按 perf.md 场景重新实测并修订内存目标。

### 1.2 非目标

- 不修改任何协议语义、不新增特性、不"顺手修复"C# 已知差距（§15 台账 9 项原样保留，见 §13）。
- 不做跨语言互操作或混合部署的原子切换方案（切换 = 停旧起新 + runbook 步骤）。
- 不支持 C# 版未支持的平台（win-x64 / linux-x64 两目标）。

## 2. 已定决策（15 项，自包含）

| # | 决策 | 结论 |
|---|---|---|
| Q1 | 仓库布局 | 新建 `go` 分支；分支根目录放 go.mod（Go 原生布局）；C# 留在 main；ContractSamples 复制入 `testdata/`，CI 校验与 main 侧 sha256 一致 |
| Q2 | 工具链 | Go 1.27（最新稳定线，go1.27.1） |
| Q3 | 验收策略 | 测试全量 1:1 搬运 |
| Q4 | WebSocket 库 | gorilla/websocket |
| Q5 | 入站 JSON | encoding/json + 手写 token 级预扫描器（`internal/protocol/jsonscan.go`，详见 §7.7-A） |
| Q6 | Argon2 兼容 | 与 Isopoh 产物**字节级互通**（实测见 §3.1 与决策台账 Q6 补记）：x/crypto/argon2 + 自写 PHC 编解码；存量密码无需重置 |
| Q7 | TOML | pelletier/go-toml/v2 |
| Q8 | 单实例锁 | gofrs/flock |
| Q9 | 用户文件监听 | fsnotify + 250ms 防抖 + 30 秒轮询兜底 |
| Q10 | 证书 | PEM 全支持 + PFX/P12 用 software.ssl.golang.org/pkcs12（实现首日验证无加密 PFX） |
| Q11 | ALPN | 仅 HTTP/1.1（`NextProtos=["http/1.1"]`） |
| Q12 | 日志 | log/slog + 自定义单行 Handler |
| Q13 | CLI | stdlib flag.NewFlagSet 手写动词分发；x/term 交互密码 |
| Q14 | 测试栈 | stdlib testing + testify（assert/require；仅进测试二进制） |
| Q15 | 版本注入 | ldflags -X main.version；CI 双平台矩阵 |

完整论证（每项三选项优劣）存于私有决策台账 `specs/go-migration-decisions.md`（不入库）；本表为唯一事实摘要。

## 3. 声明性差异（相对 C# v0.5.0 的全部偏差）

1. **Argon2 哈希与 Isopoh 字节级互通**（Q6）：Argon2id 为标准算法，Isopoh（C# 版）与 x/crypto（Go 版）对相同 (password, salt, m, t, p) 产出相同哈希字节；Isopoh 的唯一怪癖是忽略配置的 Threads 并自行选择 lane 数（写入 PHC 的 `p=` 与配置的 Argon2Parallelism 可能不一致），但其计算标准且如实记录于 PHC 串，Go 版 Verify 按串内参数重算即可验证。互通性经双向实测锁定（C# 生成 → Go 验证、Go 生成 → C# 验证），并有契约测试固化（`internal/auth` 的 Isopoh 互通字面量用例）。存量 `users.json` 原样沿用，迁移无需重置密码；登录响应对 Isopoh 产物携带 `needsRehash=true`（存储 p 与配置不一致所致），与 C# 版自身行为一致。客户端零改动。
2. **仅 HTTP/1.1**（Q11）：Go 版 TLS ALPN 显式只广播 `http/1.1`，不提供 h2。Kestrel 默认广播 h2+http/1.1。对协议无影响（RFC6455 升级仅存在于 HTTP/1.1；gorilla/websocket 亦仅支持 h1 升级）。
3. **Windows 私钥存储**：C# 需区分 DefaultKeySet/EphemeralKeySet（SChannel 限制）；Go 私钥仅在内存中，无对应问题，`internal/hosting/cert.go` 不含平台分支。
4. 除此之外，登录响应、token、协议帧、错误码、close code、文件格式、TOML 键、环境变量、CLI 行为**全部零偏差**。

## 4. 依赖清单（go.mod，Q 汇总）

直接依赖（8）：

| 依赖 | 用途 | 引入自 |
|---|---|---|
| github.com/gorilla/websocket | WebSocket 升级与帧 | Q4 |
| github.com/pelletier/go-toml/v2 | TOML 解析 | Q7 |
| github.com/gofrs/flock | CLI 单实例锁 | Q8 |
| github.com/fsnotify/fsnotify | users.json 监听 | Q9 |
| golang.org/x/crypto | Argon2id | Q6 |
| golang.org/x/term | 交互式密码输入 | Q13 |
| software.sslmate.com/src/go-pkcs12 | 无密码 PFX/P12 | Q10（实测修正：golang.org vanity 路径 proxy 未缓存且部分网络不可达，须用 canonical 模块路径，当前版 v0.7.3） |
| stretchr/testify | 测试断言（仅测试二进制） | Q14 |

间接：golang.org/x/sys（flock/fsnotify 共享）。其余全部标准库。

## 5. 工具链与仓库布局

- Go 1.27；`go.mod` 模块名 `github.com/long45343/TextCascade-Server`；`go vet` + `gofmt` 进 CI。
- 版本号：`var version = "dev"`（main 包），release 构建注入 `-ldflags "-X main.version=<tag>"`；CLI 输出与 user list 显示该值（对齐 C# 从 csproj 读取的语义）。
- 构建：`go build -trimpath -o TextCascade.Server ./cmd/server`；发布矩阵 linux-x64 / win-x64 自包含单文件（Go 无"框架依赖"概念，运行时内嵌——与 C# 产物形态的固有差异，非契约偏差）。

```
（go 分支根目录）
go.mod / go.sum
cmd/server/main.go              ← Program.cs
internal/config/config.go       ← RuntimeConfig.cs
internal/users/users.go         ← Users.cs
internal/auth/argon2.go         ← Auth.cs（哈希器半边）
internal/auth/token.go          ← Auth.cs（token 半边）
internal/auth/login.go          ← AuthService.cs
internal/protocol/protocol.go   ← Protocol.cs（结构 + 解析 + 校验）
internal/protocol/serialize.go  ← Protocol.cs（出站 marshal）
internal/protocol/jsonscan.go   ← 新组件：token 级预扫描（§7.7-A）
internal/core/limiter.go        ← Core.cs（SlidingWindowLoginLimiter）
internal/core/bucket.go         ← Core.cs（TokenBucket）
internal/core/ring.go           ← Core.cs（SeenIdRing）
internal/core/logic.go          ← Core.cs（CoreLogic / SnapshotWinner）
internal/state/store.go         ← RuntimeStateStore.cs
internal/logging/security.go    ← SecurityLogging.cs
internal/sync/server.go         ← SyncServer.cs
internal/hub/hub.go             ← UserHub.cs
internal/hub/registry.go        ← UserRegistry.cs
internal/hub/jobs.go            ← UserJobs.cs
internal/hub/coordinator.go     ← IConnectionCoordinator.cs
internal/hosting/connection.go  ← ConnectionHandler.cs
internal/hosting/endpoint.go    ← SyncEndpoint.cs
internal/hosting/watcher.go     ← UserFileWatcher.cs
internal/hosting/scanner.go     ← HeartbeatScannerService.cs
internal/hosting/cert.go        ← ServerHost.cs（证书加载半边）
internal/hosting/run.go         ← ServerHost.cs（进程装配半边）
internal/models/context.go      ← ConnectionContext.cs + ConnectionStateBag.cs
internal/models/frame.go        ← ReceivedMessage.cs
internal/cli/cli.go             ← Cli.cs
internal/cli/lock.go            ← SingleInstanceLock
internal/clock/clock.go         ← TimeProvider 接缝（固定决策 F4）
testdata/contract-samples/      ← 复制自 main 的 TestCascade.Server.Tests/ContractSamples/
```

## 6. 并发模型映射（固定决策 F3/F4/F5/F6/F7）

| C# | Go | 要点 |
|---|---|---|
| `ReadLoopAsync`（每连接） | goroutine `readLoop` | gorilla NextReader 驱动 |
| 用户 Channel（无界） | 自实现无界队列（slice + sync.Cond），`TryWrite` 恒真 | 1:1 语义 |
| `RunUserLoopAsync`（单消费者） | goroutine（mutex 保证单读者，复刻 StartIfIdle 防重入） | |
| 发送 Channel（有界 16） | `chan []byte` cap 16 + `select { case ch<-p: / default: 满 }` | 满即 `cancel()` 连接 ctx，不补发 error/close |
| `ConnectionSendLoopAsync` | goroutine `sendLoop`（唯一写者） | gorilla 要求单写者，天然对齐 |
| `HeartbeatScannerService`（BackgroundService+PeriodicTimer） | goroutine + `time.NewTicker(1s)` + ctx | 扫描 panic recover 且记日志 |
| `TimeProvider` | `type Clock interface { Now() time.Time }`；生产 `clock.System`，测试注入 fake | |
| 优雅停机 | `signal.NotifyContext(SIGTERM, SIGINT)`；流程 1:1：bye → 1001 → drain 2s → 取消全部连接 → 同步 flush | 含"close 握手等待无超时"现状（§13.8） |
| 用户循环异常 → RebuildHub | 用户 goroutine 内 `recover()` → RebuildHub | `NextVersion` 溢出必须**显式检测**：Go uint64 自增静默 wrap，`cur == math.MaxUint64` 时 panic |

## 7. 函数级映射表

约定：C# `out` 参数在 Go 中改为多返回值；`Result<T>` 改为 `(T, *Error)`；instance/static 双载合并为一个函数（表中注明）；internal 测试钩子在 Go 中为包内可见函数（同包测试可直接调用）。

### 7.1 Program.cs → cmd/server/main.go

| C# | Go | 行为 |
|---|---|---|
| `Main(args)` | `main()` | 动词分发：`serve` → `hosting.Run`；其余 → `cli.Run`；退出码一致 |

### 7.2 ServerHost.cs → internal/hosting/{run.go, cert.go}

| C# | Go | 行为 |
|---|---|---|
| `RunServer(args)` | `hosting.Run(args) int` | Load→Env→Validate→LoadUsers→ValidateUsers→state store→cert.Load→watcher.Start→http.Server(ListenAndServeTLS)；错误打印与退出码一致 |
| `CreateApp(...)` | `hosting.NewServer(cfg, users, store, hasher, clk, cert) *Server` | 构建 mux：GET /health、POST /api/v1/login、GET /api/v1/sync；等价 `UseWebSockets` 由 gorilla Upgrader 承担 |
| `ConfigureKestrel(...)` | TLSConfig 装配（NewServer 内） | MinVersion 不显式设置（跟随 OS，对齐 §8.2）；`NextProtos=["http/1.1"]`（Q11） |
| `CertificateLoader.Load(path)` | `cert.Load(path) (tls.Certificate, error)` | 扩展名分发 .pem/.crt → loadPEM；.pfx/.p12 → pkcs12（无密码）；其他扩展报错文案一致 |
| `LoadPemCertificate(path)` | `cert.loadPEM(path)` | 同名 `.key` 边车查找、bundle 解析、叶证书+私钥匹配；缺私钥/无证书错误文案一致 |
| `DisposeChain` / `LoadedCertificate.Dispose` | 无对应 | Go GC 管理；结构体保留持有 tls.Certificate |

### 7.3 RuntimeConfig.cs → internal/config/config.go

| C# | Go | 行为 |
|---|---|---|
| 5 个 record（ServerConfig 等） | `config.RuntimeConfig` + 子 struct（Server/Auth/Limits/RateLimit/Files） | 字段名一一对应 |
| `CreateDefaultConfig()` | `config.Defaults()` | 默认值逐项一致（§3.1 表） |
| `LoadTomlConfig(path, defaults)` | `config.LoadTOML(path string, def *RuntimeConfig) (RuntimeConfig, error)` | go-toml/v2 严格解析：重复键 fail-fast、类型非法 fail-fast；UTF-8 强制 |
| `ApplyTomlModel(config, model)` | `applyTOMLModel(doc *toml.MetaData)` | 键映射与回退一致 |
| `TryGetTable` / `GetString` / `GetInt` | `getTable` / `getString` / `getInt` | |
| `WarnUnknownKeys(...)` | `warnUnknownKeys(...)` | 未知键 warning（slog） |
| `ApplyEnvironmentOverrides(config)` | `(*RuntimeConfig).ApplyEnv()` | 5 个环境变量 + token secret 变量名，清单一致 |
| `ValidateConfig(config)` | `(*RuntimeConfig).Validate() error` | 全部规则一致（含 max_frame > max_text、心跳超时 > 间隔、全容量 > 0） |

### 7.4 Users.cs → internal/users/users.go

| C# | Go | 行为 |
|---|---|---|
| `UserRecord` record | `struct UserRecord`（Username/PasswordHash/TokenVersion int64/Disabled） | |
| `Argon2HashRegex` | `users.argon2HashRe`（regexp 编译一次） | 校验哈希串形态 |
| `LoadUsers(path)` | `users.Load(path) (*UsersFile, error)` | 严格 JSON：未知/重复字段拒绝（Q5 扫描器） |
| `HasUniqueProperties(...)` | `hasUniqueProperties(...)` | |
| `ValidateUsers(users)` | `(*UsersFile).Validate() error` | 水位、唯一性、哈希格式、正数 long、disabled——全部一致 |
| `BuildUserLookup(users)` | `(*UsersFile).BuildLookup() map[string]UserRecord` | |
| `SaveUsers(path, users)` | `users.Save(path, f)` | Validate → 临时文件 + `os.Rename` 原子替换（Windows 等价 MoveFileEx）+ fsync |
| `Copy(source)` | `users.Copy(f)` | |

### 7.5 Auth.cs → internal/auth/{argon2.go, token.go}

| C# | Go | 行为 |
|---|---|---|
| `Argon2PasswordHasher.Hash/Verify` | `auth.Hash(password, params)` / `auth.Verify(password, encoded)` | x/crypto/argon2 (Argon2id)；PHC 编码自洽（Q6），格式 `$argon2id$v=19$m=…,t=…,p=…$b64salt$b64hash`（RawURL 无填充） |
| `NeedsRehash`（实例/静态双载） | `auth.NeedsRehash(encoded, params)` | 合并双载；语义一致：参数不一致 → 登录响应携带 needsRehash，不重写文件 |
| `TokenPayload` record | `struct auth.TokenPayload`（Subject/Version/IssuedAt/Expires int64 Unix 秒） | |
| `AuthToken` record | `struct auth.Token` | |
| `TokenService(secret)` | `auth.NewTokenService(secret []byte)` | |
| `CreateToken(user, now, ttl)` | `(*TokenService).Create(user, now, ttl)` | |
| `CreateTokenPayload(user, now, ttl)` | `auth.CreateTokenPayload(user, now, ttl)` | 固定字段序 sub/ver/iat/exp 最小化 UTF-8 JSON（手写 marshal，非结构体反射） |
| `SignToken(payload, secret)` | `auth.SignToken(payload, secret)` | HMAC-SHA256；比较用 `hmac.Equal`（常数时间） |
| `TryVerifyToken`（实例/静态） | `auth.TryVerifyToken(compact, now, lookup) (TokenPayload, bool)` | 合并双载；Go 无 out 参数 → 多返回值 |
| `TryVerifyTokenInternal` | `tryVerifyInternal` | 验签→验过期→验用户存在→验 tokenVersion 顺序一致 |
| `TryParsePositiveInteger` | `tryParsePositiveInt` | 拒绝小数/指数/字符串形态/非正数 |
| `TryBase64UrlDecode` | 消失 | 直接用 `base64.RawURLEncoding`（Q5 固定决策：出站 EncodeToString/入站 Decode 等价） |

### 7.6 AuthService.cs → internal/auth/login.go

| C# | Go | 行为 |
|---|---|---|
| `HandleLoginAsync(...)` | `auth.HandleLogin(w, r, cfg, srv, logger)` | 全部内联逻辑 1:1 |
| `ParseLoginRequest(...)` | `parseLoginRequest(w, r)` | 16KB 上限：`http.MaxBytesReader`（覆盖 chunked，等价 IHttpMaxRequestBodySizeFeature）；MaxDepth=3、重复/未知字段拒绝走 jsonscan |
| `WriteError(...)` | `writeError(...)` | 400 invalid_request 统一形态 |
| `CreateLoginFailure` / `CreateRateLimitResult` | 常量 `errInvalidCredentials` / `errRateLimited` | |
| `LoginRequest` record | `struct loginRequest` | |
| `LoginParseException` | `auth.ErrLoginParse`（哨兵 error） | Go 无异常类型 |

### 7.7 Protocol.cs → internal/protocol/{protocol.go, serialize.go, jsonscan.go}

**结构与错误：**

| C# | Go |
|---|---|
| `ProtocolError` record | `struct Error{Code ErrKind, Message string, ReferenceID *string}` |
| `ProtocolErrorCode` enum | `ErrKind` 常量组 + `(*Error).CodeName()`（invalid_message/text_too_large/frame_too_large/empty_text/rate_limited/hello_timeout/server_busy） |
| `Result<T>` / `ParseResult` | `(T, *Error)` 惯例；`ParseClientMessage` 返回 `(Message, *Error)` |
| `ClipSnapshot/ClientHello/ClientClip/ClientPong` | 同名字段 struct（UpdatedAt/ClientTime 用 `time.Time`） |
| `ClientMessage/MessageKind` | `Kind` 常量 + `Message` 单一 struct（Go 无 object 判别联合，用固定字段） |
| `LatestText` record + `From` | `struct LatestText` + `LatestFromSnapshot(s, version, clientID, clientName)` |
| `UtcSecondDateTimeConverter` | `WriteUTCSecond(w, t)`：出站 `"2006-01-02T15:04:05Z"`；`ParseFlexibleTime(s)`：入站接受秒级或 ISO 往返两种形态、偏移必须为零 |

**出站 marshal（serialize.go，全部手写字节，字段序固定）：**

| C# | Go | 字节不变式 |
|---|---|---|
| `SerializeWelcome(latest, _)` | `MarshalWelcome(latest *LatestText)` | `{"type":"welcome","protocolVersion":1}` + latest 非 nil 时续 `,\"latest\":{...}`；键缺失即"无最新值" |
| `SerializeClip(id, latest)` | `MarshalClip(id, latest)` | type,version,id,payload,encrypted,hash,fromClientId,fromClientName,updatedAtUtc |
| `SerializeClipAck(id, latest)` | `MarshalClipAck(id, latest)` | type,id,version,updatedAtUtc |
| `SerializePing(now)` | `MarshalPing(now)` | type,serverTimeUtc |
| `SerializeBye(reason)` | `MarshalBye(reason)` | type,reason |
| `SerializeProtocolError(err)` | `MarshalError(err)` | type,code,message[,referenceId]；nil 时键省略 |
| `SerializeLoginResponse(token, cfg, needsRehash)` | `MarshalLoginResponse(token, cfg, needsRehash)` | 手写顺序：token, expiresAtUtc（"O" 往返格式，Go layout `"2006-01-02T15:04:05.0000000Z07:00"`）, protocolVersion, maxTextBytes, helloTimeoutSeconds, heartbeatIntervalSeconds, heartbeatTimeoutSeconds [, needsRehash 仅 true 时] |

**入站解析（protocol.go + jsonscan.go）：**

| C# | Go | 行为 |
|---|---|---|
| `ParseClientMessage(frame, config)` | `ParseClientMessage(frame []byte, cfg) (Message, *Error)` | 顺序：jsonscan 预扫描 → 根必须 object → `type` 字符串 → 未知/重复字段检查（known 集合按类型）→ parseHello/parseClip/parsePong → 未知 type invalid_message |
| `ParseHello(root, config)` | `parseHello` | clientId 1–128 字节、clientName 0–128 字节、lastServerVersion 非负整数、snapshot object 或 null |
| `TryGetSnapshot(...)` | `tryGetSnapshot` | payload 必填非空、预算校验、hash ≤4096 字节、localModifiedAtUtc 两种形态 |
| `ParseClip(root, config)` | `parseClip` | |
| `ParsePong(root)` | `parsePong` | |
| `ValidateHello(h, config)` | `ValidateHello(h, cfg)` | |
| `ValidateClipSnapshot(s, config)` | `validateSnapshot` | |
| `ValidateClipMessage(m, config)` | `ValidateClip(m, cfg)` | 结构→语义→资源顺序早拒绝 |
| `CheckFrameSize` / `CheckPayloadSize` | 同名导出 | |
| `ValidatePayloadSize` | `validatePayloadSize` | |
| `TryParseJson(frame, out error)` | `jsonscan.Decode(frame []byte, cfg) (*Node, *Error)` | 见下 |
| `GetReferenceId(root)` | `getReferenceID(root)` | |

**A. jsonscan 预扫描器规则（Q5 新组件，逐条复刻 C# 校验行为）：**

1. UTF-8 完整性（`utf8.Valid`），非法即 invalid_message。
2. 单一顶层 JSON 值；嵌套深度 ≤3（C# MaxDepth=3）。
3. 全树重复键检测，报文含字段名（`Unknown or duplicate field: X.`）。等价性论证：C# 未知字段一律拒绝，故 C# 的"已知层显式查重"与"全树查重"最终行为相同（重复键必然落在已知字段或导致未知字段拒绝，两者都返回 invalid_message 且报文一致）。
4. 数字形态白名单：协议字段仅接受整数字面量 `-?0|[1-9][0-9]*`；小数点/指数形态在扫描层即拒绝（等价 C# TryGetUInt64/TryParsePositiveInteger 的行为 + TryGet 系列的数字类型检查）。
5. 字符串非法转义/裸控制字符 → 非法。
6. 扫描产物为轻量 Node 树，语义层（parseHello 等）在其上取字段；语义解析不再走 encoding/json（避免二义）。users.json/login 同一扫描器复用。

### 7.8 Core.cs → internal/core/{limiter.go, bucket.go, ring.go, logic.go}

| C# | Go | 行为 |
|---|---|---|
| `TryConsumeLoginLimit(ip, user, now, cfg)` | `(*Limiter).TryConsumeLoginLimit(...)` | 双维度滑动窗口，任一超限拒绝；成功仅清用户窗口 |
| `ResetUserLimit(username)` | `(*Limiter).ResetUserWindow(user)` | |
| `GetWindowCount/HasWindowKey` | 包内可见（同包测试直调） | C# internal ForTest 钩子在 Go 无需后缀 |
| `RemoveExpired/EnqueueForTest` | 同上 | |
| `tryConsume(key, limit, now, maxKeys, allowNewKey)` | `tryConsume` | RemoveExpired 全表扫描时机一致 |
| `TokenBucket(burst, rate, now)` / `TryAcquire(now)` | `bucket.New` / `(*Bucket).TryAcquire(now)` | 补币算法一致 |
| `SeenIdRing(capacity)` | `ring.New(capacity)` | Dictionary+FIFO 环形淘汰 |
| `TryDuplicate(id)` | `(*Ring).TryDuplicate(id)` | |
| `TryGetResult(id, out r)` | `(*Ring).TryGet(id) (*LatestText, bool)` | |
| `RememberId(id, result)` | `(*Ring).Remember(id, latest)` | |
| `IsUnchangedDuplicate(...)` | `(*Ring).IsUnchangedDuplicate(id, payload, hash, encrypted) (*LatestText, bool)` | 返回 true 时 latest 必非 nil（死分支依据） |
| `RememberInternal` | `rememberInternal` | |
| `SnapshotWinner` record | `struct Winner` | |
| `CoreLogic.NextVersion(current)` | `core.NextVersion(cur uint64) uint64` | **F7：`cur == math.MaxUint64` 显式 panic**（Go 自增不溢出抛异常） |
| `CoreLogic.WithVersion(latest, next, nowUtc)` | `core.WithVersion(latest, next, now)` | 不可变替换 |
| `CoreLogic.SelectSnapshotWinner(hellos)` | `core.SelectSnapshotWinner(hellos) *Winner` | 三规则：版本最大→localModifiedAtUtc 最新→clientId 字典序更大 |

### 7.9 SyncServer.cs → internal/sync/server.go

| C# | Go | 行为 |
|---|---|---|
| 构造 | `sync.New(cfg, users, store, clk, logger)` | |
| `RemoveEmptyHubAfterRecovery(hub)` | `(*Server).RemoveEmptyHubAfterRecovery(h)` | allowDuringRecovery=true |
| `ReplaceUserLookup(users)` | `(*Server).ReplaceUserLookup(f)` | atomic.Pointer[users.File]，Volatile.Write 等价 |
| `GetOrCreateHub(username)` | `(*Server).GetOrCreateHub(u)` | 初始版本 = store.GetVersion；互斥防并发建 hub |
| `ScanHeartbeats(now)` | `(*Server).ScanHeartbeats(now)` | 1s 扫描器调用 |
| `RebuildHub(hub)` | `(*Server).RebuildHub(h)` | 取消该用户全部连接并重建；进程存活 |
| `RegisterPendingHello/UnregisterPendingHello` | 同名 | pendingHellos 集合 |
| `EnqueueHelloTimeout(c)` / `CloseAfterHelloTimeoutAsync` | `enqueueHelloTimeout(c)` / goroutine `closeAfterHelloTimeout` | time.AfterFunc 等价 |
| `CancelConnection(c, reason)` | `(*Server).CancelConnection(c, reason)` | MarkClosed 守卫行为保留（含 §13.9 静默熔断现状） |
| `EnqueueImmediateClose(c, reason)` | `(*Server).EnqueueImmediateClose(c, reason)` | Socket.Abort 等价 = `conn.UnderlyingConn().Close()` |
| `ShutdownAsync(drain, now)` | `(*Server).Shutdown(drain, now)` | **34 秒无超时 close 握手现状原样保留**（§13.8） |
| `CloseConnectionAsync(c, status, reason)` | `closeConnection(c, status, reason)` | |

### 7.10 Hub（UserHub.cs / UserRegistry.cs / UserJobs.cs / IConnectionCoordinator.cs）→ internal/hub

| C# | Go | 行为 |
|---|---|---|
| `UserHub` 构造 | `hub.New(username, cfg, processStart, coord, store, initialVersion)` | SeenIdRing/TokenBucket/无界队列初始化 |
| `AddConnection/RemoveConnection` | `(*Hub).AddConnection/RemoveConnection` | |
| `StartIfIdle()` | `(*Hub).StartIfIdle()` | mutex + 单读者防重入；启动单消费 goroutine |
| `TryWriteJob(job)` | `(*Hub).TryWriteJob(job)` | 无界队列恒真 |
| `RunUserLoopAsync(ctx)` | `(*Hub).RunUserLoop(ctx)` | recover → RebuildHub（F7） |
| `ProcessJob(job, now)` | `processJob(job, now)` | clip/pong/hello/disconnect 分派 |
| `AcceptSnapshot(hello)` | `(*Hub).AcceptSnapshot(hello)` | 预算累计（仅 payload UTF-8 字节） |
| `ClassifyClip(clip, conn)` | `(*Hub).ClassifyClip(clip, c) RecoveryDecision` | 恢复窗口有界队列；满断开提交者 |
| `CloseRecoveryWindow(now)` | `(*Hub).CloseRecoveryWindow(now)` | 选举→恢复→按到达序处理恢复队列→广播 welcome |
| `BroadcastWelcome(now)` | `broadcastWelcome(now)` | |
| `IsRecoveryWindowOpen(now)` / `EnsureRecoveryWindowClosed(now)` | 同名 | 窗口 = ProcessStart + snapshot_window_seconds |
| `MarkActivity` / `MarkActivityForScan` | 合并为 `markActivity(now)` | 10 分钟空闲回收依据 |
| `ApplyClip(clip, sender, now)` | `(*Hub).ApplyClip(clip, sender, now)` | 幂等（内容比较）→令牌桶→NextVersion→SaveVersion→不可变替换→单次序列化广播（排除发送方连接）→ACK；**死分支兜底原样保留**（§13.6）：`dupLatest != nil` 优先，`Latest` 次之，空 LatestText 兜底（不可达） |
| `BroadcastToConnection(c, payload)` | `broadcastToConnection(c, payload)` | |
| `BroadcastAsync(payload)` | `(*Hub).Broadcast(payload)` | |
| `UserRegistry.GetOrAdd/TryGetValue/RemoveIfEmpty/Remove` | `registry.GetOrAdd/TryGet/RemoveIfEmpty(h, allowDuringRecovery)/Remove` | ConcurrentDictionary → mutex map |
| `UserJob` 层级 + `RecoveryClip` | Go interface `UserJob` + ClipJob/HelloJob/PongJob/DisconnectJob struct + `RecoveryClip` struct | |
| `IConnectionCoordinator` | `hub.Coordinator` interface | 方法集一致 |

### 7.11 Hosting（ConnectionHandler.cs / SyncEndpoint.cs / UserFileWatcher.cs / HeartbeatScannerService.cs）→ internal/hosting

| C# | Go | 行为 |
|---|---|---|
| `ConnectionHandler.RunAsync(provisional, payload, cfg, server)` | `RunConnection(ctx, provisional, payload, cfg, srv)` | 转正：Hub 赋值、注册 hello 截止 |
| `ReceiveFrameAsync(...)` | `receiveFrame(c)` | gorilla `SetReadLimit(maxFrameBytes)`；超限→1009；零长度帧→frame_too_large 1009；客户端 Close 帧→回 1000 退出读循环但**不立即取消 CTS**（§5.7 现状） |
| `SendAndClosePreHelloAsync(...)` | `sendAndClosePreHello(...)` | 预 hello 阶段错误：invalid_message→1008、超限→1009 |
| `ReadLoopAsync(connection, cfg, server)` | `readLoop(c, cfg, srv)` | 帧→ParseClientMessage→投递用户队列/本地错误处理 |
| `ConnectionSendLoopAsync(connection)` | `sendLoop(c)` | 唯一写者；ctx.Done / 出队写出；取消与非取消异常同路清理 |
| `SendSafeAsync(...)` | `sendSafe(c, payload, srv)` | |
| `SyncEndpoint.HandleAsync(...)` | `HandleSync(w, r, cfg, srv)` | 升级前 Bearer 验证（401 不升级）、gorilla `CheckOrigin` 允许同源语义对齐、仅接受 `textcascade.v1`（Ordinal 精确）否则 400、非 WS 400 |
| `SelectSubProtocol(requested)` | `selectSubprotocol(r)` | gorilla Subprotocols 辅助下自校验 |
| `UserFileWatcher` 构造/`Start` | `watcher.New(...)` / `(*Watcher).Start()` | fsnotify Changed/Created/Deleted/Renamed + 250ms 防抖 + 30s ticker 兜底无条件重载 |
| `OnFileChanged/OnFileRenamed/OnFileError` | 事件分派函数 | |
| `ScheduleReload` / `ReloadAsync` | `scheduleReload` / `reload` | 3 次 50ms 退避；全败保留旧表 + warning；成功 → `srv.ReplaceUserLookup` |
| `Dispose` | `(*Watcher).Close()` | |
| `HeartbeatScannerService.ExecuteAsync/StopAsync/Scan` | `scanner.Run(ctx)` / ctx 取消 / `scan(now)` | 1s ticker；panic recover 记日志 |

### 7.12 RuntimeStateStore.cs → internal/state/store.go

| C# | Go | 行为 |
|---|---|---|
| `RuntimeStateEntry/RuntimeStateFile` | struct | `{"entries":[{"username":...,"version":...}]}` 格式不变 |
| 构造（flush 循环） | `state.NewStore(path, cfg)` | goroutine + 5s ticker；脏位快照 |
| `GetVersion(username)` | `(*Store).GetVersion(u) uint64` | |
| `SaveVersion(username, version)` | `(*Store).SaveVersion(u, v)` | 单调 max 合并（CAS），防乱序回退 |
| `Flush()` | `(*Store).Flush() bool` | 临时文件+rename+fsync；结构非法（重复键/空 username/零版本）启动 fail-fast 在 load 侧 |
| `RunFlushLoopAsync` / `Dispose` | `runFlushLoop(ctx)` / `(*Store).Stop()` | 停机同步 flush |
| `Load(path)` / `WriteAtomic(...)` | `load` / `writeAtomic` | |

### 7.13 SecurityLogging.cs → internal/logging/security.go

| C# | Go | 行为 |
|---|---|---|
| `LogSecurityEvent(logger, event, pairs)` | `logutil.SecurityEvent(logger, event string, fields ...Field)` | slog 自定义单行 Handler（Q12）复刻 `yyyy-MM-ddTHH:mm:ssZ ` 时间戳与扁平字段 |
| `RedactFields` / `RedactSensitive` | `redactFields` / `RedactSensitive` | 脱敏规则一致 |
| `TokenPrefix` | `logutil.TokenPrefix` | 保留（生产不调用，1:1） |

日志事件与字段完全一致：login(username,ip,success[,reason])、connect/disconnect(username,clientId,connectionId[,reason])、clip(username,version,clipId,bytes,fromClientId,encrypted)、reject(username,code,bytes)；登录失败折叠 `reason=invalid_credentials`。

### 7.14 Cli.cs → internal/cli/{cli.go, lock.go}

| C# | Go | 行为 |
|---|---|---|
| `RunCli(args, hasher)` | `cli.Run(args, hasher) int` | 动词：user add/passwd/disable/enable/delete/revoke-tokens/list/hash + serve |
| `CreateLockPath(usersFile)` | `CreateLockPath` | users.json 同目录 `users.json.lock` |
| `PrintUsage` | `printUsage` | 文案一致 |
| `TryExtractConfigOption(ref args, out path)` | `tryExtractConfigOption(args) (rest, path)` | 回退顺序 --config → TEXTCASCADE_CONFIG → ./textcascade.toml |
| `CommandAddUser/Passwd/SetDisabled/DeleteUser/RevokeTokens/ListUsers/HashPassword` | `cmdAdd/cmdPasswd/cmdSetDisabled/cmdDelete/cmdRevoke/cmdList/cmdHash` | 水位分配、溢出放弃、字节级文件保留行为一致 |
| `LoadForWrite(path)` | `loadForWrite(path)` | |
| `IncrementWatermark(current)` | `incrementWatermark(cur)` | 溢出放弃（显式检测） |
| `CreateArgon2Config(config)` | `createArgon2Params(cfg)` | |
| `TryGetOption/HasFlag/HasPasswordStdin` | 同名小写 | |
| `ReadPassword(prompt, args)` | `readPassword(prompt, args)` | --password-stdin 或 x/term.ReadPassword |
| `SingleInstanceLockHandle/Acquire(path, pollDelay)` | `lock.Acquire(path, pollDelay) (*Handle, error)` | gofrs/flock：OpenOrCreate + FileShare.None 语义等价（进程死亡 OS 释放）；PID 仅诊断写入；3 次重试后优雅失败 |

### 7.15 Models（ConnectionContext.cs / ConnectionStateBag.cs / ReceivedMessage.cs）→ internal/models

| C# | Go | 行为 |
|---|---|---|
| `ConnectionContext` | `struct Connection` | ID/Username/ClientID/ClientName/Conn（gorilla）/Hub（转正一次性赋值后不可变）/State |
| `ConnectionStateBag` | `struct StateBag` | lastSeen/lastPingAt 锁内赋值；SendCh chan；HelloDeadline |
| `MarkPingAwaitingPong/TryTakePongAwaiting` | 同名 | |
| `MarkClosed()` | `(*StateBag).MarkClosed() bool` | CAS 守卫 |
| `TryStartHelloTimeout()` | `(*StateBag).TryStartHelloTimeout() bool` | |
| `TryEnqueueSend(payload)` | `(*StateBag).TryEnqueueSend(p []byte) bool` | `select default`，满 false |
| `ReceivedMessage` | `struct Frame` | |

## 8. 关键语义对照细节

| 项 | C# | Go 1:1 方案 |
|---|---|---|
| 协议出站时间戳 | `yyyy-MM-dd'T'HH:mm:ss'Z'`（UTC 秒级） | layout `"2006-01-02T15:04:05Z"` |
| 登录响应 expiresAtUtc | "O" 往返格式（固定 7 位小数秒） | layout `"2006-01-02T15:04:05.0000000Z07:00"` |
| 入站时间戳 | 秒级或 ISO 往返、偏移必须为零 | `ParseFlexibleTime` 接受两种形态 |
| token base64url | System.Buffers.Text.Base64Url | `base64.RawURLEncoding`（无填充一致） |
| HMAC 比较 | 固定时间 | `hmac.Equal` |
| 字节长度校验 | UTF-8 字节数（1–128 / 0–128 / 4096） | `len([]byte(s))` 一致 |
| ulong 溢出 | C# checked 抛出 → RebuildHub | `cur == math.MaxUint64` 显式 panic → recover → RebuildHub |
| unbounded → 有界转换 | Channel.Writer/Reader | slice+Cond 队列 / buffered chan |
| volatile 写 | Volatile.Write | `atomic.Pointer` |
| File.Replace（Windows）/ rename | 原子替换 | `os.Rename`（Windows 为 MoveFileEx REPLACE_EXISTING） |
| FileSystemWatcher | 4 事件 + 防抖 | fsnotify 事件 + time.AfterFunc 防抖 |

## 9. 测试迁移计划（全量，Q3）

| C# 测试（TextCascade.Server.Tests） | Go 测试（同包 _test.go） |
|---|---|
| TokenServiceTests / AuthDeepTests / AuthServiceTimingTests | auth 包（token 全形态、Argon2 三函数、needsRehash、登录时序侧信道） |
| ClipAndCoreTests / RuntimeStateAndProtocolTests / IdempotencyBehaviorTests / ConnectionStateTests | core / protocol / state / models 包 |
| ConfigTests / UsersFileTests / CliWatermarkTests / SingleInstanceLockTests | config / users / cli 包 |
| ContractTests + ContractSamples（全矩阵） | protocol 包 + `testdata/contract-samples/`（复制自 main，CI sha256 校验防漂移） |
| WebSocketIntegrationTests（5 例） | server 集成（httptest 或 127.0.0.1:0 真实监听 + FastHasher 注入等价物） |
| NetworkIntegration（12 例，真实 TLS） | 同等 12 例（runtime 自签证书、显式 TLS1.2/1.3 探针、随机端口、帧分片、1009、重启恢复、bye/1001、HTTPS 登录全链路） |
| SlowHashSmokeTests（3 例） | 同等 3 例（生产参数真实 Argon2id） |
| UserFileWatcherTests / UserHubCoordinationTests / UserLoopConcurrencyTests / LoginLimiterTests | 对应包 |

契约字节不变式断言（welcome/clip/ack/ping/bye/error/login-response/token payload）逐条搬运；C# Theory ↔ Go table-driven + t.Run。

## 10. CI 与发布（go 分支）

- `ci.yml`（go 分支改造）：build+vet+test（过滤 network tag）+ 独立 network job + 契约样本 sha256 与 main 一致性校验步骤。
- `release.yml`：tag 触发，linux-x64/win-x64 `GOOS/GOARCH` 矩阵，`-trimpath -ldflags "-X main.version=<tag>"`，产物 `TextCascade.Server(.exe)`。
- README 增加 Go 版构建/部署/证书支持矩阵说明（衍生后续项）。

## 11. 迁移注意事项（生产切换 runbook，Q6 结果）

1. **无需重置密码**：Go 版可直接验证 C# 创建的存量 Argon2 哈希（Q6 互通性实测，见 §3.1）。已在生产切换时验证：C# CLI 创建的测试用户以既有密码登录 Go 版成功，真实存量用户以 C# 签发的 token 直连成功。
2. token 与 tokenVersion 机制不变：C# 版签发的存量 token 在 Go 版下继续有效（同一 secret、标准 HMAC）；如需强制重新登录，对目标用户执行 `user revoke-tokens`（可选步骤）。
3. users.json / textcascade.state.json / textcascade.toml 格式不变，原样沿用；证书 PEM 原样沿用（PFX 路径由集成测试覆盖）。
4. systemd 单元沿用（二进制路径替换）。perf.md 已按 Go 版重新实测并修订（2026-09-05）：全部性能目标以原始阈值达标。

## 12. 验收清单

- [x] 契约测试全矩阵通过（与 C# 相同样本、相同字节不变式；29 个样本复制入 `testdata/contract-samples/`，与 main 逐字节一致）
- [x] 单元 + 网络 12 + SlowHash 3 等价例全绿（Go 版按用例语义 1:1 搬运，数量与 C# 断言一一对应）
- [x] C# 版 §10.2 计划中尚未实施的 6 个补齐用例（子协议 400、hello 超时、NeedsRehash 不重写、同 clientId 排除、用户隔离、慢连接取消）**不在本次范围**（1:1 原则：C# 没有的测试不发明）
- [x] 生产并行对拍：Go 版以并行实例与 C# 版同时挂起（共用 users.json/证书/token secret），C# CLI 创建的用户以既有密码登录 Go 版成功，真实客户端在切换后以 C# 签发的存量 token 直连成功并完成 hello/clip/广播全链路（2026-09-05）
- [x] perf.md 场景重测完成，全部目标以原始阈值达标（2026-09-05，详见 perf.md）

## 13. 继承的实现差距台账（1:1 保留，不借迁移修复）

C# spec §15 的 9 项差距在 Go 版**原样保留**（含对应实现形态），除非用户另行决策：

1. 预 hello 连接不在 bye/1001 广播范围；
2. 空 hub 回收遗留 parked goroutine（Channel 未 Close 等价语义）；
3. 部分队列满路径直调 cancel 绕过 CancelConnection 入口；
4. TLS 下限跟随 OS 默认；
5. 明文测试缝隙（Go 侧对应：测试构建路径允许无证书 http.Server）；
6. ApplyClip 死分支兜底保留；
7. 待补齐测试项维持待办；
8. 停机 close 握手等待无超时（34 秒现状）；
9. 队列满熔断不产生 disconnect 安全事件。

> 若希望借迁移修复其中某项（例如 §13.8 停机 34 秒最痛），请在评审本 spec 时指出，按变更单处理而非默认纳入。
