# Contract Samples

服务端协议契约样本，供三端（C# 服务端 / C# 桌面端 / Kotlin Android 端）对拍。

## 目录语义（目录名即期望结果）

| 目录 | 期望 |
|---|---|
| `valid/` | `ParseClientMessage` 成功，字段逐项匹配 |
| `invalid/duplicate-field/` | 失败，`invalid_message` |
| `invalid/unknown-field/` | 失败，`invalid_message` |
| `invalid/number/` | 失败，`invalid_message`（含小数/指数/字符串数字/负数/超 ulong/类型污染/非 UTC 时间） |
| `invalid/depth-4/` | 失败（MaxDepth=3），`invalid_message` |
| `invalid/utf8/` | `.bin` 原始字节帧，失败，`invalid_message` |

驱动器（ContractSampleTests）按一级子目录名推断期望错误码，缺省 `invalid_message`。

## 无原生数值字段的等价覆盖说明

`clip` 没有数值字段、`pong` 的 `clientTimeUtc` 是时间字符串，因此数字形态在这些消息上以"字段类型污染"等价覆盖（同一 Utf8JsonReader 数字/类型分支）：

- clip.encrypted 字符串化 → TryGetBoolean 分支
- clip.hash 数字化 → TryGetString 分支
- pong.clientTimeUtc 数字化 / 无 Z 后缀 → TryGetUtcDateTime 分支
- hello.snapshot 带 +02:00 偏移 → 非零 Offset 拒绝分支

## Token 直测样本

token payload 的负数 / 小数 / 字符串数字样本内联于 `AuthDeepTests`（不走样本文件，因其直接调用 `TokenService.TryVerifyToken`）。