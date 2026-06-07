## 1. Baseline And TDD Guardrails

- [x] 1.1 Run the current focused chunk-index tests to establish the pre-change behavior baseline: `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/Shared.ChunkIndex/*"` or the nearest valid TUnit tree filter after listing tests.
- [x] 1.2 Measure current line coverage for `src/Arius.Core/Shared/ChunkIndex/` and record the starting percentage and uncovered areas before implementation.
- [x] 1.3 Keep every behavior slice test driven: add or update a focused failing test first, confirm it fails for the intended reason, implement the smallest code change, then rerun the focused test before moving to the next slice.
- [x] 1.4 Identify existing chunk-index lookup, flush, invalidation, repair, restore, and list tests that must continue to pass while the local cache implementation changes.

## 2. Storage Blob Identity Contract

- [x] 2.1 Add failing storage-boundary tests proving `BlobMetadata` exposes an opaque remote blob identity for HEAD, metadata listing, and successful upload results.
- [x] 2.2 Change `IBlobContainerService.UploadAsync` to return `BlobMetadata`, update all Core, Azure, Explorer, fake, and test call sites, and preserve existing upload semantics.
- [x] 2.3 Implement blob identity propagation in Azure storage from ETags for `UploadAsync`, `GetMetadataAsync`, and `ListAsync(includeMetadata: true)`.
- [x] 2.4 Update fake and test blob services so tests can model missing, unchanged, and changed remote shard identities without depending on Azure-specific ETag syntax.
- [x] 2.5 Add or update tests proving Core compares blob identities opaquely and `OpenWriteAsync` callers fetch metadata after a successful stream upload when they need the resulting identity.

## 3. SQLite Local Store Foundation

- [x] 3.1 Add failing local-store tests for initialization, schema version `1`, WAL mode, binary hash storage, loaded-prefix rows, dirty rows, and clean rows.
- [x] 3.2 Add `Microsoft.Data.Sqlite` to the appropriate project using the .NET CLI and keep the dependency isolated to chunk-index internals; do not introduce EF Core.
- [x] 3.3 Implement internal `ChunkIndexLocalStore` under `src/Arius.Core/Shared/ChunkIndex/` with SQLite schema ownership, `SqliteConnectionStringBuilder`, short-lived connections, and serialized writes.
- [x] 3.4 Add failing tests for `UpsertPendingFlush`, `FindEntry`, `FindPendingFlushEntry`, `GetPrefixesWithPendingFlushes`, `HasPendingFlushEntries`, and duplicate content-hash last-writer behavior.
- [x] 3.5 Implement dirty-row persistence with bounded transaction batches, reused parameterized commands, explicit transactions, and no independent in-memory pending-entry collection.
- [x] 3.6 Add failing tests for `UpdatePrefix`/`AddEmptyPrefix` proving remote-backed rows are stored with `pending_flush = 0`, loaded-prefix state is written in the same transaction, and existing pending-flush rows are not overwritten.
- [x] 3.7 Implement clean-prefix ingestion (`UpdatePrefix`/`AddEmptyPrefix`) and loaded-prefix state APIs with pending-flush-row preservation.
- [x] 3.8 Add failing tests for `ReadPrefixEntries` proving rows are returned ordered by content hash and SQLite reader lifetime does not escape the local store.
- [x] 3.9 Implement prefix row streaming through a local-store-owned synchronous callback or equivalent owned iteration pattern.
- [x] 3.10 Add static or architecture coverage proving chunk-index SQLite code uses synchronous Microsoft.Data.Sqlite command and reader APIs, avoids async SQLite overloads, and does not use `Cache=Shared` with WAL.

## 4. Replace L1/L2 Shard Cache With Disk-backed Lookup

- [x] 4.1 Add failing `ChunkIndexService` and reader tests proving lookup queries SQLite-backed local state after ensuring the prefix is loaded and does not use an L1 materialized-shard cache or plaintext per-prefix L2 files.
- [x] 4.2 Replace `ChunkIndexShardCache` read-through behavior with SQLite-backed prefix validation and row lookup while preserving the existing remote `chunk-index/<prefix>` shard format.
- [x] 4.3 Remove normal-use plaintext per-prefix L2 shard file reads and writes from chunk-index lookup, flush, and invalidation paths; ignore or delete old local L2 files as stale cache state.
- [x] 4.4 Remove the managed shard-page cache budget behavior, including any `--dedup-cache-mb` operational effect, without changing unrelated CLI behavior.
- [x] 4.5 Add failing tests proving single-hash and batched lookup load each touched prefix at most once and read only requested hashes from SQLite after hydration.
- [x] 4.6 Implement batched lookup grouping by current two-character shard prefix through an internal router seam that can later be replaced by dynamic prefix routing.

## 5. Lazy Prefix Freshness Validation

- [x] 5.1 Add failing tests proving a loaded prefix validated against the current latest snapshot identity is trusted without a remote metadata call.
- [x] 5.2 Add failing tests proving a loaded prefix with an older snapshot identity revalidates only that touched prefix by comparing remote existence and opaque blob identity.
- [x] 5.3 Implement lazy per-prefix validation using loaded-prefix state, latest snapshot identity, remote shard metadata, and remote shard download or empty-prefix ingestion.
- [x] 5.4 Add failing tests proving unchanged remote identity advances the loaded-prefix snapshot identity without deleting clean rows.
- [x] 5.5 Add failing tests proving changed remote identity deletes clean rows only for that prefix, preserves dirty rows, and ingests the current remote shard or an empty missing shard.
- [x] 5.6 Add failing tests proving routine snapshot changes do not list or HEAD every chunk-index shard and do not force a repository-wide clean-row purge.
- [x] 5.7 Add prefix-scoped gates so concurrent hydration, ingestion, streaming, and upload for the same prefix are serialized while different prefixes can proceed with bounded parallelism.

## 6. Archive Dirty Rows And Flush

- [x] 6.1 Add failing archive or chunk-index tests proving dirty rows are recorded only after the referenced large chunk or thin chunk has been durably uploaded.
- [x] 6.2 Update archive entry recording so `AddEntry` writes dirty rows directly to `ChunkIndexLocalStore`, preserves same-session lookup visibility, and fails fast when flush is in progress.
- [x] 6.3 Remove the write-session owned pending-entry collection or reduce the writer to a thin policy layer that delegates dirty-row persistence, dirty-prefix queries, lookup visibility, and cleanup to the local store.
- [x] 6.4 Add failing flush tests proving dirty prefixes come from SQLite, each dirty prefix is fully loaded and validated before upload, and uploaded shards include existing clean rows plus dirty rows.
- [x] 6.5 Implement flush streaming from SQLite ordered by content hash, one prefix per bounded worker, preserving the existing remote shard serializer and remote blob names.
- [x] 6.6 Add failing tests proving dirty rows are marked clean only after all touched prefixes upload successfully, and partial prefix upload failure fails the archive without publishing a snapshot.
- [x] 6.7 Update flush completion to record the uploaded shard's resulting ETag in loaded-prefix state from the `UploadResult` returned by the shard upload.
- [x] 6.8 Add failing tests proving stale clean rows can be refreshed during flush while current-run or retryable dirty rows survive and are included in the uploaded shard.

## 7. Local Corruption, Cache Invalidation, And Repair Marker Safety

- [x] 7.1 Add failing tests proving `ClearRemoteBackedCache` and snapshot-mismatch invalidation delete remote-backed rows and loaded-prefix state while preserving pending-flush rows or failing clearly.
- [x] 7.2 Implement cache invalidation against SQLite clean cache state and remove obsolete L1-specific invalidation behavior.
- [x] 7.3 Add failing tests proving a clean corrupt SQLite cache can be moved aside or recreated, including `cache.sqlite`, `cache.sqlite-wal`, and `cache.sqlite-shm` handling after connections are closed and `SqliteConnection.ClearAllPools()` is called.
- [x] 7.4 Implement local SQLite cache recreation via `RecreateDatabase(backupExisting)` — `ClearAllPools()`, move the `cache.sqlite`/`-wal`/`-shm` family to `.bak`, create a fresh database — and invoke it at the start of explicit repair so rebuilt prefixes rehydrate on demand.
- [x] 7.5 Add failing tests proving a local SQLite failure, including during an active archive or flush, raises a clear `ChunkIndexLocalStoreException` with delete-local-state/repair guidance and does not publish a snapshot.
- [x] 7.6 Add failing tests proving normal lookup, entry recording, and flush fail clearly while the repair-in-progress marker exists, while explicit repair can rerun with the marker present.

## 8. Disk-backed Full Repair

- [x] 8.1 Add failing full-repair tests proving repair writes the repair-in-progress marker, replaces or moves aside the existing local SQLite cache, and stages reconstructed entries in SQLite instead of grouping all entries by prefix in managed memory.
- [x] 8.2 Implement repair startup to create a fresh local SQLite repair/cache store after protecting or replacing the previous cache file family.
- [x] 8.3 Add failing tests proving full repair performs one metadata-aware listing of `chunks/` and reconstructs large and thin chunk-index entries into disk-backed state as they are discovered.
- [x] 8.4 Implement chunk listing to SQLite staging for repair without per-prefix chunk listings or repository-wide in-memory grouping.
- [x] 8.5 Add failing tests proving repair streams each rebuilt prefix from SQLite ordered by content hash, uploads deterministic non-empty remote shards, and deletes stale remote chunk-index shards not present in rebuilt state.
- [x] 8.6 Implement bounded repair prefix upload and stale-shard deletion, clearing the repair marker only after upload and deletion succeed.
- [x] 8.7 Add failing tests proving interrupted repair can be rerun, purges partial local repair state, reconstructs from committed chunks again, and does not publish snapshots.

## 9. Restore And List Lookup Through The Facade

- [x] 9.1 Add restore tests proving chunk resolution goes through `ChunkIndexService`, keeps large-file and tar-bundled grouping, and fails unresolved snapshot-referenced hashes with explicit repair guidance.
- [x] 9.2 Update restore chunk resolution to resolve a plan's content hashes through `ChunkIndexService` and fail unresolved or corrupt-index lookups with explicit repair guidance.
- [x] 9.3 Add list-query tests proving file-size lookup resolves through `ChunkIndexService` one directory at a time, preserves progressive output, maps missing chunk-index entries to `OriginalSize = null`, and fails corrupt index states with repair guidance.
- [x] 9.4 Update list size lookup to use only `ChunkIndexService` APIs and avoid direct references to SQLite or chunk-index local-store internals.

## 10. Boundary, Documentation, And Cleanup

- [x] 10.1 Add or update architecture tests proving SQLite, `Microsoft.Data.Sqlite`, and `ChunkIndexLocalStore` are referenced only inside `src/Arius.Core/Shared/ChunkIndex/` and its tests.
- [x] 10.2 Add or update architecture tests proving feature handlers, restore, list, DI registrations, and other shared services consume `ChunkIndexService` instead of internal chunk-index store, reader, writer, or repair components.
- [x] 10.3 Remove obsolete in-memory shard cache, plaintext L2 cache, `Shard.Merge`, and write-session pending collection code after replacement tests are green.
- [x] 10.4 Update `docs/cache.md` if its chunk-index cache description no longer matches the SQLite-backed local cache, lazy prefix validation, dirty-row protection, and disk-backed repair flow.
- [x] 10.5 Update `README.md` or `AGENTS.md` only if user-facing behavior or AI-agent guidance materially changes.

## 11. Acceptance Verification

- [x] 11.1 Run the full focused unit suite for changed Core behavior: `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj`.
- [x] 11.2 Run storage backend tests affected by blob identity and upload return metadata: `dotnet test --project src/Arius.AzureBlob.Tests/Arius.AzureBlob.Tests.csproj` and relevant `src/Arius.Integration.Tests` storage tests when Docker or backend prerequisites are available.
- [x] 11.3 Run architecture boundary tests: `dotnet test --project src/Arius.Architecture.Tests/Arius.Architecture.Tests.csproj`.
- [x] 11.4 Run relevant restore, list, archive, and repair integration tests that exercise chunk-index lookup, flush, corruption, and repair behavior.
- [x] 11.5 Measure final line coverage for `src/Arius.Core/Shared/ChunkIndex/` and do not accept the change until it is at least 90%.
- [x] 11.6 Confirm coverage includes focused tests for `ChunkIndexLocalStore`, prefix validation, dirty-row recording, flush, invalidation, corruption handling, repair, and the `ChunkIndexService` facade.
- [x] 11.7 Run `openspec validate disk-backed-chunk-index-cache --strict` and confirm the change is apply-ready.
