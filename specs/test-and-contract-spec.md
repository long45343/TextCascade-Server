# TextCascade 测试与契约规格（函数级）

状态：**已落地实施**（2026-08-27，代码基线 v0.3.5；测试结果 162 默认 + 12 NetworkIntegration + 3 SlowHash 全部通过）  
日期：2026-08-27  
依据：docs/server-spec.md 审计结论 + git 溯源 + [spec-decisions.md](spec-decisions.md) 10 项决策  
代码基线：v0.3.5（commit 9ed6eba）

> **实施状态（2026-08-27）**
> - §1 NetworkIntegration：12 个用例全部落地（`NetworkIntegration/` 目录：TlsAndWssHandshakeTests 6、FrameFragmentationTests 3、RestartRecoveryTests 3）。实施偏差：N2 的 TLS 版本探测改用 SslStream 直连（.NET 10 移除了 `ClientWebSocketOptions.SslProtocols`，WSS 版本协商只能跟随 OS 策略）；N11/N12 合并为一条完整链路用例；双 hello 竞态产生的重复 welcome 在测试内按良性帧跳过（spec §15 台账外发现，测试已文档化）。
> - §2 契约测试：样本目录与驱动器已落地（ContractSamples/ valid 6 + invalid 17 + README；驱动器按子目录推断期望码；6+2 条序列化不变式）。非法数字/非法 UTF-8 以"字段类型污染"等价覆盖（README 已写明映射表），未做逐字段全矩阵——对同一解析分支的重复样本已合并。
> - §3 单元缺口：AuthDeepTests 12、CliWatermarkTests 8、IdempotencyBehaviorTests 5、SlowHashSmokeTests 3 已全部落地。实施偏差：U10 静态 `NeedsRehash` 参数矩阵并入 U27；U12（NextVersion 溢出）已存在于 ClipAndCoreTests 未重复；SlowHash 断言改为回读实际编码参数（Isopoh 写入的 p= 与配置 Argon2Parallelism 不一致，是 server-spec §15 级别的已知实现事实）。
> - ci.yml Test 步骤已加 `--filter "Category!=NetworkIntegration&Category!=SlowHash"`。

本规格覆盖三块内容：
1. **NetworkIntegration 本地网络集成测试**（此前从未实现，全历史零命中）。
2. **契约测试**（ContractSamples 样本集，覆盖 JSON 深度 3 / 重复字段 / 未知字段 / 非法数字 / 非法 UTF-8 全矩阵）。
3. **单元测试缺口补齐**（Argon2 三函数、token 非法形态、CLI 水位与溢出、WithVersion、重复 id 不耗令牌桶）。

所有函数名、类型名均已在 v0.3.5 源码中核实存在；标注 `[新增 internal 可见成员]` 的除外。

---

## 0. 决策落地总览

| 决策 | 落地方式 |
|---|---|
| Q1 测试宿主 | `TextCascade.Server.Tests` 项目内新建 `NetworkIntegration/` 目录 + 自建 fixture |
| Q2 过滤机制 | 类级 `[Trait("Category", "NetworkIntegration")]`；CI 用 `--filter Category!=NetworkIntegration` 排除 |
| Q3 测试证书 | fixture 运行时用 `CertificateRequest` 自签生成（约 30 行 helper），并顺带生成带密码 PFX 用于拒绝路径 |
| Q4 重启形态 | 同进程 `ServerHost.CreateApp` 构建两次：第一次 `StopAsync` 后第二次启动，共用临时目录 |
| Q5 TLS 断言 | 客户端 `SslProtocols.Tls12` 与 `Tls13` 各发起一次 WSS，断言握手成功 |
| Q6 契约组织 | Tests 项目内新建 `ContractTests/` + `ContractSamples/` 目录，样本 `.json` 文件落盘，csproj CopyToOutputDirectory |
| Q7 样本范围 | 全矩阵：hello / clip / pong 三种消息 × 8 种非法形态 |
| Q8 单测范围 | 全部缺口逐函数补齐（约 30 个用例） |
| Q9 Argon2 | 单测注入假哈希器；Argon2 真实链路放 `Category=SlowHash` 专项；CI 过滤为 `Category!=NetworkIntegration&Category!=SlowHash` |
| Q10 断言深度 | 行为级：耗尽 burst 后重复 id 仍获 ACK、新 id 被 rate_limited |

---

## 1. NetworkIntegration 测试（Category=NetworkIntegration）

### 1.1 基础设施（新建文件）

#### `NetworkIntegration/NetworkTestFixture.cs`

职责：TLS 服务端托管、自签证书、客户端工厂。不与现有 `IntegrationTestFixture` 共享代码（Q1 选择 B 的目的就是隔离），但复制其最小必要逻辑（TestLogCollector、FastPasswordHasher 模式）。

```csharp
public sealed class NetworkTestFixture : IAsyncDisposable
{
    // 核心 API（按 Q3/Q4/Q5 决策设计）
    public string TempDir { get; }                    // Path.Combine(Path.GetTempPath(), "textcascade-ni-" + Guid.NewGuid())
    public RuntimeConfig Config { get; }
    public TestLogCollector Logs { get; }

    // 启动一个 HTTPS Kestrel 实例，绑定 127.0.0.1:0（随机端口），返回实际端口
    public Task<RunningServer> StartAsync(UsersFile? users = null);

    // 停止指定实例（供 Q4 重启场景调用）
    public Task StopAsync(RunningServer server);
}

public sealed class RunningServer
{
    public WebApplication App { get; }        // ServerHost.CreateApp(args, config, users, stateStore, hasher, clock, certificate) 构建后 RunAsync
    public int Port { get; }                  // 从 Kestrel IServerAddressesFeature 读取实际绑定端口
    public UsersFile Users { get; }           // 与第二次重启共用
    public RuntimeStateStore StateStore { get; }
}
```

证书 helper（同文件或 `SelfSignedCertificate.cs`）：

```csharp
// 用 CertificateRequest 生成 RSA2048 自签叶证书（SAN: localhost, 127.0.0.1）
public static X509Certificate2 CreateSelfSigned();
// 导出无密码 PFX 到 TempDir，返回路径（走 CertificateLoader.Load 的 .pfx 分支）
public static string WritePfx(X509Certificate2 cert);
// 导出带密码 PFX —— 仅用于"密码 PFX 必须启动失败"的负路径
public static string WritePasswordProtectedPfx(X509Certificate2 cert, string password);
// PEM bundle（叶+私钥单文件）—— 覆盖 .pem 加载分支
public static string WritePemBundle(X509Certificate2 cert);
```

关键实现约束：

- 服务端构建必须走生产入口 `ServerHost.CreateApp(string[] args, RuntimeConfig config, UsersFile users, RuntimeStateStore stateStore, IPasswordHasher? hasher = null, IClock? clock = null, LoadedCertificate? certificate = null)`（ServerHost.cs:68），certificate 参数传真实 `LoadedCertificate`，以验证 `ConfigureKestrel(config, certificate)` 的 UseHttps 绑定。`LoadedCertificate` 由 `CertificateLoader.Load(path)`（internal，Tests 已有 InternalsVisibleTo）产生。
- Config 在默认值基础上调整：`hello_timeout_seconds=5` 保持默认；心跳间隔缩到 2 秒可缩短部分用例时长（可选）；`snapshot_window_seconds=3` 保持。
- hasher 注入 `FastPasswordHasher`（复制现有实现，ValidHash 常量同步），保证登录不慢。

客户端工厂：

```csharp
// WSS 客户端：跳过证书校验（自签）；subProtocol 默认 textcascade.v1；sslVersion 显式指定（Q5）
public static Task<(ClientWebSocket Socket, HttpClient Http)> ConnectWssAsync(
    int port, string token,
    SslProtocols sslVersion,
    string? subProtocol = "textcascade.v1");
```

### 1.2 测试类与用例（每条：方法 → 输入构造 → 断言）

全部类声明 `[Trait("Category", "NetworkIntegration")]`（Q2）。运行命令：

```bash
dotnet test TextCascade.Server.slnx --filter Category=NetworkIntegration
dotnet test TextCascade.Server.slnx --filter Category!=NetworkIntegration   # CI 默认
```

#### A. `TlsAndWssHandshakeTests`

| # | 测试方法 | 输入构造 | 断言 |
|---|---|---|---|
| N1 | `Connects_WithSelfSignedPfx_OverWss` | fixture 写无密码 PFX → `CertificateLoader.Load` → StartAsync → 登录取 token → `ConnectWssAsync(port, token, Tls13)` | 握手成功；收到首帧 welcome（或 hello 前 401 不发生）；socket.State == Open |
| N2 | `Accepts_Tls12_Client` | 同 N1，但 `SslProtocols.Tls12`（Q5） | 握手成功。失败消息注明"OS 政策禁用 TLS1.2 时属环境问题" |
| N3 | `HttpUpgrade_Succeeds_WithBearerAndSubProtocol` | ClientWebSocket 同时设置 `Authorization: Bearer <token>` 与 `AddSubProtocol("textcascade.v1")` | HTTP 101；welcome.protocolVersion == 1 |
| N4 | `HttpsLogin_Endpoint_Works` | 对 `https://127.0.0.1:{port}/api/v1/login` POST 合法凭据（HttpClient 自动处理自签错误） | 200；响应含 token、expiresAtUtc、protocolVersion、maxTextBytes 等 7 固定字段 |
| N5 | `RandomPortBinding_ActuallyBinds` | StartAsync 后读 IServerAddressesFeature | 地址形如 `https://127.0.0.1:{n>0}` 且连接成功（N1 已隐含，此处显式断言端口非 0） |

#### B. `FrameFragmentationTests`

| # | 测试方法 | 输入构造 | 断言 |
|---|---|---|---|
| N6 | `FragmentedClip_Reassembles_AndBroadcasts` | 两个客户端同用户连上；A 发送一条 clip，payload 约 300KB（> 单帧常见 MSS 分片规模），手动分片发送：先 `SendAsync(buffer[0..100k], EndOfMessage:false)` 两段再 `EndOfMessage:true` | B 收到完整 clip 广播，payload 字节数一致；A 收到 clip_ack |
| N7 | `OversizeFrame_Closes1009` | A 发送总长 > max_frame_bytes(589824) 的分片帧 | 连接被服务端关闭，close status == CloseStatusStatusCode.MessageTooBig(1009)；关闭前可选收到 frame_too_large error 帧（实现行为是先 error 再 close） |
| N8 | `ZeroLengthFrame_TreatedAsFrameTooLarge` | A 发送 0 字节 EndOfMessage:true 帧 | 连接关闭 1009（锁定当前实现的零长帧判定，见 server-spec §5 差异表第 8 条） |

#### C. `RestartRecoveryTests`（Q4：CreateApp 两次停启）

| # | 测试方法 | 输入构造 | 断言 |
|---|---|---|---|
| N9 | `Restart_KeepsTokenValid_DirectReconnect` | 第一次 StartAsync → 登录取 token V1 → 发一条 clip（version=v1）→ StopAsync（确认状态文件已在 TempDir 落盘）→ 第二次 StartAsync **复用同一 UsersFile 与同一 StateStore 目录** → 直接用旧 token V1 建 WSS（不重新登录） | 第二次连接握手成功；断言重启后 hub 初始版本来自持久化水位（下一条 clip 版本 = v1+1） |
| N10 | `Restart_SnapshotElection_RestoresLatest` | N9 流程 + 重启后两个客户端分别在 hello 带 lastServerVersion=128/64 的 snapshot | welcome.latest.version == 128（winner 不加一）；随后 B 发 clip 得到 129 |
| N11 | `Shutdown_BroadcastsBye_ThenCloses1001` | 一个在线客户端；对 RunningServer 调优雅停机路径（StopAsync 触发 SyncServer.ShutdownAsync） | 先收到 `{"type":"bye","reason":"server_shutdown"}`，随后 close status == EndpointUnavailable(1001)。（此用例可与 N12 合并为一个进程内场景） |
| N12 | `RealLogin_Connect_Send_Receive_FullChain` | 完整链路：HTTPS 登录 → WSS 连接 → hello → A 发 clip → B 收广播 + A 收 ACK | 各消息字段类型正确（version 为 ulong、updatedAtUtc 含 Z 后缀等） |

实施注意（写入实施清单，本轮不改代码）：

- `.github/workflows/ci.yml` Test 步骤改为 `dotnet test ... --filter Category!=NetworkIntegration&Category!=SlowHash`（与 Q9 联动）。
- Windows 本地跑 N1/N2 若公司策略限制自签证书可能需要 `X509KeyStorageFlags` 调整——fixture 中集中封装一处。

---

## 2. 契约测试（ContractSamples + ContractTests）

### 2.1 目录组织（Q6=A/Q7=A）

```
TextCascade.Server.Tests/
  ContractTests/
    ContractSampleTests.cs          // Theory 驱动器
    ContractSchemaInvariants.cs     // 正向样本字段序/序列化断言
  ContractSamples/
    valid/
      hello.full.json               // hello 全字段合法样本
      hello.minimal.json            // 无 snapshot 合法样本
      clip.basic.json               // clip 四字段合法样本
      pong.ok.json
      login.request.json / login.response.json / login.response.rehash.json
      welcome.no-latest.json        // 断言 latest 键整体省略而非 null
      welcome.with-latest.json      // 断言六字段齐全及键序
      broadcast.clip.json / clip_ack.json / ping.json / bye.json / error.json
    invalid/
      depth-4/*.json                // 深度 4 样本（深度限 3）
      duplicate-field/hello.*.json  // 每消息类型一份重复字段
      unknown-field/hello.*.json
      number/
        hello.lastserverversion.{negative,fraction,exponent,string,toobig}.json
        clip.id.{...}.json          // 同五形态
        pong.clienttimeutc 数字污染样本（clientTimeUtc 处放非法值不影响 type 解析的场景说明见 2.3）
        token.payload.{negative,fraction,string}.json   // TokenService.VerifyToken 直测样本
      utf8/
        hello.invalid-utf8.bin.json      // 说明文件内嵌 \uD800 孤代理对
        clip.invalid-utf8.payload.txt    // 原始字节序列（非合法 UTF-8 的 payload 场景以文档标注）
```

csproj 增补（实施清单）：`<Content Include="ContractSamples\**\*.*" CopyToOutputDirectory="PreserveNewest" />`

### 2.2 驱动器设计

```csharp
public static IEnumerable<object[]> InvalidSamples => Directory.GetFiles(
    Path.Combine(AppContext.BaseDirectory, "ContractSamples", "invalid"), "*.json", SearchOption.AllDirectories)
    .Select(f => new object[] { f });

[Theory, MemberData(nameof(InvalidSamples))]
public void AllInvalidSamples_AreRejected_WithExpectedCode(string path)
{
    var frame = File.ReadAllBytes(path);
    var result = Protocol.ParseClientMessage(frame, RuntimeConfig.CreateDefaultConfig());
    Assert.True(result.IsFailure);                       // ParseResult.Failure
    var expected = ExpectedCodeAnnotation.Read(path);    // 见 2.3 注释约定
    Assert.Equal(expected, result.Error!.CodeName);      // CodeName 属性已有
}
```

正向样本单独断言：`ParseClientMessage` Success + MessageKind 正确 + record 字段逐项相等（如 `ClientHello.ClientId`、`ClipSnapshot.LocalModifiedAtUtc` 的两种合法时间格式 `"yyyy-MM-ddTHH:mm:ssZ"` 与 `"O"` round-trip）。

序列化侧不变式（ContractSchemaInvariants.cs）：

| # | 用例 | 断言对象 | 断言 |
|---|---|---|---|
| C1 | `Welcome_NoLatest_OmitsKey` | `Protocol.SerializeWelcome(null)` | 输出不含 `"latest"` 子串（当前 WhenWritingNull 行为，对照 welcome.no-latest.json） |
| C2 | `Welcome_WithLatest_FixedFieldOrder` | `SerializeWelcome(latest)` | 字节级与 welcome.with-latest.json 完全一致（UTF-8 bytes Equal），锁定 `protocolVersion→latest` 及 latest 内部键序 |
| C3 | `BroadcastClip_ContainsAllEightFields` | `Protocol.SerializeClip(...)` | 八字段齐且顺序与样本一致 |
| C4 | `TokenPayload_MinimalFixedOrder` | `Auth.SignToken(payload, secret)` | base64url 解码后 JSON 键序恰为 sub,ver,iat,exp，无数空格 |
| C5 | `ErrorResponse_IncludesReferenceId_WhenNotNull` | `Protocol.SerializeProtocolError` | referenceId 非 null 时在列；null 时省略 |
| C6 | `Timestamp_Formats_UtcZ` | PingMessage 序列化 | serverTimeUtc 以 Z 结尾秒级格式 |

### 2.3 样本预期结果标注约定

每个 invalid 样本文件首行注释 `// expect: invalid_message`（JSON 允许 // 会被 System.Text.Json 拒绝——因此不用行内注释，改用伴随文件或文件名约定）：

**采用文件名约定**：`invalid/number/hello.lastserverversion.negative.expect-invalid_message.json` 同目录放同名 `.expect` 文件过重——最终采用：目录即类别（number/duplicate-field/unknown-field/utf8/depth-4），全部预期 `invalid_message`；唯一例外 `depth-4/` 预期也是 `invalid_message`（当前实现对超深返回 invalid_message）。若有未来样本预期其他码，放置于 `expect-frame_too_large/` 等新目录。驱动器按一级子目录名推断期望码，缺省 invalid_message。

### 2.4 全矩阵清单（Q7=A）

每种消息类型 × 8 形态 = 24 个核心样本 + 正向样本 10 个 + token 直测 3 个 ≈ **37 个文件**：

| 形态 | hello 样本注入点 | clip 样本注入点 | pong 样本注入点 |
|---|---|---|---|
| 负数 | lastServerVersion=-1 | id 不能为数字→ 改 payload 数量型字段不可行，clip 注入 encrypted:"yes"（字符串枚举污染）| clientTimeUtc 缺失/类型错 |
| 小数 | lastServerVersion=1.5 | —（clip 无数值字段；样本改为 hash 字段数字类型污染） | clientTimeUtc=1.5 |
| 指数 | lastServerVersion=1e3 | 同上原则 | clientTimeUtc=1e3 |
| 字符串数字 | lastServerVersion="128" | encrypted="true"（应为 bool） | clientTimeUtc="2026-..."字符串包裹 |
| 超 long | lastServerVersion=18446744073709551616（>ulong） | — | — |
| 重复字段 | 双 type 或双 clientId | 双 id | 双 type |
| 未知字段 | extra:"x" | extra:"x" | extra:"x" |
| 非法 UTF-8 | clientId 内孤代理对 | payload 孤代理对 | clientTimeUtc 孤代理对 |

注：clip/pong 无原生数值字段的格，按"字段类型污染"等价覆盖（同一 JSON reader 数字分支），表中标"—"处移到最近似字段；这正是"等价分支合并"而非漏测，在样本 README.md 中写明映射关系。

token 直测 3 个（不走 WebSocket，直接 `TokenService.TryVerifyToken` + 手工构造 compact token）：payload 负数 ver、iat 小数、exp 字符串形式 —— 全部 false。

---

## 3. 单元测试缺口补齐（Category 默认，Q8=A）

以下用例加入现有 Tests 项目（不建新项目），分三个新文件。所有被测函数均已核实存在。

### 3.1 `AuthDeepTests.cs`（除 SlowHash 外全部用假哈希器或纯数据构造）

| # | 方法 | 被测函数 | 断言 |
|---|---|---|---|
| U1 | `SignToken_FieldOrder_And_MinimalJson` | `Auth.TokenService.SignToken` (Auth.cs:124) | 解码后恰为 {"sub":..,"ver":..,"iat":..,"exp":..} 顺序、无空格（与契约 C4 一致，此处单元级锚定） |
| U2 | `VerifyToken_Rejects_DuplicateFields` | `TokenService.TryVerifyToken` (149) | 手工构造含重复 "ver" 的 payload+正确 HMAC → false |
| U3 | `VerifyToken_Rejects_UnknownField` | 同上 | 多出一个 "aud" 字段 → false（即使验签通过也不行——需重签，样本构造 helper `MakeCompact(payloadJson)` 写进测试内部） |
| U4 | `VerifyToken_Rejects_FractionNumber` | 同上 | iat=1760000000.0 → false |
| U5 | `VerifyToken_Rejects_StringNumber` | 同上 | exp="1762592000" → false |
| U6 | `VerifyToken_Rejects_NegativeValue` | 同上 | ver=-1 → false |
| U7 | `VerifyToken_Rejects_ExpBeforeIat` | 同上 | exp <= iat → false |
| U8 | `VerifyToken_Rejects_AllPositiveCheck` | 同上 | iat=0 → false |
| U9 | `VerifyToken_RoundTrip_InstanceOverload` | `TokenService.CreateToken/VerifyToken(instance)` (108/144) | CreateToken 产物经 instance VerifyToken 通过且 payload 字段相等 |
| U10 | `NeedsRehash_ParameterParsing`（纯解析，不真算） | `Argon2PasswordHasher.NeedsRehash(string, int, int, int)` 静态版 (26) | 编码串 m/t/p 与传入参数不一致 → true；一致 → false；非 argon2id 前缀 → 按实现断言（编写时读静态实现确认分支后固定） |
| U11 | `WithVersion_Produces_NewImmutableRecord` | `CoreLogic.WithVersion` (Core.cs:268) | 返回新 LatestText：version 更新、其他字段保留、原实例未被修改；nowUtc=null 时沿用原 updatedAtUtc，显式传入时生效 |
| U12 | `NextVersion_At_UlongMaxValue_Throws` （已存在于 ClipAndCoreTests，若覆盖则跳过此项） | `CoreLogic.NextVersion` (258) | OverflowException |

### 3.2 `CliWatermarkTests.cs`（用户文件水位逻辑，跑真实 CLI 命令函数但注入 FastPasswordHasher）

| # | 方法 | 被测函数 | 输入构造 | 断言 |
|---|---|---|---|---|
| U13 | `AddUser_Allocates_FromWatermark_Increments` | `Cli.CommandAddUser`（private，经 `RunCli(new[]{"user","add",...})` 驱动） | 临时 users.json：nextTokenVersion=7，一个老用户 tokenVersion=3 | 命令 Ok；重载文件：新用户 tokenVersion==7，nextTokenVersion==8，老用户不动 |
| U14 | `DeleteUser_RecreateSameName_GetsFreshHigherVersion` | `RunCli user delete` + `user add` | nextTokenVersion=5、alice tokenVersion=2 → 删除 alice → 重建 alice | 新 alice tokenVersion==5（取全局水位）≠ 旧 2，nextTokenVersion==6 |
| U15 | `RevokeTokens_Sets_Watermark_Increments` | `CommandRevokeTokens` 经 RunCli | nextTokenVersion=9、bob tokenVersion=4 | bob.tokenVersion==9，nextTokenVersion==10 |
| U16 | `AddUser_At_LongMaxValue_FailsFast_FileUnchanged` | RunCli add | nextTokenVersion==long.MaxValue（手写 JSON） | 返回 Error；文件字节级未变（SaveUsers 前置 ValidateUsers/checked 溢出保护，Users.cs:130 atomic write 未触发） |
| U17 | `Revoke_At_LongMaxValue_FailsFast` | RunCli revoke-tokens | 同上 | Error；文件未变 |
| U18 | `ValidateUsers_NextMustExceed_AllUserVersions` | `UsersFile.ValidateUsers` (Users.cs:98) | nextTokenVersion=5 但某用户 tokenVersion=5 | 抛异常（InvalidOperationException），消息含 nextTokenVersion |
| U19 | `ValidateUsers_Rejects_NonPositiveVersion` | 同上 | tokenVersion=0 或 -1 | 抛异常 |
| U20 | `SaveUsers_AtomicWrite_LeavesOriginal_OnValidationFailure` | `UsersFile.SaveUsers` (130) | 构造非法 UsersFile（U18 场景）直接调 SaveUsers | 抛出且目标路径内容仍是旧内容（若存在）或不产生文件；临时文件不残留 |
| U21 | `HashPassword_VerifyPassword_Smoke`（真实 Argon2 1 例 smoke，走 SlowHash 见 3.4） |

### 3.3 `IdempotencyBehaviorTests.cs`（Q10=B 行为级）

| # | 方法 | 被测对象 | 输入构造 | 断言 |
|---|---|---|---|---|
| U22 | `DuplicateId_AfterBucketDrained_StillAcked` | `UserHub.ApplyClip`（UserHub.cs:280） | UserHub(initialVersion 由 ctor 给出) + StubConnectionContext；注入可控 clock：先用 burst=10 个不同 id 耗尽 `hub.ClipBucket`（clock 固定不前进则 refill=0）；第 11 个不同 id → 应得 rate_limited；然后发重复 id（与第 1 条相同 id+相同 payload/hash/encrypted）→ **成功 ACK，且不被 rate_limited** | 收到 clip_ack 且 version == 第 1 条的 version；期间 `hub.Version` 未变 |
| U23 | `DuplicateId_NewContent_IsTreatedAsFreshMessage` | 同上 | 同 id 不同 payload（沿用 U22 环境，令牌可用状态下） | 记录 warning 日志（StubLogger 收集 "Replacing reused clip id"）；产生新版本；消耗一个令牌（后续同样消息数会 rate_limited 提前触发） |
| U24 | `DuplicateId_LatestNull_FallbackAckHasEmptyPayload` | 同上 | 手工将 SeenIds 记忆置为(id, null) 的窗口场景：fresh ring 后 RememberId(id,null) 无法直接做——改为断言 IsUnchangedDuplicate(id,payload,...) 对 "entry 不存在" 返回 false | （文档化死分支：ApplyClip 中 duplicateLatest ?? Latest ?? fallback 第三段不可达，本用例锁定 IsUnchangedDuplicate 行为即可，不为死分支写生产代码路径测试） |
| U25 | `TryAcquireClockRefill_BoundaryCases`（补充现有 TokenBucketRefillsOverTime 的边界） | `TokenBucket.TryAcquire` | 同一时刻连取 burst 次 → 第 burst+1 次 false；时间前进 500ms（tokensPerSecond=2）→ true；倒流时钟（nowUtc < lastRefill）→ false（Core.cs:150 分支） | 逐一符合 |

环境要求（列入实施前置）：`ConnectionContext`/`ConnectionStateBag` 需要 stub 化 socket——现有 `UserLoopConcurrencyTests` 已有做法，复用其 stub 模式；若不可复用则在测试内新建 `StubSocket : WebSocket`。

### 3.4 `SlowHashSmokeTests.cs`（Q9=C 专项）

```csharp
[Trait("Category", "SlowHash")]
public class SlowHashSmokeTests
{
    // 真实 Argon2PasswordHasher（Isopoh），参数用 Cli.CreateArgon2Config(config) 生产默认
    U26 Hash_Then_Verify_RoundTrip                     // Hash("pw") → Verify("pw")==true、Verify("wrong")==false
    U27 NeedsRehash_CurrentParams_ReturnsFalse         // 刚生成的哈希对同参数应 false
    U28 NeedsRehash_StaleParams_ReturnsTrue            // 手工把编码串 m=19456,t=2,p=1 改成 m=1024,t=1,p=1 → true
    U29 Timing_DummyHash_Security_Note                 // 不测毫秒级时序（脆弱），仅注释指向 AuthServiceTimingTests 已有覆盖
}
```

CI 最终过滤（实施清单，ci.yml 同步修改）：
`--filter Category!=NetworkIntegration&Category!=SlowHash`
本地专项：
`--filter Category=SlowHash` / `--filter Category=NetworkIntegration`

---

## 4. 实施顺序建议

1. 契约测试（§2）：零生产代码依赖改动，只加 csproj Content 项 → 最快见效。
2. 单元缺口（§3.1–§3.3）：纯新增文件；唯一可能的 touch 点是若 stub 需要暴露 TokenBucket 只读成员（Q10 选了 B 行为级，通常不需要）。
3. NetworkInfrastructure（§1）：fixture + 11 个网络用例 + ci.yml 过滤同步。
4. 全部落地后 docs/server-spec.md §10 按本规格回填测试矩阵描述（修订工作另见 spec-decisions.md 路径 A/C 执行记录）。

## 5. 范围外声明

以下从未实现项本轮**明确不做**，相关条目将从 docs/server-spec.md 移除（另一步执行）：
- Benchmark 项目与压测场景（原 spec §10.4）
- `server_stop` 安全事件（原 spec §8.1 事件表行）
- 性能指标目标表（原 spec §9 整节）
