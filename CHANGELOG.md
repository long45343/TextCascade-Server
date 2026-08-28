# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Documentation
- Added bilingual (中文/English) performance test specification `perf.md`: reintroduces the performance targets with executable scenarios, sampling methodology, k6 sketch, and a results template; `docs/server-spec.md` §9 now defers to it.

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

[Unreleased]: https://github.com/long45343/TextCascade-Server/compare/v0.3.5...HEAD
[0.3.5]: https://github.com/long45343/TextCascade-Server/compare/v0.3.0...v0.3.5
[0.3.0]: https://github.com/long45343/TextCascade-Server/compare/v0.2.5...v0.3.0
[0.2.5]: https://github.com/long45343/TextCascade-Server/compare/v0.2.1...v0.2.5
[0.2.1]: https://github.com/long45343/TextCascade-Server/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/long45343/TextCascade-Server/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/long45343/TextCascade-Server/releases/tag/v0.1.0
