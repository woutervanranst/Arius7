## MODIFIED Requirements

### Requirement: FileTreeService shared filetree blob cache
The system SHALL provide a `FileTreeService` in `Arius.Core/Shared/FileTree/` that centralizes all filetree blob reads and writes through a local disk cache at `~/.arius/{accountName}-{containerName}/filetrees/`. The service SHALL be registered in DI as a singleton (one per `AddArius` call). The constructor SHALL accept `IBlobContainerService`, `IEncryptionService`, `string accountName`, and `string containerName`. `FileTreeService` SHALL NOT depend on `ChunkIndexService`.

#### Scenario: Service instantiation
- **WHEN** `FileTreeService` is constructed with account `myacct` and container `photos`
- **THEN** the disk cache directory SHALL be `~/.arius/myacct-photos/filetrees/` and SHALL be created if it does not exist

#### Scenario: Service follows naming convention
- **WHEN** examining the service registration
- **THEN** `FileTreeService` SHALL follow the same `{Thing}Service` pattern as `ChunkIndexService`, located in `Shared/FileTree/`

#### Scenario: Service has no chunk-index dependency
- **WHEN** `FileTreeService` is constructed or validates filetree cache state
- **THEN** it SHALL NOT require a `ChunkIndexService` dependency

### Requirement: FileTreeService.ValidateAsync
`FileTreeService.ValidateAsync(CancellationToken)` SHALL compare the latest local snapshot marker against the latest remote snapshot to determine the fast/slow path. The method SHALL:
1. Enumerate `~/.arius/{repo}/snapshots/` to find timestamp-named marker files, sort lexicographically, and take the latest. If no markers exist, treat as mismatch.
2. Call `ListAsync("snapshots/")` to enumerate remote snapshots and find the latest timestamp.
3. Compare local latest vs remote latest: match = fast path, mismatch = slow path.
4. On slow path: call `ListAsync("filetrees/")` and for each remote blob name, create an empty file at `~/.arius/{repo}/filetrees/{hash}` if not already present.

`FileTreeService.ValidateAsync` SHALL return a `FileTreeValidationResult` record containing a `SnapshotMismatch` boolean. `SnapshotMismatch` SHALL be true when the latest remote snapshot exists and does not match the latest local snapshot marker, or when no local marker exists for a non-empty remote repository. `FileTreeService.ValidateAsync` SHALL NOT invalidate chunk-index cache state. Archive-level coordination SHALL use the validation result to decide when to invalidate mutable chunk-index caches.

#### Scenario: Snapshot match — fast path
- **WHEN** the latest local marker is `2026-03-22T150000.000Z` and the latest remote snapshot is also `2026-03-22T150000.000Z`
- **THEN** `ValidateAsync` SHALL NOT call `ListAsync("filetrees/")`

#### Scenario: Snapshot mismatch — slow path
- **WHEN** the latest local marker is `2026-03-21T100000.000Z` but the latest remote snapshot is `2026-03-22T150000.000Z`
- **THEN** `ValidateAsync` SHALL call `ListAsync("filetrees/")`
- **AND** it SHALL create empty marker files on disk for each remote filetree blob that is not already cached
- **AND** it SHALL return `FileTreeValidationResult` with `SnapshotMismatch` set

#### Scenario: Slow path does not overwrite existing cache files
- **WHEN** a snapshot mismatch triggers the slow path and `~/.arius/{repo}/filetrees/abc123` already exists with content
- **THEN** `ValidateAsync` SHALL NOT overwrite the existing file (it already satisfies the existence check)

#### Scenario: No local markers — slow path
- **WHEN** no marker files exist in `~/.arius/{repo}/snapshots/` (first archive on this machine, or directory is empty)
- **THEN** `ValidateAsync` SHALL treat this as a mismatch and take the slow path

#### Scenario: No remote snapshots — fast path
- **WHEN** `ListAsync("snapshots/")` returns no results (brand new repository)
- **THEN** `ValidateAsync` SHALL set fast-path mode (nothing to invalidate, no remote blobs to prefetch)
- **AND** it SHALL return `FileTreeValidationResult` with `SnapshotMismatch` unset

#### Scenario: Snapshot match returns no mismatch
- **WHEN** the latest local marker matches the latest remote snapshot
- **THEN** `ValidateAsync` SHALL return `FileTreeValidationResult` with `SnapshotMismatch` unset

#### Scenario: Validate does not invalidate chunk index
- **WHEN** a snapshot mismatch is detected by `FileTreeService.ValidateAsync`
- **THEN** `FileTreeService` SHALL NOT delete chunk-index cache files
- **AND** it SHALL NOT call `ChunkIndexService.InvalidateL1()`
