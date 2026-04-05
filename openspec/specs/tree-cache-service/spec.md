# Tree Cache Service Spec

## Purpose

Defines `TreeCacheService`: a singleton service that centralizes all filetree blob reads and writes through a local disk cache, eliminating redundant Azure downloads for tree blobs during archive, restore, and list operations. It also owns fast-path / slow-path validation logic to determine whether the local disk cache is complete or needs to be pre-populated from the remote.

## Requirements

### Requirement: TreeCacheService shared filetree blob cache
The system SHALL provide a `TreeCacheService` in `Arius.Core/Shared/FileTree/` that centralizes all filetree blob reads and writes through a local disk cache at `~/.arius/{accountName}-{containerName}/filetrees/`. The service SHALL be registered in DI as a singleton (one per `AddArius` call). The constructor SHALL accept `IBlobContainerService`, `IEncryptionService`, `string accountName`, and `string containerName` — the same dependency pattern as `ChunkIndexService`.

#### Scenario: Service instantiation
- **WHEN** `TreeCacheService` is constructed with account `myacct` and container `photos`
- **THEN** the disk cache directory SHALL be `~/.arius/myacct-photos/filetrees/` and SHALL be created if it does not exist

#### Scenario: Service follows naming convention
- **WHEN** examining the service registration
- **THEN** `TreeCacheService` SHALL follow the same `{Thing}Service` pattern as `ChunkIndexService`, located in `Shared/FileTree/`

### Requirement: TreeCacheService.ReadAsync
`TreeCacheService.ReadAsync(string hash, CancellationToken)` SHALL return a deserialized `TreeBlob` by checking the local disk cache first, then falling back to Azure download. On cache miss, the downloaded blob SHALL be deserialized and written to the disk cache (plaintext JSON, matching the existing `EnsureUploadedAsync` cache format) before returning. On cache hit, the blob SHALL be read from disk and deserialized without any remote call. The method SHALL handle deserialization and decryption internally via `TreeBlobSerializer`.

#### Scenario: Cache hit
- **WHEN** `ReadAsync("abc123")` is called and `~/.arius/{repo}/filetrees/abc123` exists on disk
- **THEN** the service SHALL read from disk, deserialize, and return the `TreeBlob` without any Azure call

#### Scenario: Cache miss
- **WHEN** `ReadAsync("abc123")` is called and no local file exists
- **THEN** the service SHALL download from `filetrees/abc123` in Azure, deserialize, write plaintext JSON to `~/.arius/{repo}/filetrees/abc123`, and return the `TreeBlob`

#### Scenario: Concurrent reads for same hash
- **WHEN** multiple concurrent calls to `ReadAsync` request the same hash
- **THEN** the service SHALL not corrupt the disk cache file (writes SHALL be atomic or serialized per hash)

### Requirement: TreeCacheService.WriteAsync
`TreeCacheService.WriteAsync(string hash, TreeBlob, CancellationToken)` SHALL serialize the tree blob, upload to Azure at `filetrees/{hash}`, and write the plaintext JSON to the local disk cache. This is a write-through operation. The upload SHALL use `overwrite: false` semantics (content-addressed blobs are immutable). If the blob already exists in Azure (`BlobAlreadyExistsException`), the upload SHALL be silently skipped but the disk cache SHALL still be written.

#### Scenario: Write new tree blob
- **WHEN** `WriteAsync("def456", treeBlob)` is called for a new blob
- **THEN** the service SHALL serialize, upload to Azure at `filetrees/def456`, and write plaintext JSON to `~/.arius/{repo}/filetrees/def456`

#### Scenario: Write existing tree blob
- **WHEN** `WriteAsync("def456", treeBlob)` is called and the blob already exists in Azure
- **THEN** the upload SHALL be silently skipped (catch `BlobAlreadyExistsException`) and the disk cache SHALL still be written

### Requirement: TreeCacheService.ExistsInRemote
`TreeCacheService.ExistsInRemote(string hash)` SHALL return a `bool` indicating whether the tree blob exists in remote storage by checking the disk cache via `File.Exists`. This works on both paths:
- **Fast path** (snapshot match): The disk cache is already complete — this machine uploaded all blobs and they are immutable.
- **Slow path** (snapshot mismatch): `ValidateAsync` has materialized empty marker files on disk for all remote blobs, so `File.Exists` is reliable.

`ExistsInRemote` SHALL only be called after `ValidateAsync` has completed. Calling it before `ValidateAsync` SHALL throw `InvalidOperationException`.

#### Scenario: Blob exists on disk (any path)
- **WHEN** `~/.arius/{repo}/filetrees/abc123` exists (either as a full cache file or an empty marker)
- **THEN** `ExistsInRemote("abc123")` SHALL return `true` without any remote call

#### Scenario: Blob not on disk (any path)
- **WHEN** no file exists at `~/.arius/{repo}/filetrees/abc123`
- **THEN** `ExistsInRemote("abc123")` SHALL return `false`

#### Scenario: Called before ValidateAsync
- **WHEN** `ExistsInRemote` is called before `ValidateAsync` has completed
- **THEN** the service SHALL throw `InvalidOperationException`

### Requirement: TreeCacheService.ValidateAsync
`TreeCacheService.ValidateAsync(CancellationToken)` SHALL compare the latest local snapshot marker against the latest remote snapshot to determine the fast/slow path. The method SHALL:
1. Enumerate `~/.arius/{repo}/snapshots/` to find timestamp-named marker files, sort lexicographically, and take the latest. If no markers exist, treat as mismatch.
2. Call `ListAsync("snapshots/")` to enumerate remote snapshots and find the latest timestamp.
3. Compare local latest vs remote latest: match = fast path, mismatch = slow path.
4. On slow path: call `ListAsync("filetrees/")` and for each remote blob name, create an empty file at `~/.arius/{repo}/filetrees/{hash}` if not already present. Also delete all files in `~/.arius/{repo}/chunk-index/` and call `ChunkIndexService.InvalidateL1()` to invalidate stale shard data both on disk and in memory.

#### Scenario: Snapshot match — fast path
- **WHEN** the latest local marker is `2026-03-22T150000.000Z` and the latest remote snapshot is also `2026-03-22T150000.000Z`
- **THEN** `ValidateAsync` SHALL NOT call `ListAsync("filetrees/")`

#### Scenario: Snapshot mismatch — slow path
- **WHEN** the latest local marker is `2026-03-21T100000.000Z` but the latest remote snapshot is `2026-03-22T150000.000Z`
- **THEN** `ValidateAsync` SHALL call `ListAsync("filetrees/")`, create empty marker files on disk for each remote blob, delete all files in `~/.arius/{repo}/chunk-index/`, and call `ChunkIndexService.InvalidateL1()` to invalidate stale shard data both on disk and in memory

#### Scenario: Slow path does not overwrite existing cache files
- **WHEN** a snapshot mismatch triggers the slow path and `~/.arius/{repo}/filetrees/abc123` already exists with content
- **THEN** `ValidateAsync` SHALL NOT overwrite the existing file (it already satisfies the existence check)

#### Scenario: No local markers — slow path
- **WHEN** no marker files exist in `~/.arius/{repo}/snapshots/` (first archive on this machine, or directory is empty)
- **THEN** `ValidateAsync` SHALL treat this as a mismatch and take the slow path

#### Scenario: No remote snapshots — fast path
- **WHEN** `ListAsync("snapshots/")` returns no results (brand new repository)
- **THEN** `ValidateAsync` SHALL set fast-path mode (nothing to invalidate, no remote blobs to prefetch)

#### Scenario: Chunk-index L2 invalidation on mismatch
- **WHEN** a snapshot mismatch is detected
- **THEN** all files in `~/.arius/{repo}/chunk-index/` SHALL be deleted to force `ChunkIndexService` to re-download from Azure on next access

### Requirement: Snapshot disk cache update
After the archive pipeline successfully creates a new snapshot, `SnapshotService.CreateAsync` SHALL write the full JSON manifest to `~/.arius/{repo}/snapshots/` named by the snapshot timestamp (e.g., `2026-03-22T150000.000Z`), using the same `SnapshotService.TimestampFormat`. This is a write-through operation - the same content is uploaded to Azure and persisted locally. The directory `~/.arius/{repo}/snapshots/` SHALL be created if it does not exist.

`SnapshotService.ResolveAsync` SHALL remain disk-first: read from local JSON if present, fall back to Azure download and cache locally on miss. `SnapshotService` SHALL accept `accountName` and `containerName` as constructor parameters alongside its existing dependencies, and it SHALL be registered in the DI container as a singleton, consistent with `ChunkIndexService` and `TreeCacheService`, with its constructor accepting `IBlobContainerService`, `IEncryptionService`, `string accountName`, and `string containerName`.

#### Scenario: Snapshot JSON written after archive
- **WHEN** the archive pipeline creates snapshot `2026-03-22T150000.000Z`
- **THEN** `SnapshotService.CreateAsync` SHALL write the full JSON manifest to `~/.arius/{repo}/snapshots/2026-03-22T150000.000Z`

#### Scenario: Snapshot disk file written on subsequent archive
- **WHEN** a second archive creates snapshot `2026-03-23T080000.000Z`
- **THEN** an additional JSON file `~/.arius/{repo}/snapshots/2026-03-23T080000.000Z` SHALL be created (previous files remain)

#### Scenario: Snapshots directory created if missing
- **WHEN** the `~/.arius/{repo}/snapshots/` directory does not exist
- **THEN** `SnapshotService.CreateAsync` SHALL create it before writing the manifest file

#### Scenario: ResolveAsync reads from disk on cache hit
- **WHEN** `ResolveAsync` is called and the snapshot JSON file exists on disk
- **THEN** the snapshot SHALL be deserialized from local JSON without any Azure call

#### Scenario: ResolveAsync falls back to Azure on cache miss
- **WHEN** `ResolveAsync` is called and no local file exists
- **THEN** the snapshot SHALL be downloaded from Azure, cached locally as JSON, and returned
