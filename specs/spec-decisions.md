# TextCascade 规格修订决策记录

日期：2026-08-27
任务：三类差异三条路径（详见 docs/server-spec.md 审计与 git 溯源结论）
- 路径 A：更新引入的漂移 → 直接修订 `docs/server-spec.md` 对齐 v0.3.5，无决策点。
- 路径 B：NetworkIntegration 测试、契约测试组织、单元测试缺口 → 新建 `specs/test-and-contract-spec.md`，以下 10 题决定其内容。
- 路径 C：其余从未实现项（Benchmark 项目、server_stop 事件、性能目标表）→ 从 docs/server-spec.md 移除。

规则：每轮只问一个问题；提问前本题已落盘；收到答案后立刻回写本题"选择"栏。

---

## Q1 NetworkIntegration 测试宿主 【已选】

NetworkIntegration 整类测试（TLS/WSS、HTTP 升级、随机端口绑定、真实帧分片、登录建连收发、重启直连恢复）放在哪里？

- **A. 复用现有 WebSocketIntegrationTests 的 fixture**（真实 Kestrel 绑定 127.0.0.1 随机端口 + ClientWebSocket）
  - 优：零新增设施；与现有 6 个集成测试共享 helper，维护成本最低；TLS 测试只需给 fixture 加证书参数。
  - 劣：同一项目里普通集成测试与网络测试混在一起，只能靠 Trait 过滤，CI 跳过逻辑与文件物理布局无对应关系。
- **B. Tests 项目内新建 `NetworkIntegration/` 目录 + 自建 fixture**
  - 优：物理隔离清晰；fixture 可针对 TLS/重启场景定制（如持有可重启的 WebApplication 列表）；不影响现有 fixture 稳定性。
  - 劣：需要抽象或复制现有 fixture 的公共逻辑，初始工作量中等；两套 helper 有漂移风险。
- **C. 独立测试项目 `TextCascade.Server.NetworkTests`**
  - 优：隔离最彻底；CI 可以完全按项目粒度跳过（不改 slnx 过滤逻辑的话需加入 slnx 但 CI 单独 dotnet test 主项目即可）；未来可加性能冒烟。
  - 劣：需改 .slnx、新增 csproj、跨项目 InternalsVisibleTo；维护三个项目的成本最高。

选择：**B. Tests 项目内新建 `NetworkIntegration/` 目录 + 自建 fixture**（2026-08-27）

---

## Q2 NetworkIntegration 过滤机制 【已选】

如何让 `dotnet test` 默认跳过这些测试、按需运行？

- **A. `[Trait("Category", "NetworkIntegration")]` + CI/本地用 `--filter Category!=NetworkIntegration`**
  - 优：xunit 原生、与 spec 原文 `Category=NetworkIntegration` 完全一致；`dotnet test --filter Category=NetworkIntegration` 即可单独跑。
  - 劣：CI 的 `dotnet test TextCascade.Server.slnx` 必须追加过滤参数，否则会跑到（需同步改 ci.yml）。
- **B. MTP 风格（Microsoft.Testing.Platform）`--filter-category` 等**
  - 优：新一代测试平台原生参数。
  - 劣：当前项目用 VSTest 模式跑（ci.yml 无 MTP 配置），引入 MTP 需改测试 SDK 配置，风险与收益不成比例。
- **C. xunit Collection + 命名约定（如类名以 Network 开头）+ assembly 级配置**
  - 优：Collection 级可统一串行化，适合真实端口/证书场景。
  - 劣：跳过逻辑要靠自定义 xunit trait 发现或 `--filter` 仍然需要；命名约定脆弱。

选择：**A. `[Trait("Category", "NetworkIntegration")]` + `--filter` 过滤**（2026-08-27）。备注：实施时需同步给 ci.yml 的 dotnet test 追加 `--filter Category!=NetworkIntegration`；本轮只写入 spec 实施清单，不改代码。

---

## Q3 测试用证书策略 【已选·默认】

NetworkIntegration 的 TLS/WSS 测试需要证书，从哪来？

- **A. 测试运行时自签生成**（.NET `CertificateRequest` 创建自签叶证书 + 私钥，内存导出 PFX 或直接传 X509Certificate2）
  - 优：无仓库二进制；证书永不过期（自签可设长有效期）；跨平台无文件权限问题；可顺带测试"带密码 PFX 拒绝""PEM bundle"等加载路径。
  - 劣：需在测试 fixture 写约 30 行证书生成代码；自签证书的 Subject/SAN 需要构造（客户端可关证书校验绕过）。
- **B. 仓库内提交测试用 PFX 文件**
  - 优：fixture 最简单，直接读文件。
  - 劣：仓库出现二进制文件；PFX 若含密码会与"无密码证书"规格冲突，需维护说明；证书过期/轮换是长期负担；安全审计观感差。
- **C. 证书加载器抽象出接口（ILoadedCertificateProvider）供测试 mock**
  - 优：单测可全 mock，不碰真实证书。
  - 劣：**与 NetworkIntegration 的目的冲突**——该类测试本就要验证真实证书加载与 WSS 握手，mock 掉加载器等于没测；还会改动生产代码结构。

选择：**A. 测试运行时自签生成**（2026-08-27，用户未作答，按推荐项默认；如需改选请修改此行）。

---

## Q4 重启直连恢复测试形态 【已选·默认】

"服务端重启后 token 直连重连与 snapshot 恢复"如何模拟重启？

- **A. 同进程内停掉 WebApplication 再重启**（`ServerHost.CreateApp` 构建两次，第一次 `StopAsync` 后第二次 `RunAsync`，共用同一 users.json/临时目录）
  - 优：真实覆盖"进程内服务器实例重建 + 状态文件残留 + token 跨实例有效"链路；快、稳定、可调试；RuntimeStateStore 落盘行为也能顺带验证。
  - 劣：不是真的进程退出——静态/全局状态若有残留可能掩盖问题（当前代码 CreateApp 每次新建 SyncServer，风险可控）。
- **B. 独立子进程跑发布产物**
  - 优：最真实的进程级重启（含文件锁、端口释放、PID 生命周期）。
  - 劣：需要先 publish 或定位构建产物，慢（数十秒）、CI 不稳定因素多；端口占用/防火墙等环境敏感；调试困难。
- **C. 只测 CreateApp 重建，不测真实停启时序**
  - 优：最省事。
  - 劣：覆盖不了 bye/1001 → 重连 → snapshot 上报的完整时序，测试价值大打折扣。

选择：**A. 同进程内停掉 WebApplication 再重启（ServerHost.CreateApp 两次）**（2026-08-27，用户未作答，按推荐项默认；如需改选请修改此行）。

---

## Q5 TLS 版本断言 【已选】

spec §8.2 "TLS 最低 1.2"——当前实现未显式设置 SslProtocols。测试怎么处理？

- **A. 客户端显式用 SslProtocols.Tls12（及另一条 Tls13）发起 WSS，断言握手成功**
  - 优：行为级验证，直接回答"1.2/1.3 能不能用"；在 Windows/Linux 默认配置下均应通过。
  - 劣：若 OS 未来禁用 TLS1.2，测试会失败但那是 OS 政策问题，需在断言消息中说明。
- **B. 断言 Kestrel 配置对象中的 HttpsConnectionAdapterOptions.SslProtocols**
  - 优：直接检查服务端配置意图。
  - 劣：当前实现根本没设置该选项（依赖 OS 默认），断言会立即失败——**选 B 实际上等于决定要先改生产代码显式设置 TLS 下限**，超出本轮"只写测试 spec"的范围。
- **C. 不测 TLS 版本，在测试 spec 的"已知限制"一节记录：服务端依赖 OS 默认协议版本**
  - 优：零成本，与当前实现状态一致。
  - 劣：TLS 降级风险（理论上）无回归防护。

选择：**A. 客户端显式 SslProtocols.Tls12/Tls13 发起 WSS，断言握手成功**（2026-08-27）。备注：断言消息中需说明"若 OS 政策禁用该版本导致的失败属环境问题"。

---

## Q6 契约测试组织 【已选】

spec §10.4 的"服务端维护典型 JSON 样本，约束三端协议字段与行为"如何组织？

- **A. Tests 项目内新建 `ContractTests/` 目录 + JSON 样本文件落盘**（`ContractSamples/hello/*.json` 等，测试读取并断言解析结果）
  - 优：样本即文档，三端（C#/Kotlin）可直接取用同一批文件做各自实现的对拍；新增样本不改代码。
  - 劣：需要 csproj 把样本 CopyToOutputDirectory；文件与代码双份维护。
- **B. 内联样本字符串（Theory 的 InlineData / 常量）**
  - 优：最简单，全部在一个 .cs 里，跳转方便。
  - 劣：三端对拍要人肉从代码抄样本；样本多了文件臃肿。
- **C. 独立契约项目（如 TextCascade.Contract.Tests）**
  - 优：概念上最干净。
  - 劣：为几十个样本开一个项目不值；又多一项 slnx/CI 维护。

选择：**A. Tests 项目内新建 `ContractTests/` 目录 + JSON 样本文件落盘**（2026-08-27）。备注：实施时需在测试 csproj 加 CopyToOutputDirectory；样本目录设计在 test-and-contract-spec.md 中细化。

---

## Q7 非法数字 / 非法 UTF-8 样本范围 【已选】

契约样本要覆盖"JSON 深度 3、重复字段、未知字段、非法数字、非法 UTF-8"，其中非法数字与非法 UTF-8 的覆盖面多大？

- **A. 全矩阵：每种消息类型（hello/clip/pong）× 每种非法数字形态（负数、小数、指数、字符串数字、超 long、重复字段、未知字段、非法 UTF-8 字节）**
  - 优：完备，一次性锁死三端解析行为。
  - 劣：样本数量约 3×8=24+，编写与维护成本高；部分组合行为完全同质（都是 invalid_message），边际价值低。
- **B. 代表性样本：每类非法形态挑 1-2 个高价值位置（如 hello.lastServerVersion、clip.id）**
  - 优：成本适中，覆盖每个错误分支至少一次；样本 10 个左右。
  - 劣：非全矩阵，理论上某消息类型某字段的特有解析分支可能漏测。
- **C. 仅 token + clip 两个高风险面做全形态，hello/pong 只测代表性样本**
  - 优：风险导向，token 是安全面、clip 是主路径。
  - 劣：hello.lastServerVersion 的数字校验分支（TryGetUInt64）无直接样本。

选择：**A. 全矩阵：每种消息类型（hello/clip/pong）× 每种非法形态（负数、小数、指数、字符串数字、超 long、重复字段、未知字段、非法 UTF-8）**（2026-08-27，用户主动选择非推荐项）。

---

## Q8 单元测试缺口补齐方式 【已选】

第一轮审计列出的单元缺口：Argon2 三函数（HashPassword/VerifyPassword/NeedsRehash）、token 重复字段/非法数字/非法范围（exp≤iat、负数）、CLI 水位递增/删除重建/溢出 fail-fast、WithVersion、"重复 id 不消耗令牌桶"。补多少？

- **A. 全部补齐**（上述每一项都有直接测试）
  - 优：spec §10.1 清单闭环；Argon2 与 CLI 是安全/数据完整性面，值得覆盖。
  - 劣：工作量最大（约 25-35 个用例）；CLI 水位测试需要搭临时 users.json 场景。
- **B. 只补高风险面**：token 数字校验全形态 + CLI 水位/溢出 + 重复 id 不耗令牌桶
  - 优：安全相关（token）与数据完整性（水位）+ 协议热点（幂等）优先；约 15 个用例。
  - 劣：Argon2 三函数与 WithVersion 仍零覆盖。
- **C. 只补纯函数**（WithVersion、token 校验等无需 IO 的），不碰 Argon2 与 CLI
  - 优：测试快、无环境依赖。
  - 劣：恰好跳过了两个最重要的缺口（Argon2、CLI 水位），补了寂寞。

选择：**A. 全部补齐**（Argon2 三函数、token 全形态、CLI 水位/重建/溢出、WithVersion、重复 id 不耗令牌桶；约 25-35 用例）（2026-08-27，用户主动选择非推荐项）。

---

## Q9 Argon2 测试用假哈希器还是真实慢哈希 【已选】

spec 原文："认证测试注入假哈希器，避免 Argon2id 拖慢常规单元测试"。但 Argon2 三函数本身还没测过。

- **A. 假哈希器为主**：Argon2 三函数只测 1 个真实参数的 smoke 用例（Hash→Verify 成功、错密码失败），其余认证路径全部用现有 FastPasswordHasher/RecordingHasher
  - 优：符合 spec 精神；全套测试仍秒级；NeedsRehash 的参数解析逻辑用构造的哈希串测（不真算）。
  - 劣：NeedsRehash 真实行为（不同参数组合判定）覆盖浅。
- **B. 真实 Argon2 低参数**（如 memory=64KiB, iterations=1）跑完整用例矩阵
  - 优：测试对象即真实算法，无 mock 偏差。
  - 劣：与生产参数（19456KiB/2）不同，测的不是同一配置；即便低参数，几十个用例也明显拖慢。
- **C. 混合**：单测用假哈希器；单独一个标记 `Category=SlowHash` 的真实参数用例（Hash→Verify→NeedsRehash 三链路）
  - 优：日常快速，专项真实；NeedsRehash 真实语义有兜底。
  - 劣：又引入一个新的测试类别过滤（与 Q2 的机制要兼容）。

选择：**C. 混合**：单测用假哈希器；单独 `Category=SlowHash` 真实参数用例覆盖 Hash→Verify→NeedsRehash 三链路（2026-08-27）。备注：CI 排除规则需同时排除 SlowHash 与 NetworkIntegration。

---

## Q10 "重复 id 不消耗令牌桶"测试断言深度 【已选】

spec §5.4 要求重复 id 不生成新版本、不耗令牌桶、重复 ACK 走有界队列。测试怎么断言？

- **A. 直接断言内部状态**：`hub.ClipBucket` 可观察的剩余令牌数（需给 TokenBucket 加测试可见的读取，或在测试程序集可见的 internal 属性）
  - 优：精确、无歧义，直接锁死"没消耗"。
  - 劣：依赖内部结构，重构时测试要跟着改；可能需要给生产代码加 internal 只读成员（InternalsVisibleTo 已存在，成本低）。
- **B. 行为级断言**：先真实发送 burst 上限（10）条不同 clip 耗尽令牌桶，再发重复 id 的 clip——断言仍能拿到 ACK（未被 rate_limited）；对照组：发新 id 的 clip 被拒
  - 优：不依赖内部实现，从客户端可观测行为验证，天然防回归。
  - 劣：构造稍复杂（要精确耗尽桶）；时间相关的 refill 需要用注入 IClock 控制。
- **C. 两者都要**：内部断言精确性 + 行为断言防回归
  - 优：覆盖最全。
  - 劣：维护成本最高；两套断言可能对同一行为给出矛盾信号（若实现变化，需判断哪个是"真相"）。

选择：**B. 行为级断言**：耗尽 burst 后重复 id 仍获 ACK、新 id 被拒；用注入 IClock 控制 refill（2026-08-27）。

---

## 决策汇总与冲突检查

| 题 | 选择 | 状态 |
|---|---|---|
| Q1 测试宿主 | B. Tests 项目内 NetworkIntegration/ 目录 + 自建 fixture | 已选 |
| Q2 过滤机制 | A. [Trait(Category=NetworkIntegration)] + --filter；ci.yml 需加排除 | 已选 |
| Q3 测试证书 | A. 运行时自签生成 | 已选（默认） |
| Q4 重启形态 | A. 同进程 CreateApp 两次停启 | 已选（默认） |
| Q5 TLS 断言 | A. 客户端显式 Tls12/Tls13 握手断言 | 已选 |
| Q6 契约组织 | A. ContractTests/ 目录 + 样本文件落盘 | 已选 |
| Q7 样本范围 | A. 全矩阵（3 消息类型 × 8 形态） | 已选 |
| Q8 单测范围 | A. 全部补齐（约 25-35 用例） | 已选 |
| Q9 Argon2 | C. 混合：假哈希 + Category=SlowHash 专项 | 已选 |
| Q10 断言深度 | B. 行为级断言（耗尽桶后重复 id 仍 ACK） | 已选 |

### 冲突检查结论（2026-08-27）

- Q3（自签证书）× Q5（真实 TLS 握手）：无冲突——自签证书正是真实握手所需，互补。
- Q2（Trait 过滤）× Q9（新增 SlowHash 类别）：兼容——同一机制扩展一个新 Category 值，CI 排除 `Category!=NetworkIntegration&Category!=SlowHash`。
- Q1（同项目目录）× Q4（CreateApp 两次停启）：兼容——fixture 放 NetworkIntegration/ 目录内，持有 WebApplication 列表即可。
- Q6（样本落盘）× Q7（全矩阵）：兼容——全矩阵约 24+ 样本文件，落盘组织正是为此设计；测试 csproj 需 CopyToOutputDirectory。
- Q8（全部补齐）× Q9（混合）：兼容——Argon2 三函数的功能断言放 SlowHash 专项，认证路径其余测试保持假哈希器。
- Q10（行为级）× Q8（补重复 id 缺口）：兼容——该缺口以行为级用例补齐，无需改生产代码。
- 无发现组合冲突。

---