# TextCascade.Server 代码审查报告 (Code Review)

## 1. 总体架构评价

TextCascade.Server 是一个定位非常清晰的轻量级剪贴板/最新文本同步服务端。整体代码风格紧凑、克制，没有引入臃肿的企业级分层（无 EF Core、无外部数据库、无庞杂的中间件），非常契合 "Ponytail" / "Do Less" 的实用主义设计哲学。

### 核心亮点
1. **并发与隔离模型清晰**：每个在线用户一个 `UserHub`，内部采用单消费者 Channel（`RunUserLoopAsync`），天然避免了多连接并发修改版本号和最新文本时的大颗粒度锁争用。
2. **背压与慢连接处理果断**：每个连接分配有界发送队列（默认 16 条），队列满时立即判定为慢连接并直接 Cancel/Abort，绝不等待阻塞，也不拖垮同用户的其他客户端。
3. **无状态 Token + 版本作废机制**：采用基于 HMAC-SHA256 的紧凑型 Token，服务端重启无需保存 Session；通过 `users.json` 中的 `tokenVersion` 与全局水位 `nextTokenVersion` 实现高效的单用户/全量 Token 撤销。
4. **内存与资源开销极低**：广播时一次序列化，多连接复用同一份 UTF-8 Byte 数组；统一心跳扫描器替代每连接独立 Timer。
5. **单文件与开箱即用**：集成了 CLI 用户管理、TLS 证书加载、本地状态落盘与单实例锁，运维负担极小。

---

## 2. 关键发现与问题清单

### P1 - 性能与资源瓶颈 (Performance & Allocation)

#### 1. 广播路径上的连接列表数组分配
- **位置**：`TextCascade.Server/Hub/UserHub.cs`
- **现象**：`Connections` 属性每次被读取时，都会执行 `lock (connectionsGate) { return connections.ToArray(); }`。在 `ApplyClip` 广播、`BroadcastWelcome` 等高频路径中，每条 Clip 都会分配一次连接数组。
- **人话建议**：
  在连接数变动较少、消息广播频繁的场景下，可以采用 **Copy-On-Write（写时复制）** 模式维护内部连接数组（即维护一个不可变的 `ImmutableArray` 或 `ConnectionContext[]`，只有在 `AddConnection` / `RemoveConnection` 时才重新生成新数组）。这样广播时读取连接列表完全**零锁、零分配**。

#### 2. Token 校验中的多重内存分配与 Dictionary 构造
- **位置**：`TextCascade.Server/Auth.cs` (`TokenService.TryVerifyTokenInternal`)
- **现象**：
  1. `compactToken.Split('.')` 每次都分配一个 `string[]`。
  2. `var actualPayload = payloadRent is null ? payloadBytes[..payloadLength].ToArray() : payloadRent[..payloadLength];` 在栈空间足够时依然调用了 `.ToArray()`。
  3. `var properties = root.EnumerateObject().ToDictionary(...)` 每次校验 Token 都会分配一个 `Dictionary<string, JsonElement>` 和 4 个字符串 Key。
- **人话建议**：
  HMAC 校验和 `JsonDocument.Parse` 都可以直接接受 `ReadOnlySpan<byte>`。解析 Payload 时直接使用 `root.TryGetProperty("sub", out ...)`，不要转成 `ToDictionary`，也不要多余 `.ToArray()`。作为 WebSocket 握手升级的高频入口，优化后可实现接近零分配。

#### 3. 登录接口中的双重 JSON 解析与字符串转换
- **位置**：`TextCascade.Server/AuthService.cs` (`ParseLoginRequest`)
- **现象**：从 Request Body 读取后，先 `new UTF8Encoding().GetString(body.ToArray())`，再用 `JsonDocument.Parse` 校验属性，最后又调用了一次 `JsonSerializer.Deserialize<LoginRequest>(text)`。
- **人话建议**：既然第一步已经用 `JsonDocument` 验证了字段结构，直接从 `document.RootElement` 取出 `username` 和 `password` 即可，彻底省去第二次 `JsonSerializer.Deserialize` 以及中间的多余字符串拷贝。

---

### P2 - 并发与稳健性风险 (Concurrency & Edge Cases)

#### 1. Argon2id 同步计算可能阻塞 Kestrel 线程
- **位置**：`TextCascade.Server/AuthService.cs` (`HandleLoginAsync`)
- **现象**：`syncServer.Hasher.Verify(request.Password, passwordHash)` 是纯 CPU/内存密集型运算（约 19MB 内存、2 轮迭代）。当前是在 Kestrel HTTP 请求处理的同一线程上下文中同步执行。
- **人话建议**：如果短时间内有多个并发登录请求，可能会占满线程池调度。建议使用 `await Task.Run(() => syncServer.Hasher.Verify(...))` 将密码哈希运算明确卸载到后台工作线程，避免堵塞 HTTP 管道。

#### 2. 滑动窗口限流器在 Key 满时的全局遍历瓶颈
- **位置**：`TextCascade.Server/Core.cs` (`SlidingWindowLoginLimiter.TryConsume`)
- **现象**：当外部遇到 IP/用户名爆破攻击导致 `windows.Count >= maxKeys` (10000) 时，每次新请求都会在 `lock (gate)` 内遍历整个字典清理过期项（`RemoveExpired`）。在大流量恶意扫描下，会导致所有正常用户的登录请求在同一个锁上排队。
- **人话建议**：
  可以采用分段锁，或者在后台使用 `PeriodicTimer` 每隔几秒做一次批量清理；当字典达到 `maxKeys` 时，不再对每个请求都执行全量 O(N) 遍历，而是快速丢弃或采用简单的 LRU/分桶计数。

#### 3. 规范与现实的偏离 (Spec vs Code Drift)
- **位置**：`docs/server-spec.md` vs `TextCascade.Server/Hosting/UserFileWatcher.cs`
- **现象**：
  - `server-spec.md` §3.2 和 §14 中明确写道：“*不热加载用户文件，避免在线连接认证状态与文件状态竞态*”、“*修改用户文件后需重启服务生效*”。
  - 但代码中实现了 `UserFileWatcher` 并在 `ServerHost.cs` 中启用了文件变动监听和热加载。
- **人话建议**：代码中的热加载实现实际上做得很干净（使用了防抖、重试以及 `Volatile.Write` 原子替换），这是个很好的功能改进。建议同步更新 `docs/server-spec.md` 文档，将已废弃的“必须重启生效”描述更新为“支持 users.json 监听与平滑热重载”。

---

### P3 - 代码洁癖与简化空间 (Code Cleanliness & Simplification)

1. **废弃的 `TryDuplicate` 方法**：
   `Core.cs` 中的 `SeenIdRing.TryDuplicate(string id)` 目前在实际业务流程中未被调用（业务中使用的是 `IsUnchangedDuplicate` + `TryGetResult` + `RememberId`），仅留存在单元测试中。可评估是否需要保留或标记内部测试专用。
2. **Source Generator 覆盖度**：
   协议消息（`WelcomeMessage`, `ClipMessage` 等）已经使用了 .NET 10 的 `JsonSourceGenerationOptions` 强类型上下文，非常棒！但 `RuntimeStateStore` 和 `UsersFile` 仍在使用传统的动态反射序列化。建议统一接入 Source Generator，进一步增强 Native AOT 兼容性与运行速度。

---

## 3. 改进建议总结

| 序号 | 改进项 | 收益 | 实施复杂度 |
|---|---|---|---|
| 1 | `UserHub.Connections` 改用 Copy-On-Write 数组 | 广播路径彻底消除锁与数组分配 | 低 |
| 2 | `AuthService` 与 `TokenService` 内存与解析去重 | 降低登录与 WS 握手时的 GC 压力 | 低 |
| 3 | Argon2 校验使用 `Task.Run` 卸载 | 保护 Kestrel 请求处理管线抗突发并发 | 极低 |
| 4 | 限流器清理机制从同步全量遍历优化为轻量清理 | 提升恶意请求轰炸下的吞吐与稳定性 | 中 |
| 5 | 同步更新 `server-spec.md` 关于热重载的说明 | 保持文档与生产代码一致 | 极低 |
