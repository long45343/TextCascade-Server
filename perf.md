# 性能实测报告 / Performance Measurement Report

状态 / Status：v1.0.0-Alpha（Go 版）实测报告 / Measured on v1.0.0-Alpha (Go implementation)  
日期 / Date：2026-09-05  
测量工具 / Tooling：[tools/perf_probe.py](tools/perf_probe.py)（纯标准库 asyncio WSS 探针，支持 `--host/--port` 跨机测量 / stdlib-only asyncio WSS probe with `--host/--port` for cross-machine runs）  
关联 / Related：[docs/go-server-spec.md](docs/go-server-spec.md) §1.1、§12、§13；[specs/go-migration-decisions.md](specs/go-migration-decisions.md)（私有 / private）

> 摘要 / Summary：Go 版全部性能目标达成：稳态 RSS 11.7 MB（P1 目标 < 50 MB）、每连接边际成本 83 KB（P2 目标 < 200 KB/连接）、1KB 广播 p95 2.11 ms（P3 目标 < 30 ms）、512KB 广播 p95 90.6 ms（P4 目标 < 250 ms）、空闲 CPU 0.1%、999/1000 并发连接在跨机负载端下稳定保持 10 分钟且服务器 RSS 仅约 59 MB（唯一一次握手失败来自公网路径）。与前一版 .NET 实现的实测基线相比，内存基线降至约 1/10，每连接成本降至约 1/3，延迟全面略优。1.6 GB / 2 vCPU 生产 VPS 上的同机 1000 并发压测对两种运行时都不可行（.NET 触发内存回收活锁，Go 触发用户态 CPU 饥饿），S5 须使用跨机负载端。  
> Summary: The Go build meets every performance target: steady-state RSS 11.7 MB (P1 target < 50 MB), marginal per-connection cost 83 KB (P2 target < 200 KB/connection), 1 KB broadcast p95 2.11 ms (P3 target < 30 ms), 512 KB broadcast p95 90.6 ms (P4 target < 250 ms), idle CPU 0.1%, and 999/1000 concurrent connections held stably for 10 minutes with a cross-machine generator at ~59 MB server RSS (the single handshake failure was on the WAN path). Compared with the measured baseline of the previous .NET implementation, the runtime baseline dropped to about one tenth, per-connection cost to about one third, and latency improved across the board. Same-host 1000-connection testing on the 1.6 GB / 2 vCPU production VPS is infeasible for both runtimes (.NET hit a memory-reclaim livelock, Go a userland CPU-starvation livelock); S5 requires a cross-machine generator.

---

## 第一部分：中文

### 1. 测试环境

| 项 | 值 |
|---|---|
| 硬件 | 2 vCPU（Intel Xeon Platinum）/ 1.6 GB 内存（LongCloud VPS） |
| 系统 | Ubuntu 24.04 LTS，内核 6.8 |
| 运行时 | Go 1.27 静态单文件（`TextCascade.Server` v1.0.0-Alpha，CGO_ENABLED=0） |
| 服务形态 | systemd（`textcascade-server.service`），WSS + 生产 PEM 证书，端口 8443 |
| 负载端（延迟/连接保持） | 与服务同机，回环 127.0.0.1，`tools/perf_probe.py`（Python 3.12 asyncio） |
| 负载端（S5 1000 并发） | 跨机：Windows x64 办公网络 → 公网 8443，同探针 `--host` 参数 |
| 服务配置 | 默认值；延迟场景临时调高 `[rate_limit] clip_burst/clip_tokens_per_second` 至 2000/500（测后已恢复默认） |

延迟场景在同机回环上测量以排除网络抖动；S5 使用跨机负载端——同机负载端已被证实无法支撑 1000 并发场景（见 S5 明细），这一约束与被测端运行时无关。

### 2. 结果总表

| # | 场景 | 指标 | 目标 | 实测 | 判定 |
|---|---|---|---|---|---|
| P1 | S1 基础内存 | 稳态 RSS（含真实流量） | < 50 MB | **11.7 MB**（3 条真实连接 + 真实 clip 流量，运行 10 分钟） | ✓ 达标 |
| P2 | S2 300 空闲连接 | RSS 边际增量 | < 200 KB/连接 | **83 KB/连接**（+24.9 MB / 300，全部存活，0 错误） | ✓ 达标 |
| P3 | S3 1KB 广播 | broadcast_lag p95（1000 样本） | < 30 ms | **2.11 ms**（p50 1.72 / p99 2.77 / max 4.06） | ✓ 达标（14× 余量） |
| P3b | S3 附带 | ack_rtt p95 | 参考 | 1.97 ms | — |
| P4 | S4 512KB 广播 | broadcast_lag p95（200 样本） | < 250 ms | **90.6 ms**（p50 85.0 / p99 113 / max 133） | ✓ 达标（2.8× 余量） |
| P5 | S8 空闲 CPU | ps %CPU 均值（10 分钟窗口） | ≈ 0% | **0.1%**（含 3 条真实连接与真实 clip 流量） | ✓ 达标 |
| P6 | S7 冷启动 | 应用启动阶段（Started → listening） | < 2 s | **1.4 s**（实测见 S7 明细） | ✓ 达标 |
| P7 | 恢复窗口 | snapshot_window_seconds | 3 s | 配置常量，功能由集成测试覆盖 | —（不适用） |
| P8 | S5 1000 并发 | 10 分钟稳定性（跨机负载端） | 无断连 | **通过**：999/1000 建立并保持（1 次公网握手超时），0 服务端错误，RSS ≈ 59 MB（同机负载端不可行，见 S5 明细） | ✓ 达标 |

前一版 .NET 实现（v0.4.0，2026-08-27/29 实测）的基线供对照：P1 稳态 RSS 125–131 MB；P2 边际约 240 KB/连接（Linux）/ 211 KB/连接（Windows）；P3 p95 3.5 ms；P4 p95 103.2 ms；P8 仅在 32 GB Windows 本机通过（+211 MB）。Go 版在全部场景上优于或持平该基线，原被判"过紧"的 P1/P2 目标在 Go 版上以原始阈值直接达标。

### 3. 各场景明细

**S1 基础内存**：生产环境直接观测——服务运行 10 分钟、承载 3 条真实设备连接并处理真实 clip 广播时，VmRSS 为 11.7 MB；冷启动阶段的 VmHWM 峰值约 29 MB，随后回落。基线由 Go 运行时（8 线程）、TLS 栈与监听器构成。前一版 .NET 实现的稳态基线为 125–131 MB（22 线程），迁移的内存动机由此兑现：运行时基线降至约 1/10。

**S2 连接保持与每连接成本**：以 300 条空闲连接（64 并发握手建立）保持 48 秒，期间每 4 秒采样 VmRSS。压测前基线 83.3 MB（含此前的 512KB 延迟场景堆残留，Go GC 惰性归还未用堆段），保持期间峰值 129 MB（握手风暴瞬时分配），稳定段 107–108 MB；结束后连接全部关闭，RSS 108.3 MB。边际增量 (108.3 − 83.3) / 300 ≈ 83 KB/连接。每连接成本由 goroutine 栈、TLS 流缓冲与 gorilla 读写缓冲构成；与 .NET 的 240 KB/连接（Linux）相比降至约 1/3。Go 的堆归还策略使延迟场景的残留会在空闲后被 scavenger 逐步回收，属正常行为而非泄漏。

**S3 1KB 广播延迟**：1000 样本全数回收。`broadcast_lag` p95 = 2.11 ms——服务端路径（jsonscan 预扫描 → 语义解析 → 令牌桶 → 版本自增 → 单次序列化 → 双连接投递）加上两端 TLS 回环开销，低于前一版 .NET 的 3.5 ms，14 倍余量达标。

**S4 512KB 广播延迟**：200 样本全数回收。p95 90.6 ms，主要成本在 512KB JSON 的转义与两次 512KB TLS 记录写，与 .NET 的 103.2 ms 同量级、略优，2.8 倍余量达标。

**S5 1000 并发连接**：跨机负载端通过——从 Windows x64 办公网络经公网对生产 VPS 尝试建立 1000 条 WSS 连接（64 并发握手，约 2 分钟完成），999 条成功建立并保持 10 分钟，1 条在公网路径握手超时（服务端无任何对应错误记录）；心跳 pong 19501 次全部应答（下限 18981），服务器 VmRSS 全程约 59 MB（基线 12 MB + 1000 连接 × 约 47 KB），load average 0.05，系统可用内存 918 MB。服务日志无任何错误。真实用户连接全程共存不受影响。

同机负载端在该 VPS 上对两种运行时均不可行，机制不同：

- .NET（v0.4.0 时期实测）：1000 个升级请求风暴打到冷进程后，journald 连续输出 "Under memory pressure, flushing caches" 直至静默，整机冻结 47 分钟（宿主机 watchdog 硬重启）。根因是零 swap 环境下冷进程 JIT、并发 TLS 握手、同机负载端自身的内存叠加触发内核回收活锁。
- Go（v1.0.0-Alpha 实测）：同机 hold 压测开至约 580 连接时整机用户态冻结约 34 分钟（同样需要人工强制重启）。与 .NET 机制不同：服务器 VmRSS 全程 ≤ 141 MB（2 秒间隔看门狗采样），journal 无任何内存压力或 OOM 签名，日志在 connect 风暴中戛然而止，TCP 层仍可接受连接但 sshd 等用户态进程不再被调度——2 vCPU 被服务端握手与同机 Python 负载端叠加耗尽，表现为用户态饥饿型 livelock。

两次事故的共性结论：该 1.6 GB / 2 vCPU 环境的瓶颈在"同机负载端 + 被测端共享 2 vCPU"，与被测端运行时无关。S5 的可行路径是跨机负载端（本次已采用），生产影响披露：两次同机事故分别造成真实用户约 47 分钟与 34 分钟无法连接。

**S7 冷启动**：`systemctl restart` 后以 20 ms 间隔轮询 `/health` 直至 200，Go 版进程启动到可服务的实测为 1.4 秒（静态二进制无运行时预热，含证书加载与登录时序防御所需的 Argon2 哑哈希计算及轮询粒度误差）。前一版 .NET 为 2 秒。`systemctl restart` 的端到端耗时仍由停机路径主导：优雅停机对每条连接等待 close 握手且无超时（spec §13.8 记录的现状），真实客户端在线时表现为立即完成（客户端回显 close 帧），静默客户端最坏可拖长停机。

**S8 空闲 CPU**：生产进程 10 分钟窗口的 ps %CPU 均值 0.1%（期间有真实连接与真实 clip 流量）。心跳扫描器（1 Hz）、状态刷盘（5 秒周期）、用户表轮询（30 秒周期）的固定开销可忽略。

### 4. 发现与后续

| # | 发现 | 影响 | 建议 |
|---|---|---|---|
| F1 | 同机 1000 并发压测对该 VPS 不可行，且机制随运行时不同（.NET 内存回收活锁 / Go 用户态 CPU 饥饿） | S5 无法用同机负载端验证 | 已解决：跨机负载端（`--host` 公网直连）验证通过；勿再在该 VPS 发起同机高并发压测 |
| F2 | Go GC 惰性归还使延迟场景后的 RSS 残留达数十 MB，需数分钟逐步回落 | 读数解读需区分基线与残留 | 如需快速回落可设 `GOGC`/`GOMEMLIMIT`；当前规模无必要 |
| F3 | 用户表事件驱动重载存在未复现的单次丢失（一次 CLI 写文件后的登录跑在了防抖重载前，被 30 秒轮询兜底覆盖） | 极端情况下配置变更最多延迟 30 秒生效 | 属 Q9 轮询兜底的设计行为；观察期内若复现再深入排查 inotify |

### 5. 复现步骤

```bash
# 1. 同机场景：上传探针
scp tools/perf_probe.py root@HOST:/tmp/
# 2. 创建临时压测用户（测后删除）
/opt/textcascade-server/TextCascade.Server user add --username perftest --password-stdin \
  --config /etc/textcascade/textcascade.toml < <(echo PASSWORD)
# 3. 延迟场景（需要临时调高 [rate_limit]，测后恢复默认）
python3 /tmp/perf_probe.py --user perftest --password PASSWORD latency --size 1024 --count 1000 --interval 0.05
python3 /tmp/perf_probe.py --user perftest --password PASSWORD latency --size 524288 --count 200 --interval 0.2
# 4. 连接保持与 RSS 采样
grep VmRSS /proc/$(systemctl show -p MainPID --value textcascade-server)/status
python3 /tmp/perf_probe.py --user perftest --password PASSWORD hold --count 300 --seconds 45
# 5. 清理
/opt/textcascade-server/TextCascade.Server user delete --username perftest --config /etc/textcascade/textcascade.toml
```

```bash
# S5 跨机场景（负载端 = 任一有 Python 3 的工作机，无需上传探针到服务器）
python tools/perf_probe.py --host server.example.com --port 8443 \
  --user perftest --password PASSWORD hold --count 1000 --seconds 600
```

注意：同机高并发压测勿在低内存/少核主机上执行（见 S5 生产影响披露）；S5 类场景一律使用跨机负载端。

### 6. 已知限制

- 延迟场景的令牌桶依赖：默认 `clip_tokens_per_second = 2` 会在持续压测时截断样本（约 2 样本/秒），延迟类场景必须临时调高 `[rate_limit]`，测后恢复默认。
- S5 的跨机路径经过公网，握手阶段受办公网络带宽与 RTT 影响（64 并发握手约 2 分钟完成 1000 连接）；结果反映服务器侧承载能力，不构成 WAN 延迟基线。
- RSS 读数受 Go 堆归还策略影响（见 F2），建议以"同方法、同前置条件"做纵向对比而非绝对值对比。

---

## Part 2: English

### 1. Test Environment

| Item | Value |
|---|---|
| Hardware | 2 vCPU (Intel Xeon Platinum) / 1.6 GB RAM (LongCloud VPS) |
| OS | Ubuntu 24.04 LTS, kernel 6.8 |
| Runtime | Go 1.27 static single binary (`TextCascade.Server` v1.0.0-Alpha, CGO_ENABLED=0) |
| Service | systemd (`textcascade-server.service`), WSS + production PEM certificate, port 8443 |
| Load generator (latency/hold) | Same host, loopback 127.0.0.1, `tools/perf_probe.py` (Python 3.12 asyncio) |
| Load generator (S5, 1000 connections) | Cross-machine: Windows x64 office network → public 8443, same probe with `--host` |
| Server config | Defaults; `[rate_limit] clip_burst/clip_tokens_per_second` temporarily raised to 2000/500 for latency scenarios (restored afterwards) |

Latency scenarios run on loopback to exclude network jitter; S5 uses a cross-machine generator — a same-host generator has been proven unable to sustain the 1000-connection scenario regardless of the server runtime (see S5 details).

### 2. Results

| # | Scenario | Metric | Target | Measured | Verdict |
|---|---|---|---|---|---|
| P1 | S1 Base memory | Steady-state RSS (with real traffic) | < 50 MB | **11.7 MB** (3 real connections + real clip traffic, 10 min uptime) | ✓ Pass |
| P2 | S2 300 idle connections | Marginal RSS growth | < 200 KB/connection | **83 KB/connection** (+24.9 MB / 300, all alive, 0 errors) | ✓ Pass |
| P3 | S3 1 KB broadcast | broadcast_lag p95 (1000 samples) | < 30 ms | **2.11 ms** (p50 1.72 / p99 2.77 / max 4.06) | ✓ Pass (14× headroom) |
| P3b | S3 companion | ack_rtt p95 | Reference | 1.97 ms | — |
| P4 | S4 512 KB broadcast | broadcast_lag p95 (200 samples) | < 250 ms | **90.6 ms** (p50 85.0 / p99 113 / max 133) | ✓ Pass (2.8× headroom) |
| P5 | S8 Idle CPU | ps %CPU average (10 min window) | ≈ 0% | **0.1%** (3 real connections + real clip traffic included) | ✓ Pass |
| P6 | S7 Cold start | Application start phase (Started → listening) | < 2 s | **1.4 s** (see S7 details) | ✓ Pass |
| P7 | Recovery window | snapshot_window_seconds | 3 s | Configuration constant, covered by integration tests | — (n/a) |
| P8 | S5 1000 connections | 10-minute stability (cross-machine generator) | No disconnects | **Pass**: 999/1000 established and held (1 WAN handshake timeout), 0 server-side errors, RSS ≈ 59 MB (same-host generator infeasible, see S5 details) | ✓ Pass |

The previous .NET implementation (v0.4.0, measured 2026-08-27/29) serves as the baseline for comparison: P1 steady-state RSS 125–131 MB; P2 marginal ≈ 240 KB/connection (Linux) / 211 KB/connection (Windows); P3 p95 3.5 ms; P4 p95 103.2 ms; P8 passed only on a 32 GB Windows machine (+211 MB). The Go build matches or beats that baseline in every scenario, and the targets once judged "too tight" (P1/P2) are met with their original thresholds.

### 3. Scenario Details

**S1 Base memory**: observed directly in production — with 10 minutes of uptime, 3 real device connections and real clip broadcasts, VmRSS was 11.7 MB; the cold-start VmHWM peak was about 29 MB and settles afterwards. The baseline consists of the Go runtime (8 threads), the TLS stack and the listeners. The previous .NET implementation idled at 125–131 MB (22 threads); the migration's memory motivation is realized with the runtime baseline reduced to about one tenth.

**S2 Connection hold and per-connection cost**: 300 idle connections (established with 64 concurrent handshakes) held for 48 seconds with VmRSS sampled every 4 seconds. The pre-test baseline was 83.3 MB (heap residue from the earlier 512 KB latency scenarios; the Go GC returns unused heap lazily), the hold peaked at 129 MB (transient handshake allocations), then settled at 107–108 MB; after all connections closed, RSS read 108.3 MB. The marginal growth is (108.3 − 83.3) / 300 ≈ 83 KB per connection, consisting of goroutine stacks, TLS stream buffers and gorilla read/write buffers — about one third of .NET's 240 KB/connection (Linux). The Go heap's lazy return means post-latency residue decays gradually via the scavenger; this is normal behavior, not a leak.

**S3 1 KB broadcast latency**: all 1000 samples recovered. `broadcast_lag` p95 = 2.11 ms — the server path (jsonscan pre-scan → semantic parse → token bucket → version increment → single serialization → two-connection delivery) plus TLS loopback overhead beats the previous .NET figure of 3.5 ms with 14× headroom.

**S4 512 KB broadcast latency**: all 200 samples recovered. p95 90.6 ms; the cost is dominated by escaping the 512 KB JSON and two 512 KB TLS record writes — same magnitude as .NET's 103.2 ms, slightly better, 2.8× headroom.

**S5 1000 concurrent connections**: passes with a cross-machine generator — 1000 WSS connection attempts from a Windows x64 office network over the public internet to the production VPS (64 concurrent handshakes, completed in about 2 minutes); 999 established and held for 10 minutes, 1 handshake timed out on the WAN path (no corresponding server-side record); all heartbeat pongs answered (19501 sent vs floor 18981); server VmRSS stayed around 59 MB (12 MB baseline + 1000 × ≈ 47 KB), load average 0.05, 918 MB system memory available. No errors in the service logs. Real user connections coexisted unaffected.

A same-host generator is infeasible on this VPS for both runtimes, with different mechanisms:

- .NET (measured in the v0.4.0 era): after a storm of 1000 upgrade requests hit a cold process, journald printed "Under memory pressure, flushing caches" continuously until silence, and the box froze for 47 minutes (host watchdog hard reset). Root cause: on a zero-swap host, cold-process JIT, concurrent TLS handshakes and the same-host Python generator's own memory triggered a kernel reclaim livelock.
- Go (measured on v1.0.0-Alpha): a same-host hold test froze all userland at about 580 connections for about 34 minutes (also requiring a manual forced restart). The mechanism differs from .NET: server VmRSS stayed ≤ 141 MB throughout (watchdog sampling every 2 seconds), the journal shows no memory-pressure or OOM signature and ends abruptly mid-connect-storm, TCP still accepted connections but sshd and other userland processes were no longer scheduled — the 2 vCPUs were exhausted by server-side handshakes plus the same-host Python generator, presenting as a userland CPU-starvation livelock.

The common conclusion from both incidents: the bottleneck of this 1.6 GB / 2 vCPU environment is the same-host load generator sharing 2 vCPUs with the server, independent of the server runtime. S5's viable path is a cross-machine generator (used this time). Production impact disclosure: the two same-host incidents caused about 47 and 34 minutes of unavailability for the real user respectively.

**S7 Cold start**: polling `/health` every 20 ms after `systemctl restart` until HTTP 200, the Go build serves its first request 1.4 seconds after process start (a static binary needs no runtime warm-up; the figure includes certificate loading, the timing-defense Argon2 dummy hash and polling granularity). The previous .NET implementation took 2 seconds. The end-to-end duration of `systemctl restart` is still dominated by the shutdown path: graceful shutdown waits for each connection's close handshake without a timeout (the current behavior recorded in spec §13.8); with responsive clients it completes immediately (the client echoes the close frame), while a silent client can extend the worst case.

**S8 Idle CPU**: the production process averaged 0.1% ps %CPU over a 10-minute window that included real connections and real clip traffic. The fixed costs of the heartbeat scanner (1 Hz), state flush (5 s period) and users-file reload (30 s period) are negligible.

### 4. Findings and Follow-ups

| # | Finding | Impact | Recommendation |
|---|---|---|---|
| F1 | Same-host 1000-connection testing is infeasible on this VPS, with runtime-specific mechanisms (.NET memory-reclaim livelock / Go userland CPU starvation) | S5 cannot be validated with a same-host generator | Resolved: cross-machine generator (`--host` over the public internet) passes; do not repeat same-host high-concurrency tests on this VPS |
| F2 | The Go GC's lazy heap return leaves tens of MB of RSS residue after latency scenarios, decaying over minutes | Readings must distinguish baseline from residue | Set `GOGC`/`GOMEMLIMIT` for faster decay if ever needed; unnecessary at the current scale |
| F3 | One unreproduced miss of the event-driven users-file reload (a login issued right after a CLI write raced the debounced reload; the 30-second poll covered it) | Configuration changes may take up to 30 seconds to take effect in the worst case | This is the designed behavior of the Q9 poll fallback; investigate inotify deeper only if it recurs during the observation period |

### 5. Reproduction

```bash
# 1. Same-host scenarios: upload the probe
scp tools/perf_probe.py root@HOST:/tmp/
# 2. Create a temporary test user (delete afterwards)
/opt/textcascade-server/TextCascade.Server user add --username perftest --password-stdin \
  --config /etc/textcascade/textcascade.toml < <(echo PASSWORD)
# 3. Latency scenarios (temporarily raise [rate_limit]; restore defaults afterwards)
python3 /tmp/perf_probe.py --user perftest --password PASSWORD latency --size 1024 --count 1000 --interval 0.05
python3 /tmp/perf_probe.py --user perftest --password PASSWORD latency --size 524288 --count 200 --interval 0.2
# 4. Connection hold with RSS sampling
grep VmRSS /proc/$(systemctl show -p MainPID --value textcascade-server)/status
python3 /tmp/perf_probe.py --user perftest --password PASSWORD hold --count 300 --seconds 45
# 5. Cleanup
/opt/textcascade-server/TextCascade.Server user delete --username perftest --config /etc/textcascade/textcascade.toml
```

```bash
# S5 cross-machine scenario (generator = any machine with Python 3; no probe upload to the server needed)
python tools/perf_probe.py --host server.example.com --port 8443 \
  --user perftest --password PASSWORD hold --count 1000 --seconds 600
```

Note: never run same-host high-concurrency tests on low-memory/small-CPU hosts (see the S5 production impact disclosure); always use a cross-machine generator for S5-class scenarios.

### 6. Known Limitations

- Token-bucket dependency of latency scenarios: the default `clip_tokens_per_second = 2` truncates sustained-load samples (about 2 samples/second); latency scenarios must temporarily raise `[rate_limit]` and restore it afterwards.
- The S5 cross-machine path traverses the public internet; the handshake phase is bounded by office-network bandwidth and RTT (1000 connections in about 2 minutes with 64 concurrent handshakes). The results reflect server-side capacity, not a WAN latency baseline.
- RSS readings are affected by the Go heap return policy (see F2); compare longitudinally with the same method and preconditions rather than against absolute values.
