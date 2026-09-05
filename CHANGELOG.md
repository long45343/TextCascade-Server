# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0-Alpha] - 2026-09-05

### Changed
- Full server rewrite in Go (branch `go`), a function-level 1:1 port of the C# v0.5.0 implementation with a zero-change external contract: wire protocol and byte-exact serialization, close codes, error codes, TOML keys, environment variables, CLI behavior and defaults are identical. The branch contains no C# code; the C# implementation remains on `main`.
- Stack mapping (decisions Q1–Q15 in `docs/go-server-spec.md`): `gorilla/websocket` for WebSocket, `pelletier/go-toml/v2` for TOML, `gofrs/flock` for the CLI single-instance lock, `fsnotify` + 250 ms debounce + 30 s poll fallback for `users.json` watching, `golang.org/x/crypto/argon2` for Argon2id, `golang.org/x/term` for interactive passwords, `software.sslmate.com/src/go-pkcs12` for password-less PFX/P12, `log/slog` with a custom single-line handler for logging.
- Inbound JSON is validated by a hand-written token-level pre-scanner (`internal/protocol/jsonscan.go`): strict UTF-8, nesting depth, integer-only number literals, and escape/control-character rules; semantic layers replicate the C# unknown/duplicate-field checks in document order.
- Graceful shutdown keeps the C# behavior 1:1: bye → 1001 → 2 s drain → cancel all connections → synchronous flush, including the documented no-timeout close-handshake wait.
- Deployed to production  and switched over from the C# build after a parallel-instance verification; real clients migrate transparently with existing tokens and passwords.
- Performance re-measured per the perf.md scenarios (`perf.md`, 2026-09-05): every target now passes at its original threshold — steady-state RSS 11.7 MB (previously 125–131 MB), marginal per-connection cost 83 KB (previously ≈ 240 KB), 1 KB broadcast p95 2.11 ms (previously 3.5 ms), 512 KB p95 90.6 ms (previously 103.2 ms); 1000 concurrent connections pass with a cross-machine load generator (same-host generators are infeasible on the 1.6 GB / 2 vCPU VPS for both runtimes).
- The slow-consumer (S6) scenario documentation was removed from perf.md; the probe retains the capability for future cross-machine runs.

### Fixed
- Argon2 password hashes created by the C# build verify directly against the Go build: Argon2id is a standardized algorithm and Isopoh's output is byte-identical to `x/crypto` for the same (password, salt, m, t, p) — Isopoh's only quirk is choosing its own lane count and recording it honestly in the PHC string (verified bidirectionally on the development machine and in production, with pinned interop contract tests in `internal/auth`). Existing users migrate without password resets; tokens issued by the C# build remain valid.

### Added
- Test suite ported 1:1 from xUnit: unit tests, the full contract-sample matrix (`testdata/contract-samples/`, byte-identical to the C# suite), WebSocket integration tests over plaintext HTTP, and 12 real-TLS network integration cases, plus slow-hash smoke tests with production Argon2 parameters and pinned Isopoh interop contract tests.
- CI (`ci.yml`) and release (`release.yml`) workflows rewritten for Go: gofmt/vet/test with race detection, contract-sample checksum verification, and a linux-x64/win-x64 static-binary release matrix with `-X main.version` injection.

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

[Unreleased]: https://github.com/long45343/TextCascade-Server/compare/v1.0.0-Alpha...HEAD
[1.0.0-Alpha]: https://github.com/long45343/TextCascade-Server/compare/v0.5.0...v1.0.0-Alpha
[0.5.0]: https://github.com/long45343/TextCascade-Server/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/long45343/TextCascade-Server/compare/v0.3.5...v0.4.0
[0.3.5]: https://github.com/long45343/TextCascade-Server/compare/v0.3.0...v0.3.5
[0.3.0]: https://github.com/long45343/TextCascade-Server/compare/v0.2.5...v0.3.0
[0.2.5]: https://github.com/long45343/TextCascade-Server/compare/v0.2.1...v0.2.5
[0.2.1]: https://github.com/long45343/TextCascade-Server/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/long45343/TextCascade-Server/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/long45343/TextCascade-Server/releases/tag/v0.1.0
