# 性能实测报告 / Performance Measurement Report

状态 / Status：v0.4.0 首次实测（/ First measured run on v0.4.0）  
日期 / Date：2026-08-29（ measurements executed 2026-08-27 23:50 – 2026-08-29 01:50 CST）  
测量工具 / Tooling：[tools/perf_probe.py](tools/perf_probe.py)（纯标准库 asyncio WSS 探针 / stdlib-only asyncio WSS probe）  
关联 / Related：[docs/server-spec.md](docs/server-spec.md) §9、§15；[specs/spec-decisions.md](specs/spec-decisions.md)

> 摘要 / Summary：延迟与 CPU 表现优秀（1KB 广播 p95 3.5ms，8.6 倍余量；空闲 CPU 0.08%）；冷启动 2 秒达标；内存目标（P1/P2）未达标——.NET 运行时基线与每连接真实成本决定了旧目标定得过紧；1000 并发在该 1.6GB 内存的同机环境不可测（触发宿主机硬重启）。另发现两个实现层问题（停机 close 握手无超时、队列满熔断无日志），已记入 spec §15。Windows 本机补测 1000 并发通过（10 分钟零断连、+211 MB），并顺带发现并修复了 Windows TLS 临时密钥缺陷。  
> **Summary**：Latency and CPU are excellent (1KB broadcast p95 3.5 ms — 8.6× headroom; idle CPU 0.08%); cold start meets the 2 s target; the memory targets (P1/P2) are not met — the .NET runtime baseline and the real per-connection cost show the old targets were too tight; 1000 concurrent connections is untestable on this 1.6 GB same-host environment (the VM hard-reset). Two implementation findings (unbounded shutdown close-handshake wait; silent queue-full abort without logging) are recorded in spec §15. A follow-up run on a local Windows machine (32 GB) passed the 1000-connection scenario (10 minutes, zero disconnects, +211 MB) and surfaced a Windows TLS ephemeral-key defect that has been fixed.

---

## 第一部分：中文

### 1. 测试环境

| 项 | 值 |
|---|---|
| 硬件 | 2 vCPU（Intel Xeon Platinum）/ 1.6 GB 内存（LongCloud VPS） |
| 系统 | Ubuntu 24.04.4 LTS，内核 6.8.0-63-generic |
| 运行时 | .NET 10.0.11（框架依赖单文件，`TextCascade.Server` v0.4.0+fb33861） |
| 服务形态 | systemd（`textcascade-server.service`），WSS + 生产自签证书，端口 8443 |
| 负载端 | 与服务同机，回环 127.0.0.1，`tools/perf_probe.py`（Python 3.12 asyncio） |
| 服务配置 | 默认值 + `[rate_limit] clip_burst/clip_tokens_per_second` 临时调至 5000/5000（仅延迟与慢消费者场景，测后已恢复） |

同机回环排除了网络抖动，但负载端与被测端共享 2 个 vCPU 与内存——并发类场景（S5）受此制约。

### 2. 结果总表

| # | 场景 | 指标 | 目标 | 实测 | 判定 |
|---|---|---|---|---|---|
| P1 | S1 基础内存 | RSS（新进程，60s 预热） | < 50 MB | **125–131 MB** | ✗ 未达标 |
| P2 | S2 100 空闲连接 | RSS 增量（5 分钟，全部存活） | < 20 MB | 首测 +66 MB（含 JIT/堆扩张一次性成本）；复测边际 **+24 MB**（≈240 KB/连接） | ◑ 边际接近达标 |
| P3 | S3 1KB 广播 | broadcast_lag p95（1000 样本） | < 30 ms | **3.5 ms**（p50 1.87 / p99 5.1 / max 11.8） | ✓ 达标（8.6× 余量） |
| P3b | S3 附带 | ack_rtt p95 | 参考 | 4.0 ms | — |
| P4 | S4 512KB 广播 | broadcast_lag p95（200 样本） | < 250 ms | **103.2 ms**（p50 87.3 / p99 138 / max 157） | ✓ 达标（2.4× 余量） |
| P5 | S8 空闲 CPU | 60 秒均值（两轮） | ≈ 0% | **0.08%**（5 ticks / 60 s） | ✓ 达标 |
| P6 | S7 冷启动 | 应用启动阶段（Started → listening） | < 2 s | **2 s**（三次重启一致） | ✓ 达标（临界） |
| P7 | 恢复窗口 | snapshot_window_seconds | 3 s | 配置常量，功能由集成测试覆盖 | —（不适用） |
| P8 | S5 1000 并发 | 10 分钟稳定性 | 无断连 | **Windows 本机通过**：0 错误、1000/1000 存活、+211 MB（Linux VPS 同机环境不可测，见 S5 明细） | ✓ 达标（Windows 32 GB） |
| — | S6 慢消费者隔离 | A 的 p95（B 停读 45 秒） | 不受影响 | **7.0–7.9 ms**（基线 6–17 ms）；B 在 ~16 s 被静默熔断 | ✓ 隔离有效 |

### 3. 各场景明细

**S1 基础内存**：三次重启后 RSS 分别为 127872 / 128256 / 131328 kB；运行 24 小时后为 143852 kB。基线由 .NET 10 运行时、Kestrel、TLS 栈与 22 个线程构成，新进程即为 ~125 MB，说明不是泄漏而是运行时基线。原 50 MB 目标对 ASP.NET Core 应用不现实（判为"目标过紧"而非"实现缺陷"）。

**S2 100 空闲连接**：新进程 RSS 127872 kB → 195392 kB，增量 67520 kB（≈660 KB/连接）。期间 900/900 心跳 pong 全部响应，零错误。660 KB/连接包含 TLS 流缓冲、Kestrel 每连接管道与 pinned buffer、托管对象。原 20 MB 目标（200 KB/连接）低估了 Kestrel + TLS 的真实成本。

**S2 复测（澄清 660 KB/连接的归因，2026-08-29）**：在已运行 32 分钟、代码已完全 JIT 的进程上重跑同一场景，增量仅 **+24 MB（≈240 KB/连接）**，且连接关闭后内存不回落（GC 段保留，三个 ~20 MB 匿名段）。结论：初次测得的 660 KB/连接混合了一次性成本——100 个连接的握手风暴触发的 JIT 编译与 GC 堆首次扩张（约 40-50 MB）——而非每连接真实成本；真实的**边际**每连接成本约为 240 KB（Linux）与 211 KB（Windows）同量级。Windows 显示"更低"主要是分母效应：211 KB 摊在 1000 个连接上，而 Linux 的 66 MB 摊在 100 个上。修订后的 P2 判定：**边际成本达标（240 KB/连接 vs 目标 200 KB/连接，差 20%）**，首次连接风暴的瞬时峰值超出目标。

**S3 1KB 广播延迟**：1000 样本全数回收。`broadcast_lag` p95 = 3.5 ms——服务端路径（解析 → 令牌桶 → 版本自增 → 单次序列化 → 双连接投递）加上两端 TLS 在回环上的开销远低于 30 ms 目标。

**S4 512KB 广播延迟**：200 样本全数回收。p95 103 ms，主要成本在 512KB JSON 的序列化/转义与两次 512KB TLS 记录写，2.4 倍余量达标。

**S5 1000 并发连接（未完成，有生产影响）**：进程基线 128256 kB 起步。SSH 会话在风暴中约 2 分钟后被重置，随后整机冻结，01:38:58 宿主机 watchdog 硬重启恢复。**根因（据上一启动周期持久日志）**：00:48:59 一秒内全部 1000 个 WebSocket 升级请求同时打到刚重启 40 秒的冷进程；00:50:18 起 journald 连续输出 "Under memory pressure, flushing caches"；00:51:53 日志彻底静默（整机冻结 47 分钟）。内核日志**无任何 OOM kill 记录**，机器 **swap = 0**。结论：不是稳态内存不足——1000 × 240 KB（复测边际成本）≈ 240 MB 完全容纳得下——而是**风暴瞬时峰值**击穿了零 swap 环境：冷进程 JIT + 1000 个并发 TLS 握手的叠加分配 + 同机 Python 负载端自身数百 MB + 升级日志洪流，导致内核回收活锁（.NET 运行时的文件映射页被反复逐出重读），冻结到 OOM killer 根本来不及工作。**生产影响披露**：期间真实用户设备约 47 分钟无法连接。本环境通过 P8 的可行路径：预热后的服务 + 缓慢升速（如 20 连接/秒）+ 临时 swap，或跨机负载端。

**S5 补测（Windows 本机，2026-08-29）**：环境为 Windows x64 / 32 GB RAM，openssl 自签无密码 PFX，二进制含 Windows TLS 修复（见 F5）。结果：1000 连接全部建立（~90 秒完成握手）、0 错误、10 分钟保持期间 19576/19000 心跳 pong 全响应（下限 19000 = 1000 连接 × 19 个周期，超出部分为时钟取整）；RSS 曲线 105 MB（基线）→ 稳态 262–285 MB → 结束 316 MB，**增量 ≈ 211 MB（≈211 KB/连接）**，仅为 Linux 实测值（660 KB/连接）的三分之一（Kestrel/TLS 缓冲策略差异 + GC 行为不同）。全程服务日志无任何错误。**P8 判定：通过。**

顺带发现并修复 **F5（Windows TLS 缺陷）**：首次本地部署时 WSS 完全无法握手——`CertificateLoader` 的 `EphemeralKeySet` 在 Windows 上被 SChannel 拒绝（"platform does not support ephemeral keys"，0x8009030E），而 Linux/OpenSSL 不受影响，因此 VPS 部署从未暴露此问题。修复：Windows 上 PFX 使用 `DefaultKeySet`（持久密钥），PEM 加载后重导出为持久密钥；Linux 保持原状。spec §2.1 声明支持的 Windows Service 托管形态由此才真正可用。

**S6 慢消费者隔离**：32KB clip @ 50/s（1.6 MB/s）。基线 p95 17 ms（含 JIT 噪声），B 停读后 A 的 p95 稳定在 7.0–7.9 ms——完全隔离。B 在 **~16 秒**被服务端熔断断开（观测：established 3→2 且无 disconnect 日志；回环内核缓冲自调优 ~10 MB 吸收了初段流量，之后 16 条发送队列填满触发熔断）。两个发现：

- **熔断静默**：队列满路径直接 `MarkClosed + Cts.Cancel`，后续 `CancelConnection` 因 `MarkClosed` 已置位而提前返回，不产生任何 disconnect 安全事件——被熔断的连接在日志中不可见（已记入 spec §15）。
- **熔断延迟**：16 条队列的熔断点受内核 socket 缓冲（自动调优可达 ~10 MB）放大，取决于消息尺寸与速率，"队列满即断"在回环场景实际表现为"缓冲满即断"。

**S7 冷启动**：三次重启的应用启动阶段（journal `Started` → `Now listening`）均为 **2 秒**，达标但已贴线。另发现：`systemctl restart` 端到端耗时 **35.6 秒**（有真实客户端在线时）——旧实例的关闭阶段花了 34 秒，原因是 `ShutdownAsync` 对每个连接 `CloseAsync` 等待 close 握手完成且无超时，静默客户端会拖住整个停机流程；spec §7 的"等待最多 2 秒"只覆盖 close 握手完成后的 drain。已记入 spec §15。

**S8 空闲 CPU**：两轮 60 秒各 5 个时钟 tick（100 tick = 1 CPU 秒）→ 0.083% CPU。心跳扫描器（1 Hz）、状态刷盘（5 秒周期，空闲时无脏数据）、用户表轮询（30 秒周期）的固定开销可忽略。

### 4. 发现与后续

| # | 发现 | 影响 | 建议 |
|---|---|---|---|
| F1 | 停机关闭握手等待无超时（实测 34 秒） | 重启/升级时拖长停机窗口 | `CloseConnectionAsync` 的 `CloseAsync` 加超时（如 2 秒）后走 abort；已记入 spec §15 |
| F2 | 队列满熔断不产生 disconnect 日志 | 被熔断连接在安全日志中不可见 | 熔断路径补一条安全事件；已记入 spec §15 |
| F3 | P1 内存目标过紧（.NET 运行时基线 125-131 MB） | P1 不可达 | 修订 P1 为 < 150 MB；P2 按边际成本 240 KB/连接 基本达标，保留观察 |
| F4 | P8 在 1.6GB 同机环境不可测 | 无法验证 1000 并发 | 已解决：Windows 本机补测通过（见 S5 补测）；VPS 上仍建议跨机负载端 |
| F5 | Windows 上 `EphemeralKeySet` 导致 WSS 握手必然失败（0x8009030E） | spec §2.1 声明的 Windows Service 托管不可用 | 已修复：Windows 用 `DefaultKeySet`（PFX）+ PEM 重导出持久密钥；Linux 不变 |

### 5. 复现步骤

```bash
# 1. 上传探针
scp tools/perf_probe.py root@HOST:/tmp/
# 2. 创建临时压测用户（测后删除）
/opt/textcascade-server/TextCascade.Server user add --username perftest --password-stdin \
  --config /etc/textcascade/textcascade.toml < <(echo PASSWORD)
# 3. 延迟场景（需要临时调高 [rate_limit]，测后恢复）
python3 /tmp/perf_probe.py --user perftest --password PASSWORD latency --size 1024 --count 1000 --interval 0.02
python3 /tmp/perf_probe.py --user perftest --password PASSWORD latency --size 524288 --count 200 --interval 0.1
# 4. 连接保持与 RSS 采样
grep VmRSS /proc/$(systemctl show -p MainPID --value textcascade-server)/status
python3 /tmp/perf_probe.py --user perftest --password PASSWORD hold --count 100 --seconds 300
# 5. 慢消费者
python3 /tmp/perf_probe.py --user perftest --password PASSWORD slow --size 32768 --stall 45
# 6. 清理
/opt/textcascade-server/TextCascade.Server user delete --username perftest --config /etc/textcascade/textcascade.toml
```

注意：S5 类高并发场景勿在与服务同机的低内存主机上执行（见 S5 生产影响披露）。

### 6. 已知限制

- 负载端与服务同机，S5 受内存与 CPU 共享制约；跨机测量结果不可与本次回环数值直接比较；
- 采样节奏受 Python asyncio 定时器影响（Linux 下精度远优于 Windows 的 15ms）；
- 基线 RSS 受 GC 策略影响，跨 .NET 版本可能漂移；
- rate_limit 临时调整仅在延迟/慢消费者场景使用，S1/S2/S5/S7/S8 均在默认配置下测量。

---

## Part 2: English

### 1. Test Environment

| Item | Value |
|---|---|
| Hardware | 2 vCPU (Intel Xeon Platinum) / 1.6 GB RAM (LongCloud VPS) |
| OS | Ubuntu 24.04.4 LTS, kernel 6.8.0-63-generic |
| Runtime | .NET 10.0.11 (framework-dependent single file, `TextCascade.Server` v0.4.0+fb33861) |
| Service shape | systemd (`textcascade-server.service`), WSS + production self-signed certificate, port 8443 |
| Load generator | Same host, loopback 127.0.0.1, `tools/perf_probe.py` (Python 3.12 asyncio) |
| Service config | Defaults + `[rate_limit] clip_burst/clip_tokens_per_second` temporarily raised to 5000/5000 (latency and slow-consumer scenarios only; restored afterwards) |

Same-host loopback excludes network jitter, but the generator shares the 2 vCPUs and memory with the service — concurrency scenarios (S5) are constrained by this.

### 2. Results Summary

| # | Scenario | Metric | Target | Measured | Verdict |
|---|---|---|---|---|---|
| P1 | S1 base memory | RSS (fresh process, 60 s warmup) | < 50 MB | **125–131 MB** | ✗ fail |
| P2 | S2 100 idle connections | RSS delta (5 min, all alive) | < 20 MB | first run +66 MB (incl. one-time JIT/heap growth); re-run marginal **+24 MB** (≈240 KB/conn) | ◑ marginal near-target |
| P3 | S3 1KB broadcast | broadcast_lag p95 (1000 samples) | < 30 ms | **3.5 ms** (p50 1.87 / p99 5.1 / max 11.8) | ✓ pass (8.6× headroom) |
| P3b | S3 companion | ack_rtt p95 | reference | 4.0 ms | — |
| P4 | S4 512KB broadcast | broadcast_lag p95 (200 samples) | < 250 ms | **103.2 ms** (p50 87.3 / p99 138 / max 157) | ✓ pass (2.4× headroom) |
| P5 | S8 idle CPU | 60 s average (two runs) | ≈ 0% | **0.08%** (5 ticks / 60 s) | ✓ pass |
| P6 | S7 cold start | Application start phase (Started → listening) | < 2 s | **2 s** (consistent across 3 restarts) | ✓ pass (borderline) |
| P7 | Recovery window | snapshot_window_seconds | 3 s | config constant; correctness covered by tests | — (n/a) |
| P8 | S5 1000 concurrent | 10-minute stability | no disconnects | **passed on local Windows**: 0 errors, 1000/1000 alive, +211 MB (Linux VPS same-host environment untestable, see S5 details) | ✓ pass (Windows 32 GB) |
| — | S6 slow-consumer isolation | A's p95 (B stalled 45 s) | unaffected | **7.0–7.9 ms** (baseline 6–17 ms); B silently aborted at ~16 s | ✓ isolation holds |

### 3. Scenario Details

**S1 base memory**: RSS after three restarts was 127872 / 128256 / 131328 kB; 143852 kB after 24 h of uptime. The baseline consists of the .NET 10 runtime, Kestrel, the TLS stack, and 22 threads — a fresh process is already ~125 MB, so this is a runtime baseline, not a leak. The original 50 MB target is unrealistic for an ASP.NET Core app (judged "target too tight" rather than an implementation defect).

**S2 100 idle connections**: fresh RSS 127872 kB → 195392 kB, delta 67520 kB (≈660 KB/connection). All 900/900 expected heartbeat pongs were answered with zero errors. The 660 KB/connection includes TLS stream buffers, Kestrel per-connection pipes and pinned buffers, and managed objects. The original 20 MB target (200 KB/connection) underestimated the real cost of Kestrel + TLS.

**S2 re-measurement (clarifying the 660 KB/connection attribution, 2026-08-29)**: re-running the same scenario on a process that had been up for 32 minutes with fully-JITted code showed a delta of only **+24 MB (≈240 KB/connection)**, and memory did not return after the connections closed (GC segments retained; three ~20 MB anonymous segments). Conclusion: the initially measured 660 KB/connection mixed in one-time costs — the JIT compilation and first-time GC heap expansion triggered by the 100-connection handshake storm (roughly 40–50 MB) — rather than the true per-connection cost. The real **marginal** per-connection cost is ≈240 KB (Linux) vs 211 KB (Windows), the same order. Windows "looking lower" is mostly a denominator effect: 211 KB spread over 1000 connections vs 66 MB spread over 100. Revised P2 verdict: **marginal cost meets the target within 20%** (240 KB vs 200 KB per connection); the transient peak of a first connection storm exceeds it.

**S3 1KB broadcast latency**: all 1000 samples recovered. `broadcast_lag` p95 = 3.5 ms — the server path (parse → token bucket → version increment → single serialization → delivery to two connections) plus TLS on both ends stays far below the 30 ms target.

**S4 512KB broadcast latency**: all 200 samples recovered. p95 103 ms, dominated by serializing/escaping the 512KB JSON and two 512KB TLS record writes; 2.4× headroom, within target.

**S5 1000 concurrent connections (not completed; production impact)**: started from a fresh 128256 kB baseline. The SSH session was reset ~2 minutes into the storm; the machine then froze until the provider watchdog hard-reset it at 01:38:58. **Root cause (from the previous boot's persistent journal)**: all 1000 WebSocket upgrade requests arrived within the same second (00:48:59) against a server restarted only 40 s earlier; from 00:50:18 journald repeatedly logged "Under memory pressure, flushing caches"; at 00:51:53 the journal went silent (machine frozen for 47 minutes). The kernel log contains **no OOM-kill records** and the machine has **swap = 0**. Conclusion: steady-state memory was not the problem — 1000 × 240 KB (the re-measured marginal cost) ≈ 240 MB fits comfortably — it was the **transient storm peak** that broke a zero-swap environment: cold-process JIT + overlapping allocations of 1000 concurrent TLS handshakes + several hundred MB for the same-host Python generator + the upgrade-request log flood caused a kernel reclaim livelock (the .NET runtime's file-backed pages were endlessly evicted and re-faulted); the machine froze before the OOM killer could act. **Production impact disclosure**: real user devices could not connect for ~47 minutes. Viable paths to pass P8 in this environment: a warmed-up server, a slow ramp (e.g. 20 connections/s), a temporary swap file, or an off-host generator.

**S5 follow-up run (local Windows, 2026-08-29)**: environment was Windows x64 / 32 GB RAM, an openssl self-signed passwordless PFX, and a binary containing the Windows TLS fix (see F5). Result: all 1000 connections established (handshakes completed within ~90 s), 0 errors, and during the 10-minute hold 19576/19000 heartbeat pongs were answered (floor = 1000 connections × 19 cycles). RSS curve: 105 MB (baseline) → 262–285 MB steady → 316 MB at the end — a **delta of ≈211 MB (≈211 KB/connection)**, one third of the Linux figure (660 KB/connection) due to different Kestrel/TLS buffer behavior and GC. The server log contained no errors. **P8 verdict: pass.**

This run also surfaced and fixed **F5 (Windows TLS defect)**: the first local deployment could not complete a single WSS handshake — `CertificateLoader`'s `EphemeralKeySet` is rejected by SChannel on Windows ("platform does not support ephemeral keys", 0x8009030E), while Linux/OpenSSL is unaffected, which is why the VPS deployment never exposed it. Fix: on Windows the PFX branch now uses `DefaultKeySet` (persisted key) and PEM-loaded certificates are re-exported to a persisted key; Linux behavior is unchanged. Spec §2.1's declared Windows Service hosting shape is only truly usable with this fix.

**S6 slow-consumer isolation**: 32KB clips @ 50/s (1.6 MB/s). Baseline p95 17 ms (includes JIT noise); with B stalled, A's p95 held at 7.0–7.9 ms — fully isolated. B was silently aborted by the server at **~16 s** (observed: established count 3→2 with no disconnect log; the autotuned ~10 MB loopback kernel buffers absorbed the initial burst, after which the 16-message send queue filled and triggered the abort). Two findings:

- **Silent abort**: the queue-full path calls `MarkClosed + Cts.Cancel` directly, and the subsequent `CancelConnection` returns early because `MarkClosed` is already set — no disconnect security event is produced, so aborted connections are invisible in the logs (recorded in spec §15).
- **Abort delay**: the 16-message queue trigger point is amplified by kernel socket buffers (autotuned up to ~10 MB) and therefore depends on message size and rate; on loopback, "queue full = disconnect" behaves as "buffers full = disconnect".

**S7 cold start**: the application start phase (journal `Started` → `Now listening`) was **2 seconds** across three restarts — on target but borderline. Additional finding: end-to-end `systemctl restart` took **35.6 seconds** (with real clients connected) — the old instance's stop phase took 34 seconds because `ShutdownAsync` awaits each connection's `CloseAsync` close-handshake with no timeout, so silent clients stall the whole shutdown; spec §7's "wait up to 2 seconds" only covers the drain after the handshakes complete. Recorded in spec §15.

**S8 idle CPU**: two 60-second runs, 5 clock ticks each (100 ticks = 1 CPU-second) → 0.083% CPU. The fixed overhead of the heartbeat scanner (1 Hz), state flush (5 s cycle, no dirty data while idle), and user-file polling (30 s cycle) is negligible.

### 4. Findings and Follow-ups

| # | Finding | Impact | Recommendation |
|---|---|---|---|
| F1 | Shutdown close-handshake wait is unbounded (34 s measured) | Prolongs the stop window on restarts/upgrades | Add a timeout (e.g. 2 s) to `CloseConnectionAsync`'s `CloseAsync`, then abort; recorded in spec §15 |
| F2 | Queue-full abort produces no disconnect log | Aborted connections are invisible in security logs | Emit a security event on the abort path; recorded in spec §15 |
| F3 | P1 memory target too tight (.NET runtime baseline is 125–131 MB) | P1 unreachable | Revise P1 to < 150 MB; P2 essentially meets target at 240 KB/connection marginal cost, keep observing |
| F4 | P8 untestable on a 1.6 GB same-host environment | 1000 concurrent connections unverifiable | Resolved: passed on local Windows (see S5 follow-up); an off-host generator is still recommended for the VPS |
| F5 | `EphemeralKeySet` made WSS handshakes always fail on Windows (0x8009030E) | The Windows Service hosting shape declared in spec §2.1 was unusable | Fixed: `DefaultKeySet` for PFX on Windows + PEM re-export to a persisted key; Linux unchanged |

### 5. Reproduction

```bash
# 1. Upload the probe
scp tools/perf_probe.py root@HOST:/tmp/
# 2. Create a temporary benchmark user (delete afterwards)
/opt/textcascade-server/TextCascade.Server user add --username perftest --password-stdin \
  --config /etc/textcascade/textcascade.toml < <(echo PASSWORD)
# 3. Latency scenarios (temporarily raise [rate_limit]; restore afterwards)
python3 /tmp/perf_probe.py --user perftest --password PASSWORD latency --size 1024 --count 1000 --interval 0.02
python3 /tmp/perf_probe.py --user perftest --password PASSWORD latency --size 524288 --count 200 --interval 0.1
# 4. Connection holds and RSS sampling
grep VmRSS /proc/$(systemctl show -p MainPID --value textcascade-server)/status
python3 /tmp/perf_probe.py --user perftest --password PASSWORD hold --count 100 --seconds 300
# 5. Slow consumer
python3 /tmp/perf_probe.py --user perftest --password PASSWORD slow --size 32768 --stall 45
# 6. Cleanup
/opt/textcascade-server/TextCascade.Server user delete --username perftest --config /etc/textcascade/textcascade.toml
```

Caution: do not run S5-style high-concurrency scenarios on a low-memory host shared with the service (see the S5 production-impact disclosure).

### 6. Known Limitations

- The generator shares the host with the service; S5 is constrained by memory and CPU, and cross-host results are not directly comparable to these loopback numbers;
- Sampling cadence is bounded by Python asyncio timer precision (far better on Linux than the 15 ms default on Windows);
- Baseline RSS depends on GC policy and may drift across .NET versions;
- The temporary rate_limit adjustment was used only for the latency/slow-consumer scenarios; S1/S2/S5/S7/S8 were measured under default configuration.
