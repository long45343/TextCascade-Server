# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.5.0] - 2026-09-04

### Changed
- Replaced hand-rolled helpers with standard library equivalents, with no behavior change: `TokenService` base64url codec now uses `System.Buffers.Text.Base64Url` (.NET 9 BCL) instead of a custom encode/decode pair; the `IClock`/`SystemClock` seam was replaced by `System.TimeProvider` (registered in DI, tests pass `TimeProvider.System`); `HeartbeatScannerService` was rewritten from a `System.Threading.Timer` callback to `PeriodicTimer` inside `BackgroundService` (scan exceptions are now logged instead of being unobserved).
- Login request parsing (`AuthService.ParseLoginRequest`) now performs a single strict `JsonSerializer.DeserializeAsync` pass with .NET 10 `AllowDuplicateProperties = false` and `JsonUnmappedMemberHandling.Disallow`, replacing the manual 4 KB chunked read loop plus a double `JsonDocument`/`JsonSerializer` parse; the 16 KB cap is enforced via `IHttpMaxRequestBodySizeFeature` (chunked bodies included). Structural violations previously reported as "Invalid login request." now return "Invalid JSON." — status 400 and the `invalid_request` code are unchanged.
- `SingleInstanceLock` no longer probes or recovers PIDs: the lock file (`users.json.lock`) is opened with `FileMode.OpenOrCreate` + `FileShare.None`, so the OS releases it when the holder process dies and a leftover file from a crash is simply reopened instead of blocking. The PID written into the file is diagnostic only. Contention behavior (3 retries, then graceful failure) is unchanged.

### Fixed
- TLS server authentication failed on Windows with "platform does not support ephemeral keys" (0x8009030E): `CertificateLoader` now loads PFX certificates with a persisted key set (`DefaultKeySet`) on Windows (Linux keeps `EphemeralKeySet`), and PEM-loaded certificates are re-exported to a persisted key on Windows. Spec §2.1's Windows Service hosting shape required this to be usable. Found while benchmarking 1000 concurrent connections on a local Windows deployment.

### Documentation
- perf.md: added the Windows local 1000-connection result — 10-minute hold with 0 errors and all 1000 connections alive, RSS delta ≈211 MB (≈211 KB/connection, one third of the Linux figure); P8 is now verified.
- Rewrote `perf.md` as a bilingual (中文/English) actual performance measurement report for v0.4.0 on the production VPS: 1KB broadcast p95 3.5 ms (8.6× headroom), 512KB p95 103 ms, idle CPU 0.08%, cold start 2 s; memory targets (P1/P2) recorded as unmet and flagged for revision; the 1000-connection scenario is documented as untestable on a 1.6 GB same-host environment after it exhausted memory and forced a VM reset.
- Added `tools/perf_probe.py`, a stdlib-only asyncio WSS probe used for the measurements (hold/latency/slow-consumer scenarios).
- Recorded two implementation findings in `docs/server-spec.md` §15: unbounded shutdown close-handshake wait (34 s stop phase observed) and silent queue-full abort without a disconnect security event.

## [0.4.0] - 2026-08-27

### Added
- Contract test suite with JSON sample corpus (`ContractSamples/`) covering duplicate fields, unknown fields, illegal number forms, depth-4 nesting, and invalid UTF-8, plus byte-level serialization invariants for welcome/clip/ack/ping/error/token payloads.
- Network integration test suite (`Category=NetworkIntegration`, 12 cases) over real Kestrel TLS with runtime-generated self-signed certificates: WSS handshake, TLS 1.2/1.3 protocol probes, random port binding, real frame fragmentation, oversize/zero-length frame closes (1009), server restart with token direct reconnect and persisted-version baseline, snapshot election restore, and graceful-shutdown bye/1001 chain.
- Slow-hash smoke tests (`Category=SlowHash`) exercising the real Argon2 Hash/Verify/NeedsRehash chain with production parameters.
- Unit tests closing spec §10.1 gaps: token duplicate-field/illegal-number/range rejection, CLI watermark allocation, delete-and-recreate watermark behavior, revoke and overflow fail-fast with byte-identical file preservation, `WithVersion` immutability, and behavior-level duplicate-id idempotency (drained token bucket still acks duplicates; reused id with new content treated as fresh message).

### Changed
- CI main test job now includes the SlowHash category and excludes only `Category=NetworkIntegration`, which runs in a dedicated CI job.
- Release workflow test step aligned with the same category filter.
- Server spec (`docs/server-spec.md`) rewritten to match v0.3.5 implementation: hot user-file reload, RuntimeStateStore version persistence, content-comparing clip idempotency semantics, 10-minute idle hub recycling, actual log event fields, and a new implementation-gap ledger (§15). Never-implemented items (benchmark project, `server_stop` event, performance target table) removed.
- Added `specs/test-and-contract-spec.md` (function-level test and contract specification) and `specs/spec-decisions.md` (decision record for the spec alignment).

## [0.3.5] - 2026-08-22

### Changed
- Refactored `RuntimeStateStore` to lock-free memory CAS updates using `ConcurrentDictionary` and background periodic flush (`PeriodicTimer`) with atomic snapshot persistence, eliminating synchronous disk I/O bottlenecks in the clip synchronization pipeline.
- Added graceful shutdown flush hook to `SyncServer.ShutdownAsync` ensuring all pending version increments are flushed upon service stop.
- Added concurrency, background periodic flush, and fault-tolerance unit tests for `RuntimeStateStore`.

## [0.3.0] - 2026-08-22

### Added
- Native ASP.NET Core `CreateApp` factory and comprehensive end-to-end WebSocket integration tests covering handshake, broadcast, snapshots, invalid tokens, and abrupt disconnections.
- Hot-reloading of `users.json` via file system watcher with debounced reload and periodic fallback.
- Constant-time password verification on login for non-existent users using a cached dummy hash.

### Changed
- Refactored `SyncServer` by splitting models, hubs, and hosting services into dedicated domain files (`Models/`, `Hub/`, `Hosting/`).
- Decoupled `UserHub` from `SyncServer` using lightweight `IConnectionCoordinator` interface.
- Protected `UserHub.StartIfIdle` with mutex lock and added single-reader re-entrancy protection to `RunUserLoopAsync`.
- Optimized `SeenIdRing` deduplication with hash map lookup (`Dictionary<string, LatestText?>`) and FIFO circular eviction queue.
- Migrated PEM certificate and private key loading to native .NET APIs (`X509Certificate2.CreateFromPemFile` and `X509Certificate2Collection.ImportFromPemFile`).
- Relocated CLI single-instance lock file adjacent to the target `users.json` (`users.json.lock`).
- Switched WebSocket JSON message parsing to `ReadOnlyMemory<byte>` to eliminate redundant byte array copies (`frame.ToArray()`).

### Fixed
- Fixed sliding window login rate limiter to clean up only expired timestamps from queue head rather than evicting the entire key.

### Security
- Eliminated username existence timing side-channel during login authentication.

## [0.2.5] - 2026-08-22

### Added
- Version persistence across server restarts using `RuntimeStateStore` (`textcascade.state.json`).
- Reconnection snapshot recovery window during server startup.

### Changed
- Refactored snapshot selection tie-breaking and broadcast logic.

### Fixed
- Fixed server protocol bugs in version negotiation and error responses.

## [0.2.1] - 2026-08-18

### Fixed
- Fixed server protocol bugs in message deserialization and error framing.

## [0.2.0] - 2026-08-18

### Added
- Cross-platform release workflows and CI matrix for Linux and Windows single-file binaries.
- Bilingual README and documentation.

## [0.1.0] - 2026-08-18

### Added
- Initial import and baseline release of TextCascade Server with Minimal API, Kestrel WebSocket, Argon2 password hashing, and token authentication.

[Unreleased]: https://github.com/long45343/TextCascade-Server/compare/v0.4.0...HEAD
[0.4.0]: https://github.com/long45343/TextCascade-Server/compare/v0.3.5...v0.4.0
[0.3.5]: https://github.com/long45343/TextCascade-Server/compare/v0.3.0...v0.3.5
[0.3.0]: https://github.com/long45343/TextCascade-Server/compare/v0.2.5...v0.3.0
[0.2.5]: https://github.com/long45343/TextCascade-Server/compare/v0.2.1...v0.2.5
[0.2.1]: https://github.com/long45343/TextCascade-Server/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/long45343/TextCascade-Server/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/long45343/TextCascade-Server/releases/tag/v0.1.0
