# Chunk Storage Service Spec

## Purpose

Defines `ChunkStorageService`: a shared service that owns chunk blob upload, download, hydration status resolution, rehydration initiation, and rehydrated cleanup planning, while keeping chunk metadata lookup and shard management in `ChunkIndexService`.

## Requirements

### Requirement: Separate chunk index from chunk storage responsibilities
The system SHALL keep chunk metadata lookup and chunk blob protocol as separate shared services. `ChunkIndexService` SHALL own content-hash lookup, pending `ShardEntry` recording, shard flush, and in-memory shard-cache invalidation. `ChunkStorageService` SHALL own chunk blob upload, chunk blob download, hydration status resolution, rehydration initiation, and rehydrated cleanup planning.

#### Scenario: Archive handler uses both chunk services
- **WHEN** `ArchiveCommandHandler` archives a file
- **THEN** it SHALL use `ChunkIndexService` for dedup lookup and shard recording
- **AND** it SHALL use `ChunkStorageService` for large, tar, and thin chunk blob operations

#### Scenario: Restore handler uses both chunk services
- **WHEN** `RestoreCommandHandler` restores files from a snapshot
- **THEN** it SHALL use `ChunkIndexService` to resolve content hashes to chunk metadata
- **AND** it SHALL use `ChunkStorageService` to download chunks, resolve hydration state, start rehydration, and plan rehydrated cleanup

### Requirement: Chunk storage upload API
`ChunkStorageService` SHALL expose separate asynchronous methods for large chunks, tar chunks, and thin chunks:

- `UploadLargeAsync(string chunkHash, Stream content, long sourceSize, BlobTier tier, IProgress<long>? progress, CancellationToken)`
- `UploadTarAsync(string chunkHash, Stream content, long sourceSize, BlobTier tier, IProgress<long>? progress, CancellationToken)`
- `UploadThinAsync(string contentHash, string parentChunkHash, long originalSize, long compressedSize, CancellationToken)`

`UploadLargeAsync` and `UploadTarAsync` SHALL return a `ChunkUploadResult` containing the chunk hash, stored size, and whether the blob already existed. `UploadThinAsync` SHALL return `true` when it creates the thin chunk blob and `false` when a fully committed thin chunk already exists.

#### Scenario: Large chunk upload returns stored size
- **WHEN** a large chunk is uploaded through `UploadLargeAsync`
- **THEN** the method SHALL return the chunk hash and the stored chunk size after gzip and optional encryption are applied

#### Scenario: Tar chunk upload uses explicit tar method
- **WHEN** a sealed tar bundle is uploaded
- **THEN** the feature SHALL call `UploadTarAsync` rather than a generic kind-switching upload API

#### Scenario: Thin chunk upload uses parent chunk hash
- **WHEN** a tar-bundled file needs its thin chunk blob created
- **THEN** the feature SHALL call `UploadThinAsync` with the file content hash and the tar chunk's `parentChunkHash`

### Requirement: Chunk storage owns storage transforms and metadata protocol
`ChunkStorageService` SHALL own the chunk storage encoding and decoding protocol. `UploadLargeAsync` and `UploadTarAsync` SHALL accept plaintext source streams and SHALL internally apply optional progress reporting, gzip compression, encryption, stored-size counting, blob upload, metadata writes, tier assignment, and create-if-not-exists crash-recovery rules. `DownloadAsync(string chunkHash, IProgress<long>? progress, CancellationToken)` SHALL return a plaintext readable stream and SHALL internally choose the best readable blob source, apply optional progress reporting, decrypt, and gunzip before returning the stream.

Feature handlers SHALL NOT construct chunk blob names, select chunk content types, write chunk metadata keys, or build the gzip/encryption stream chain themselves.

#### Scenario: Archive handler does not build upload protocol
- **WHEN** `ArchiveCommandHandler` uploads a large or tar chunk
- **THEN** it SHALL pass a plaintext source stream and progress sink to `ChunkStorageService`
- **AND** it SHALL NOT directly call `BlobPaths.Chunk(...)`, choose chunk content types, or write chunk metadata

#### Scenario: Restore handler receives plaintext download stream
- **WHEN** `RestoreCommandHandler` downloads a chunk through `DownloadAsync`
- **THEN** the returned stream SHALL already be decrypted and gunzipped plaintext suitable for direct large-file restore or tar extraction

#### Scenario: Chunk storage handles already-exists recovery
- **WHEN** a chunk upload encounters a previously existing blob at the target name
- **THEN** `ChunkStorageService` SHALL interpret chunk metadata completeness and perform the existing recover-or-delete-and-retry behavior internally

### Requirement: Shared chunk hydration status API
The system SHALL define `ChunkHydrationStatus` once in `Shared/ChunkIndex/ChunkHydrationStatus.cs` and SHALL reuse that type across chunk storage and features. `ChunkStorageService` SHALL expose `GetHydrationStatusAsync(string chunkHash, CancellationToken)` and SHALL encapsulate the rules for `Unknown`, `Available`, `NeedsRehydration`, and `RehydrationPending` by checking primary and rehydrated chunk blobs.

Feature handlers and queries SHALL NOT implement their own chunk hydration-state resolution rules.

#### Scenario: Hydration status query reuses shared type
- **WHEN** `ChunkHydrationStatusQueryHandler` needs a file's chunk hydration state
- **THEN** it SHALL resolve the content hash through `ChunkIndexService`
- **AND** it SHALL call `ChunkStorageService.GetHydrationStatusAsync` and return the shared `ChunkHydrationStatus` value

#### Scenario: Restore handler does not inspect blob tiers directly
- **WHEN** `RestoreCommandHandler` classifies chunks as available, pending, or needing rehydration
- **THEN** it SHALL use `GetHydrationStatusAsync`
- **AND** it SHALL NOT implement its own `chunks/` versus `chunks-rehydrated/` tier interpretation logic

### Requirement: Chunk storage rehydration lifecycle API
`ChunkStorageService` SHALL expose `StartRehydrationAsync(string chunkHash, RehydratePriority priority, CancellationToken)` and SHALL encapsulate the copy-to-rehydrate blob protocol from `chunks/<hash>` to `chunks-rehydrated/<hash>`. The service SHALL also expose `PlanRehydratedCleanupAsync(CancellationToken)` returning an `IRehydratedChunkCleanupPlan` with `ChunkCount`, `TotalBytes`, and `ExecuteAsync(CancellationToken)`.

The cleanup plan SHALL allow restore to preview cleanup totals for user confirmation and then execute cleanup without enumerating `chunks-rehydrated/` twice.

#### Scenario: Restore confirms cleanup from plan totals
- **WHEN** restore completes with no pending rehydration and rehydrated blobs exist
- **THEN** `RestoreCommandHandler` SHALL obtain `ChunkCount` and `TotalBytes` from the cleanup plan for confirmation before deletion

#### Scenario: Cleanup plan executes deletion once confirmed
- **WHEN** the user confirms cleanup of rehydrated blobs
- **THEN** `ExecuteAsync` SHALL delete the planned rehydrated blobs and return the actual deleted count and freed bytes

### Requirement: Repository-local path helpers live outside chunk index
The system SHALL provide a repository-scoped path helper for local cache and log directories instead of exposing repository directory naming helpers from `ChunkIndexService`. Shared services, CLI code, and tests SHALL use the repository-scoped helper for repo root, chunk-index cache, filetree cache, snapshot cache, and logs directories.

#### Scenario: Snapshot and filetree services share repository path helper
- **WHEN** `SnapshotService` and `FileTreeService` derive their local cache directories
- **THEN** they SHALL do so through the shared repository path helper rather than through static methods on `ChunkIndexService`

#### Scenario: CLI logs path does not depend on chunk index service
- **WHEN** CLI code derives the repository logs directory
- **THEN** it SHALL use the shared repository path helper and SHALL NOT depend on `ChunkIndexService` for the repository directory name
