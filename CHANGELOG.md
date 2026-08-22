# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/long45343/TextCascade-Server/compare/v0.2.5...HEAD
[0.2.5]: https://github.com/long45343/TextCascade-Server/compare/v0.2.1...v0.2.5
[0.2.1]: https://github.com/long45343/TextCascade-Server/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/long45343/TextCascade-Server/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/long45343/TextCascade-Server/releases/tag/v0.1.0
