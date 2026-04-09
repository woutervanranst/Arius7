## Context

`ChunkIndexService` currently owns shard lookup, in-memory/disk cache behavior, pending entry recording, and end-of-run shard flush. The actual chunk blob protocol lives elsewhere: archive uploads large, tar, and thin chunks directly through `IBlobContainerService`; restore chooses between `chunks/` and `chunks-rehydrated/`, interprets hydration state, starts rehydration copies, and enumerates/deletes rehydrated blobs; `ChunkHydrationStatusQueryHandler` resolves hydration state through its own helper.

That split leaves chunk-specific mechanics duplicated across multiple features. It also makes the service boundary inconsistent with `FileTreeService` and `SnapshotService`, which already act as the shared entry points for their blob concepts.

## Goals / Non-Goals

**Goals:**
- Create a shared service boundary for chunk blob protocol details without moving archive/restore orchestration out of the feature handlers.
- Keep `ChunkIndexService` focused on chunk metadata lookup and shard persistence.
- Centralize chunk blob naming, metadata keys, content types, gzip/encryption transforms, crash-recovery upload rules, hydration state rules, and rehydrated cleanup mechanics.
- Preserve feature ownership of when to upload, how to group files into tar bundles, when to request rehydration, and how restored bytes are materialized into files.
- Move repository-local cache/log path helpers into a repository-scoped helper instead of keeping them on `ChunkIndexService`.

**Non-Goals:**
- Renaming `Shared/ChunkIndex` to `Shared/Chunks` in this change.
- Changing archive or restore user-visible behavior.
- Merging chunk index and chunk storage back into one oversized shared service.
- Moving container creation out of `IBlobContainerService`.

## Decisions

### Split chunk responsibilities into two shared services

We will keep two chunk-focused shared services:

- `ChunkIndexService`
  - `LookupAsync(IEnumerable<string>)`
  - `LookupAsync(string)` convenience overload
  - `Record(ShardEntry)`
  - `FlushAsync()`
  - `InvalidateMemoryCache()`

- `ChunkStorageService`
  - `UploadLargeAsync(...)`
  - `UploadTarAsync(...)`
  - `UploadThinAsync(...)`
  - `DownloadAsync(...)`
  - `GetHydrationStatusAsync(...)`
  - `StartRehydrationAsync(...)`
  - `PlanRehydratedCleanupAsync(...)`

This keeps the mutable shard-cache problem separate from the chunk blob protocol problem while still giving features a clean shared entry point for chunk storage mechanics.

Alternative considered: broadening `ChunkIndexService` into `ChunkService`.
Rejected because it would mix two distinct concerns with different lifecycles: mutable shard caching vs. blob upload/download/rehydration protocol.

### Keep orchestration in features

`ArchiveCommandHandler` and `RestoreCommandHandler` will continue to decide:

- when dedup lookup happens
- when a file becomes a large chunk vs. a tar entry
- when a tar is sealed
- when rehydration is requested
- when cleanup is offered to the user
- how downloaded plaintext bytes are written or extracted

The new shared service only owns chunk blob protocol details.

Alternative considered: moving archive/restore flows into a single shared chunk service.
Rejected because it would collapse vertical-slice orchestration into Shared and violate the existing architecture guidance.

### Keep `UploadLargeAsync` and `UploadTarAsync` as separate public methods

The public API will keep separate large and tar upload methods even though their internals will share one private upload helper. Call sites stay more explicit about intent, while implementation still avoids duplication.

Alternative considered: one public `UploadAsync(kind: ...)` method.
Rejected because it makes feature call sites less self-explanatory for little practical gain.

### `ChunkStorageService` owns gzip and encryption transforms

`UploadLargeAsync` and `UploadTarAsync` will accept plaintext source streams plus `IProgress<long>?` and will internally perform:

`ProgressStream -> GZipStream -> EncryptingStream -> CountingStream -> blob write`

`DownloadAsync` will return a plaintext stream and will internally handle:

`blob read -> optional ProgressStream -> decrypt -> gunzip`

This keeps chunk encoding/decoding as part of the chunk storage protocol instead of leaving that detail in feature handlers.

Alternative considered: passing already-transformed streams into the service.
Rejected because it would leave chunk storage format knowledge in the features and weaken the boundary we are trying to establish.

### Reuse `ChunkHydrationStatus` as shared vocabulary

The existing `ChunkHydrationStatus` enum will move to `Shared/ChunkIndex/ChunkHydrationStatus.cs`. `ChunkStorageService` and feature/query handlers will all reuse this shared type. The folder placement is a short-term compromise; a later folder rename to `Shared/Chunks` is explicitly out of scope for this change.

### Use a cleanup-plan object instead of a confirmation callback

`ChunkStorageService` will expose `PlanRehydratedCleanupAsync()` returning an `IRehydratedChunkCleanupPlan` with:

- `ChunkCount`
- `TotalBytes`
- `ExecuteAsync()`

This lets the restore feature preview cleanup counts for user confirmation and then execute deletion without listing `chunks-rehydrated/` twice.

Alternative considered: injecting a confirmation delegate into `ChunkStorageService`.
Rejected because it mixes storage mechanics with application workflow and user-interaction policy.

### Introduce `RepositoryPaths` for local repository directories

Repository-local path helpers such as repo directory name, chunk-index cache dir, filetree cache dir, snapshot cache dir, and logs dir will move into a shared `RepositoryPaths` helper. These helpers are not chunk-index responsibilities and are already used by multiple services, CLI code, and tests.

## Risks / Trade-offs

- **Larger `ChunkStorageService` surface** -> Mitigation: keep it strictly scoped to chunk blob protocol and do not move feature orchestration into it.
- **Progress wiring becomes more service-centric** -> Mitigation: accept `IProgress<long>?` in upload/download methods so features still own user-facing progress factories.
- **Cleanup plan introduces a small lifecycle abstraction** -> Mitigation: keep it narrow (`ChunkCount`, `TotalBytes`, `ExecuteAsync`, `DisposeAsync`) and use it only for rehydrated cleanup.
- **Temporary folder placement for `ChunkHydrationStatus` is imperfect** -> Mitigation: document the later `Shared/Chunks` rename as out of scope rather than blocking this change.
- **Handler tests may need broad updates** -> Mitigation: refactor tests toward service-boundary assertions and keep lower-level blob protocol tests near `ChunkStorageService`.

## Migration Plan

1. Add `RepositoryPaths` and move existing path helper usage to it.
2. Introduce shared `ChunkHydrationStatus` and `ChunkStorageService` abstractions.
3. Move large/tar/thin upload, download, hydration-state, rehydration, and cleanup-plan mechanics into `ChunkStorageService`.
4. Refactor archive, restore, and hydration-status query handlers to use `ChunkStorageService`.
5. Keep `IBlobContainerService.CreateContainerIfNotExistsAsync` in place for repository/container bootstrap.
6. Update tests around the new service boundary.

Rollback is straightforward because this change is internal architecture only: revert handler wiring back to raw blob operations and remove the new service if implementation proves awkward.

## Open Questions

- None for the current change boundary.
