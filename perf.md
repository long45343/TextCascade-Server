# 性能测试规范 / Performance Test Specification

状态 / Status：测量契约与结果记录模板（Measurement contract and results template），v0.4.0  
日期 / Date：2026-08-27

> **说明 / Note**：基准压测程序（独立 Benchmark 项目）尚未实现；本文件先于工具存在，定义性能目标、场景、测量方法与结果记录格式，作为后续构建度量设施的契约。  
> **Note**：The standalone benchmark project does not exist yet. This document precedes the tooling and defines the performance targets, scenarios, measurement methodology, and the results format — the contract for building the measurement harness.

---

## 第一部分：中文

### 1. 背景与目的

`docs/server-spec.md` 原第 9 节的性能指标表在 v0.4.0 规格对齐时移除，原因是项目从未建立度量手段，未度量数字等于虚假承诺。本文件把目标重新引入，但赋予其可执行的语义：每一项目标都绑定明确的场景、采样方法与判定标准；结果填入第 6 节模板，未填写的行即"未验证"。

设计性质（目标为何可达，来自架构）：

- 广播每用户仅做一次 UTF-8 序列化，同一份字节投递到所有连接；
- 每连接发送队列有界（默认 16 条），慢连接立即取消，内存上限可预测；
- 每用户一个 Channel 单消费者，无锁竞争路径；
- 空闲期固定开销：1 秒一次的心跳扫描器、RuntimeStateStore 每 5 秒脏检查刷盘、UserFileWatcher 每 30 秒轮询兜底。

### 2. 性能目标

| # | 指标 / Metric | 目标 / Target |
|---|---|---|
| P1 | 基础进程内存（无客户端，预热后 RSS） | < 50 MB |
| P2 | 100 个空闲连接内存增量（保持 5 分钟） | < 20 MB |
| P3 | 1KB 文本广播单向延迟 p95（同机回环） | < 30 ms |
| P4 | 512KB 文本广播单向延迟 p95（同机回环） | < 250 ms |
| P5 | 空闲 CPU 占用（60 秒均值） | ≈ 0%（心跳扫描、状态刷盘与用户表轮询除外） |
| P6 | 冷启动时间（进程拉起 → `/health` 返回 200） | < 2 s |
| P7 | 重启恢复窗口 | 固定 3 秒（`snapshot_window_seconds`，功能正确性由测试保障） |
| P8 | 1000 并发连接稳定性（保持 10 分钟） | 无断连、无错误日志、内存增量可解释 |

### 3. 测试环境要求

每次记录结果必须附带环境描述，否则结果不可比：

- 硬件：CPU 型号与核数、内存容量（如 2 vCPU / 2 GB VPS）；
- 系统：OS 与内核版本；
- 运行时：.NET Runtime 版本、部署形态（框架依赖单文件）、`DOTNET_` 环境变量；
- 网络：回环（127.0.0.1）或真实局域网，WSS（生产 TLS）或 WS（仅诊断）；
- 负载端：与被测服务的相对位置（同机 / 另一主机）。

参考环境建议：与生产部署一致（systemd + WSS + 自签证书），客户端与服务器同机回环，排除网络抖动。

### 4. 测试场景

| # | 场景 | 步骤 | 采样 |
|---|---|---|---|
| S1 | 基础内存 | 启动服务，无客户端，预热 60 秒后读 RSS | 单点 ×3 取中位 |
| S2 | 100 空闲连接 | 建 100 个 WSS 连接并发送合法 hello，保持 5 分钟 | 前后 RSS 差值 |
| S3 | 1KB 广播延迟 | 同用户 2 连接：A 发 1KB clip，A 记 ACK 往返，B 记广播单向滞后 | 预热丢弃 50，采样 ≥1000，间隔 20ms |
| S4 | 512KB 广播延迟 | 同 S3，payload 512KB | 预热丢弃 10，采样 ≥200，间隔 100ms |
| S5 | 1000 并发连接 | 建 1000 连接完成 hello，保持 10 分钟 | 全程 RSS/CPU 曲线 + 断连计数 |
| S6 | 慢消费者隔离 | A 每 20ms 发 4KB clip；B 建立后停止读 socket 10 秒 | A 的 p95 不受影响；B 应在队列满（16 条）后被断开 |
| S7 | 冷启动 | `systemctl restart`，从进程拉起到 `/health` 200 | 3 次取中位 |
| S8 | 空闲 CPU | 无客户端 60 秒，取 `ps -o %cpu` 均值 | 单点 ×3 取中位 |

延迟指标定义：

- **ACK 往返（ack_rtt）**：A 端 `send(clip)` 前取时间戳，收到本连接 `clip_ack` 再取，差值即 RTT；
- **广播单向滞后（broadcast_lag）**：A 端 `send(clip)` 前取 `t0`，B 端收到含该 id 的 `clip` 帧取 `t1`，`t1 - t0` 即单向滞后（同机时钟，无偏差问题；跨主机需先做时钟校准或改测 ACK 往返的一半）。

P3/P4 以 `broadcast_lag` 的 p95 判定；`ack_rtt` 一并记录作参考。

### 5. 执行方法

**内存与 CPU（现成工具即可）**：

```bash
# RSS（字节）
grep VmRSS /proc/$(systemctl show -p MainPID --value textcascade-server)/status
# systemd 视角内存
systemctl status textcascade-server --no-pager | grep Memory
# 60 秒平均 CPU
ps -o %cpu= -p $(systemctl show -p MainPID --value textcascade-server) --sort=-start_time
# 更细的 GC/线程池观测（可选）
dotnet-counters monitor --process-id <pid> System.Runtime
```

**冷启动**：

```bash
systemctl restart textcascade-server
# journalctl 中 "Started" 与 "Now listening on" 两条时间戳之差，或循环 curl /health 直到 200
```

**延迟与并发负载**：独立压测程序尚未实现。过渡期任选其一：

1. 通用 WebSocket 压测工具（如 k6）按 S3/S5 参数执行；
2. 一次性控制台脚本（C# `ClientWebSocket` 或 Python `websockets`），逻辑为：登录 → 建连 → hello → 定时发 clip → 记录 `clip_ack` 时间戳；
3. 实现 `TextCascade.Server.Benchmark` 控制台项目（持久方案，场景按第 4 节命名 S1–S8）。

k6 示例（S3 的 ACK 往返部分，TOKEN/主机替换后使用；广播滞后需第二个静态接收端）：

```javascript
import ws from 'k6/ws';
import { Trend } from 'k6/metrics';

const ackRtt = new Trend('ack_rtt_ms');

export default function () {
  ws.connect('wss://HOST:8443/api/v1/sync', { headers: { Authorization: 'Bearer TOKEN' } }, (socket) => {
    socket.on('open', () => {
      socket.send(JSON.stringify({ type: 'hello', clientId: 'k6-a', clientName: 'k6', lastServerVersion: 0, snapshot: null }));
      socket.on('message', (raw) => {
        if (raw.includes('welcome')) {
          for (let i = 0; i < 1000; i++) {
            const t0 = Date.now();
            socket.send(JSON.stringify({ type: 'clip', id: 'c' + i, payload: 'x'.repeat(1024), encrypted: false, hash: 'h' + i }));
          }
        } else if (raw.includes('clip_ack')) {
          ackRtt.add(Date.now() - Number(raw.match(/"id":"c(\d+)"/)[1]) * 20);
        }
      });
    });
  });
}
```

注意：示例按"每 20ms 发一条"的节奏需配合限速循环，实际脚本应使用 `setInterval` 或分批发送；以上仅示意数据采集点。

### 6. 结果记录模板

| 场景 | 指标 | 目标 | 实测 | 环境 | 日期 | 结论 |
|---|---|---|---|---|---|---|
| S1 | RSS 中位 | < 50 MB | TBD | TBD | TBD | ☐ |
| S2 | ΔRSS | < 20 MB | TBD | TBD | TBD | ☐ |
| S3 | broadcast_lag p95 | < 30 ms | TBD | TBD | TBD | ☐ |
| S3 | ack_rtt p95 | 参考 | TBD | TBD | TBD | ☐ |
| S4 | broadcast_lag p95 | < 250 ms | TBD | TBD | TBD | ☐ |
| S5 | 10 分钟稳定性 | 无断连 | TBD | TBD | TBD | ☐ |
| S6 | 慢连接隔离 | B 断开且 A p95 达标 | TBD | TBD | TBD | ☐ |
| S7 | 冷启动中位 | < 2 s | TBD | TBD | TBD | ☐ |
| S8 | 空闲 CPU 均值 | ≈ 0% | TBD | TBD | TBD | ☐ |

填写规则：环境列引用第 3 节描述的编号；结论列在目标达成时打勾，未达标时在文末追加差距分析（原因 + 归属：实现 / 环境 / 目标本身）。

### 7. 已知限制

- 基准压测程序未实现，过渡期依赖通用工具或临时脚本，采样节奏精度受客户端定时器分辨率影响（Windows 默认 ~15ms）；
- P7 恢复窗口为配置常量，其"3 秒"语义已在集成测试中验证，本文件只做部署侧确认；
- 同机回环测量排除了网络抖动，跨主机结果不可直接与回环目标比较；
- RuntimeStateStore 刷盘（5 秒周期）与 UserFileWatcher 轮询（30 秒周期）是空闲开销的一部分，属预期行为而非回归。

---

## Part 2: English

### 1. Background and Purpose

The performance target table in the original `docs/server-spec.md` §9 was removed during the v0.4.0 spec alignment because the project never had measurement tooling — unmeasured numbers are empty promises. This document reintroduces the targets with executable semantics: every target is bound to a concrete scenario, sampling method, and pass criterion. Results go into the template in section 6; an unfilled row means "not verified".

Design properties that make the targets achievable (from the architecture):

- Each broadcast is serialized to UTF-8 once per user; the same bytes are handed to every connection;
- Per-connection send queues are bounded (16 messages by default) and slow connections are cancelled immediately, keeping the memory ceiling predictable;
- One single-consumer Channel per user — no lock contention on the hot path;
- Fixed idle overhead: the 1-second heartbeat scanner, RuntimeStateStore dirty-check flush every 5 seconds, and the UserFileWatcher 30-second polling fallback.

### 2. Performance Targets

| # | Metric | Target |
|---|---|---|
| P1 | Base process memory (RSS after warmup, no clients) | < 50 MB |
| P2 | Memory delta with 100 idle connections (held 5 minutes) | < 20 MB |
| P3 | 1KB text broadcast one-way latency p95 (same-host loopback) | < 30 ms |
| P4 | 512KB text broadcast one-way latency p95 (same-host loopback) | < 250 ms |
| P5 | Idle CPU usage (60-second average) | ≈ 0% (excluding heartbeat scan, state flush, user-file polling) |
| P6 | Cold start (process spawn → `/health` returns 200) | < 2 s |
| P7 | Restart recovery window | Fixed 3 s (`snapshot_window_seconds`; correctness covered by tests) |
| P8 | 1000 concurrent connections stability (held 10 minutes) | No disconnects, no error logs, explainable memory delta |

### 3. Environment Requirements

Every recorded result must include its environment description, otherwise results are not comparable:

- Hardware: CPU model and core count, RAM (e.g. 2 vCPU / 2 GB VPS);
- OS: distribution and kernel version;
- Runtime: .NET Runtime version, deployment shape (framework-dependent single file), `DOTNET_*` environment variables;
- Network: loopback (127.0.0.1) or real LAN; WSS (production TLS) or WS (diagnostics only);
- Load generator: relative location to the service under test (same host / separate host).

Recommended reference environment: identical to production deployment (systemd + WSS + self-signed certificate), clients on the same host over loopback to exclude network jitter.

### 4. Scenarios

| # | Scenario | Steps | Sampling |
|---|---|---|---|
| S1 | Base memory | Start service with no clients; read RSS after 60 s warmup | 3 runs, take median |
| S2 | 100 idle connections | Open 100 WSS connections with valid hello; hold 5 minutes | RSS delta before/after |
| S3 | 1KB broadcast latency | Same-user 2 connections: A sends 1KB clips; A records ACK round-trip, B records broadcast lag | Discard 50 warmup, ≥1000 samples at 20 ms interval |
| S4 | 512KB broadcast latency | Same as S3 with 512KB payload | Discard 10 warmup, ≥200 samples at 100 ms interval |
| S5 | 1000 concurrent connections | Open 1000 connections with hello; hold 10 minutes | Full RSS/CPU curve + disconnect count |
| S6 | Slow-consumer isolation | A sends 4KB clip every 20 ms; B stops reading its socket for 10 s | A's p95 unaffected; B disconnected after queue fills (16 messages) |
| S7 | Cold start | `systemctl restart`; measure from spawn to `/health` 200 | 3 runs, take median |
| S8 | Idle CPU | No clients for 60 s; average `ps -o %cpu` | 3 runs, take median |

Latency metric definitions:

- **ACK round-trip (ack_rtt)**: timestamp before `send(clip)` on A, again when this connection's `clip_ack` arrives; the difference is the RTT;
- **Broadcast lag (broadcast_lag)**: `t0` before A sends the clip, `t1` when B receives the `clip` frame with that id; `t1 - t0` is the one-way lag (same-host clocks, no skew problem; across hosts, calibrate clocks first or measure half the ACK round-trip instead).

P3/P4 are judged on the p95 of `broadcast_lag`; `ack_rtt` is recorded alongside for reference.

### 5. How to Run

**Memory and CPU (existing tools suffice)**:

```bash
# RSS (bytes)
grep VmRSS /proc/$(systemctl show -p MainPID --value textcascade-server)/status
# systemd view of memory
systemctl status textcascade-server --no-pager | grep Memory
# 60-second average CPU
ps -o %cpu= -p $(systemctl show -p MainPID --value textcascade-server)
# Finer GC/thread-pool observation (optional)
dotnet-counters monitor --process-id <pid> System.Runtime
```

**Cold start**:

```bash
systemctl restart textcascade-server
# Difference between the "Started" and "Now listening on" journal timestamps,
# or poll /health with curl until it returns 200.
```

**Latency and concurrent load**: the dedicated benchmark project does not exist yet. Until then, pick one:

1. A general-purpose WebSocket load tool (e.g. k6) driven with the S3/S5 parameters;
2. A throwaway console script (C# `ClientWebSocket` or Python `websockets`): login → connect → hello → send clips on a timer → timestamp `clip_ack`;
3. Implement the `TextCascade.Server.Benchmark` console project (the durable option; scenarios named S1–S8 per section 4).

k6 sketch (the ACK round-trip part of S3; replace TOKEN/HOST; broadcast lag needs a second static receiver):

```javascript
import ws from 'k6/ws';
import { Trend } from 'k6/metrics';

const ackRtt = new Trend('ack_rtt_ms');

export default function () {
  ws.connect('wss://HOST:8443/api/v1/sync', { headers: { Authorization: 'Bearer TOKEN' } }, (socket) => {
    socket.on('open', () => {
      socket.send(JSON.stringify({ type: 'hello', clientId: 'k6-a', clientName: 'k6', lastServerVersion: 0, snapshot: null }));
      socket.on('message', (raw) => {
        if (raw.includes('welcome')) {
          for (let i = 0; i < 1000; i++) {
            const t0 = Date.now();
            socket.send(JSON.stringify({ type: 'clip', id: 'c' + i, payload: 'x'.repeat(1024), encrypted: false, hash: 'h' + i }));
          }
        } else if (raw.includes('clip_ack')) {
          ackRtt.add(Date.now() - Number(raw.match(/"id":"c(\d+)"/)[1]) * 20);
        }
      });
    });
  });
}
```

Note: the sketch assumes one clip every 20 ms; a real script should pace sends with `setInterval` or batching. It illustrates the data-collection points only.

### 6. Results Template

| Scenario | Metric | Target | Measured | Environment | Date | Pass |
|---|---|---|---|---|---|---|
| S1 | RSS median | < 50 MB | TBD | TBD | TBD | ☐ |
| S2 | ΔRSS | < 20 MB | TBD | TBD | TBD | ☐ |
| S3 | broadcast_lag p95 | < 30 ms | TBD | TBD | TBD | ☐ |
| S3 | ack_rtt p95 | reference | TBD | TBD | TBD | ☐ |
| S4 | broadcast_lag p95 | < 250 ms | TBD | TBD | TBD | ☐ |
| S5 | 10-minute stability | no disconnects | TBD | TBD | TBD | ☐ |
| S6 | slow-consumer isolation | B dropped, A p95 within target | TBD | TBD | TBD | ☐ |
| S7 | cold start median | < 2 s | TBD | TBD | TBD | ☐ |
| S8 | idle CPU average | ≈ 0% | TBD | TBD | TBD | ☐ |

Filling rules: the Environment column references the description from section 3; tick the Pass column when the target is met, and append a gap analysis at the end of this file when it is not (root cause + attribution: implementation / environment / the target itself).

### 7. Known Limitations

- The benchmark project is not implemented; interim tooling depends on generic clients or throwaway scripts, and sampling cadence accuracy is bounded by client timer resolution (~15 ms by default on Windows);
- The P7 recovery window is a configuration constant whose 3-second semantics are already verified by integration tests; this document only confirms it from the deployment side;
- Same-host loopback measurements exclude network jitter; cross-host results must not be compared directly against the loopback targets;
- RuntimeStateStore flushing (5-second cycle) and UserFileWatcher polling (30-second cycle) are part of the idle overhead — expected behavior, not regressions.
