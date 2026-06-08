## Context

The current chunk-index implementation has three local memory pressure points:

- `ChunkIndexShardCache` materializes remote or local shard files into `Shard` objects backed by `Dictionary<ContentHash, ShardEntry>` and optionally keeps them in an L1 LRU.
- `ChunkIndexWriteSession` stores every newly uploaded archive entry in a `ConcurrentDictionary<ContentHash, ShardEntry>` until archive-tail flush succeeds.
- `ChunkIndexService.RepairAsync` scans committed chunks and groups all reconstructed entries in memory by shard prefix before writing rebuilt shards.

The previous split change made these responsibilities explicit, but deliberately preserved the memory-heavy shapes. At 10M chunks, even if the L1 cache is disabled or removed, the write session and repair grouping can still dominate memory.

The remote repository format should not return to a single uploaded/downloaded database file. A previous version stored the whole chunk index in one database blob, which made tiny changes rewrite and transfer a large database. This design keeps the current remote per-prefix shard blobs and uses SQLite only as a local cache and working store.

## Goals / Non-Goals

**Goals:**

- Bound managed memory usage for normal chunk-index lookup, archive entry recording, archive-tail flush, and full repair.
- Replace local in-memory shard dictionaries and per-prefix plaintext L2 shard files with a local SQLite store owned by chunk-index internals.
- Keep SQLite strictly local. It is a cache and working store, not the source of truth and not an uploaded chunk-index artifact.
- Keep current remote `chunk-index/<prefix>` blob names and serialization format unchanged.
- Keep `ChunkIndexService` as the public operational facade for existing callers.
- Keep the SQLite dependency isolated to `src/Arius.Core/Shared/ChunkIndex/` and use raw synchronous ADO.NET-style access, not EF Core.
- Preserve same-service visibility for entries recorded during the current archive session before they are flushed.
- Make full repair stream reconstructed entries into disk-backed local state instead of grouping all entries in memory.
- Add bounded chunk-index lookup flow for restore and list so those callers do not materialize all chunk-index lookup inputs or results at once when avoidable.
- Leave clear seams for a later dynamic chunk-prefix change.

**Non-Goals:**

- Do not introduce dynamic prefix splitting, route manifests, longest-prefix lookup, or stale-parent cleanup in this change.
- Do not upload or download a local SQLite database as the remote chunk index.
- Do not change remote shard serialization or encryption behavior.
- Do not use EF Core.
- Do not redesign restore into a fully streaming or disk-backed restore-plan pipeline in this change.
- Do not make restore/list callers depend on SQLite or chunk-index implementation details.
- Do not introduce distributed locking for concurrent archive/repair operations.

## Decisions

### Use SQLite as the local chunk-index store

Use `Microsoft.Data.Sqlite` directly through raw synchronous commands, readers, and transactions. The database lives under the existing repository local state area, for example:

```text
~/.arius/{account}-{container}/chunk-index/cache.sqlite
```

SQLite replaces both the current L1 shard-page cache and the plaintext per-prefix L2 shard files. There is no separate in-memory LRU shard cache, no plaintext per-prefix local shard file cache, and no `--dedup-cache-mb` managed cache budget after this change. SQLite stores local hydrated index rows and pending archive rows in B-tree indexes so point lookup does not require loading a whole shard into managed memory.

The local database is discardable. If it is missing, stale, or corrupt, chunk-index code can recreate it and rehydrate needed prefixes from remote `chunk-index/<prefix>` blobs. Explicit repair can rebuild remote shards from committed chunks when remote shard state itself is corrupt or incomplete.

SQLite does not support asynchronous I/O, and `Microsoft.Data.Sqlite` async methods execute synchronously. The local store API should therefore be synchronous by design. Outer chunk-index operations can remain async because remote blob operations are async, but they should call synchronous local-store methods for SQLite work.

Use conservative SQLite settings appropriate for a local cache:

- WAL mode for append/update behavior during archive and repair.
- A bounded SQLite page cache rather than managed object caches.
- Explicit transactions for shard ingestion, dirty-row recording, flush prefix updates, and repair staging.
- Synchronous `ExecuteReader`, `ExecuteNonQuery`, and transaction APIs rather than `ExecuteReaderAsync`, `ExecuteNonQueryAsync`, or other async SQLite overloads.
- Bulk insert/update loops that use one transaction, bind the command to that transaction, and reuse the same parameterized command and parameter objects, following `Microsoft.Data.Sqlite` bulk-insert guidance.

The recommended connection/concurrency model is:

- `ChunkIndexLocalStore` owns the connection string and SQLite schema, not a long-lived EF-style context.
- Build SQLite connection strings with `SqliteConnectionStringBuilder` so the local database file path cannot be interpreted as connection-string options.
- Store operations open short-lived `SqliteConnection` instances and rely on normal Microsoft.Data.Sqlite pooling unless profiling shows a reason to hold a dedicated connection.
- Do not use `Cache=Shared` with WAL; Microsoft guidance discourages mixing shared cache and write-ahead logging.
- Serialize SQLite writes through a local gate because SQLite still has a single-writer model.
- Allow read operations to use their own connection where safe. Streaming reads should own their connection and reader for the duration of enumeration and dispose both promptly.
- Use per-prefix gates around prefix hydration and flush/upload coordination so two operations do not concurrently download, ingest, stream, or upload the same prefix.

Keep three failure states separate:

- Dirty rows are not corruption. They are unflushed local archive state that should be flushed to remote shard blobs before snapshot publication.
- Local SQLite corruption is local cache corruption. Move aside or delete the local database and rehydrate needed clean rows from remote shard blobs. If this interrupts an active archive/flush, fail the operation and do not publish a snapshot.
- Remote chunk-index shard corruption is repository metadata corruption. Normal operations fail clearly and explicit full repair rebuilds remote shards from committed chunk blobs.
- Corrupt local SQLite that contains or may contain dirty rows is not silently recoverable. Non-archive operations should fail with a clear message that tells the user to rerun archive or delete the local `.arius` folder. Emergency restore remains recoverable because deleting the local `.arius` folder discards the local cache/working store and lets restore rehydrate clean rows from remote shard blobs.

Alternative considered: custom unsorted per-leaf files scanned linearly. That can be elegant when dynamic prefix splitting guarantees small leaf files, but without dynamic routing in this change fixed two-character prefixes can still contain tens of thousands of entries at 10M chunks. Making unsorted scans efficient would force dynamic prefix splitting into this change and expand scope into route-manifest correctness.

Alternative considered: custom sorted binary shard files with binary search and append journals. That avoids SQLite but recreates database concerns: indexing, transactions, corruption recovery, compaction, duplicate handling, and future route metadata. SQLite solves those concerns with less custom code.

Alternative considered: keep per-prefix plaintext files and remove L1 only. That reduces one memory pressure point but leaves write-session and repair memory unbounded, and lookup still needs either whole-shard materialization or repeated file scans.

### Keep remote per-prefix shard blobs as the repository index format

Remote chunk-index state remains the current per-prefix format:

```text
chunk-index/00
chunk-index/01
...
chunk-index/ff
```

Each remote shard remains serialized by `ShardSerializer` using the current wire format. The local SQLite store is not uploaded, not downloaded wholesale, and not treated as a repository artifact.

This preserves the reason for the current remote format: a small archive run should rewrite only touched shard blobs, not a monolithic database.

The source-of-truth model remains:

- Snapshots are the repository commit point.
- Chunk blobs and their metadata are the durable recovery source for full repair.
- Remote chunk-index shards are mutable repository metadata used for fast lookup.
- Local SQLite is cache and in-progress working state for the current process/machine.

If local SQLite is lost, lookup rehydrates from remote shards. If remote shards are corrupt or missing entries referenced by a snapshot, normal operations fail clearly and explicit repair rebuilds from committed chunks.

### One local-store state owner, with lookup/write/remote orchestration on the service

`ChunkIndexLocalStore` is the single owner of all disk-backed tables and transactions for:

- loaded shard rows
- loaded-prefix freshness state, including the snapshot identity and remote shard identity used when the prefix was last validated
- pending (unflushed) archive entries
- repair staging rows (staged as remote-backed rows during a repair run)

The implementation does not split reader, writer, and remote-store responsibilities into separate classes. The separate `ChunkIndexReader`/`ChunkIndexWriteSession`/`ChunkIndexRemoteStore` boundaries considered earlier collapsed into pass-throughs, so they were not created. The as-built shape is:

```text
ChunkIndexService            // lookup grouping, AddEntry/Flush semantics, prefix-load orchestration, remote shard download/upload
  ├─ ChunkIndexLocalStore    // SQLite tables and transactions (the only local-state owner)
  └─ ChunkIndexRouter        // content-hash -> leaf prefix routing seam
```

`ChunkIndexService` directly enforces flush-in-progress rules (`_acceptingEntries` guard), groups lookups by prefix, coordinates per-prefix hydration through `EnsurePrefixLoadedAndSynchronizedAsync`, serializes and uploads remote shards, and delegates every local-state read/mutation to `ChunkIndexLocalStore`. There is no in-memory pending-entry collection anywhere: `AddEntry` writes straight to the local store as a `pending_flush = 1` row.

Rationale: two disk-backed components that both know about unflushed archive entries would split transaction ownership and make partial-flush recovery harder. A single local store can make dirty-row writes, prefix selection, and dirty cleanup transactional.

### Local store API

`ChunkIndexLocalStore` owns SQLite details, but its internal methods are chunk-index-domain operations rather than SQL-shaped commands. The constructor creates or upgrades the schema. The API is:

```csharp
internal sealed class ChunkIndexLocalStore
{
    // Find
    public ShardEntry? FindEntry(ContentHash contentHash);            // any stored row (remote-backed or pending-flush)
    public ShardEntry? FindPendingFlushEntry(ContentHash contentHash); // pending-flush rows only, win before validation
    public bool IsPrefixAtSnapshotVersion(PathSegment prefix, string snapshotVersion);
    public bool IsPrefixAtETag(PathSegment prefix, string etag);
    public IReadOnlyList<PathSegment> GetPrefixesWithPendingFlushes();
    public IEnumerable<PathSegment> GetStoredPrefixes();
    public void ReadPrefixEntries(PathSegment prefix, Action<ShardEntry> consume);
    public bool HasPendingFlushEntries();

    // Pending-flush (archive) writes
    public void UpsertPendingFlush(ShardEntry entry);

    // Remote-backed cache writes
    public void UpsertRemoteBacked(ShardEntry entry);                  // used by repair
    public void UpdatePrefix(PathSegment prefix, string etag, string snapshotVersion, IEnumerable<ShardEntry> entries);
    public void AddEmptyPrefix(PathSegment prefix, string snapshotVersion);
    public void SetPrefixSnapshotVersion(PathSegment prefix, string etag, string snapshotVersion);
    public void PromoteToSnapshotVersion(string oldSnapshotVersion, string newSnapshotVersion);
    public void MarkPendingFlushesSynchronized(IEnumerable<(PathSegment Prefix, string Etag)> states, string snapshotVersion);
    public void ClearRemoteBackedCache();

    // Recovery
    public void RecreateDatabase(bool backupExisting);
}
```

The local store names follow the `pending_flush`/remote-backed vocabulary: `UpsertPendingFlush` records an unflushed archive row; `UpsertRemoteBacked`, `UpdatePrefix`, and `AddEmptyPrefix` write hydrated cache rows; `GetPrefixesWithPendingFlushes` and `MarkPendingFlushesSynchronized` drive flush; `ClearRemoteBackedCache` discards hydrated rows; `HasPendingFlushEntries` reports unflushed state. Clean-prefix ingestion is split into `UpdatePrefix` (existing remote shard) and `AddEmptyPrefix` (missing remote shard). Loaded-prefix freshness is queried with the boolean probes `IsPrefixAtSnapshotVersion` and `IsPrefixAtETag`.

`UpdatePrefix` is used when a remote shard has been downloaded and deserialized. It deletes existing remote-backed rows for the prefix, inserts the deserialized entries as remote-backed rows (`pending_flush = 0`) while preserving any local `pending_flush = 1` row for the same content hash, and records loaded-prefix state (prefix, remote existence, remote blob identity/ETag, snapshot version) in the same SQLite transaction. `AddEmptyPrefix` is the missing-shard equivalent. Pending-flush rows represent retryable archive operational state and win over remote cache hydration.

`UpdatePrefix` performs its inserts with one transaction and a reused parameterized command (`CreateUpsertCommand`) whose parameters are created once and assigned new values per row, with explicit SQLite types for hashes, sizes, prefix, and the pending-flush flag. `EnsurePrefixLoadedAndSynchronizedAsync` deserializes one remote shard at a time into a `Shard` and hands `shard.Entries` to `UpdatePrefix`, so peak memory is bounded by one materialized shard per touched prefix.

`ReadPrefixEntries` uses a callback rather than returning a deferred `IEnumerable<ShardEntry>` so the local store owns the SQLite connection and reader lifetime. The callback should consume entries synchronously. For this change, it is acceptable to materialize one prefix at a time into a `Shard` and serialize that one prefix to a `byte[]` for remote upload. That keeps peak memory bounded by one remote shard plus bounded worker concurrency while avoiding a complicated streaming serializer rewrite. The implementation should not keep multiple materialized shards beyond the bounded flush/repair worker count and should not cache materialized `Shard` objects in memory after the operation.

Avoid putting `GetOrAdd`-style remote loading on the local store. The “add” side requires remote shard download and wire deserialization, so that coordination belongs in the reader/writer orchestration path. A dedicated prefix-loader helper is optional, not required. Use the decision rule: keep prefix-loading coordination inline while only one or two call sites need it; extract a focused internal helper only if lookup and flush share enough code that extraction reduces complexity.

### Suggested local schema

Use binary hash storage to avoid string overhead and keep indexes compact.
Use one row table for both hydrated remote entries and unflushed archive entries. The `pending_flush` flag distinguishes discardable cache rows from current archive operational state:

- `pending_flush = 0`: hydrated remote-backed/cache row, safe to discard during cache invalidation.
- `pending_flush = 1`: unflushed archive row, protected state that must not be silently lost.

The schema (`ChunkIndexLocalStore.CreateOrUpgradeSchema`) is:

```sql
CREATE TABLE IF NOT EXISTS metadata (
    key   TEXT NOT NULL PRIMARY KEY,
    value TEXT NOT NULL,
    CHECK (length(key) > 0),
    CHECK (length(value) > 0)
);

CREATE TABLE IF NOT EXISTS chunk_index_entries (
    content_hash    BLOB NOT NULL PRIMARY KEY,
    chunk_hash      BLOB NOT NULL,
    original_size   INTEGER NOT NULL CHECK (original_size >= 0),
    compressed_size INTEGER NOT NULL CHECK (compressed_size >= 0),
    prefix          TEXT NOT NULL,
    pending_flush   INTEGER NOT NULL DEFAULT 0 CHECK (pending_flush IN (0, 1)),
    CHECK (length(content_hash) = 32),
    CHECK (length(chunk_hash) = 32),
    CHECK (length(prefix) > 0)
);

CREATE INDEX IF NOT EXISTS ix_chunk_index_entries_prefix
    ON chunk_index_entries(prefix, content_hash);

CREATE INDEX IF NOT EXISTS ix_chunk_index_entries_pending_flush_prefix
    ON chunk_index_entries(pending_flush, prefix);

CREATE TABLE IF NOT EXISTS loaded_prefixes (
    prefix               TEXT NOT NULL PRIMARY KEY,
    remote_exists        INTEGER NOT NULL CHECK (remote_exists IN (0, 1)),
    remote_blob_identity TEXT,
    snapshot_version     TEXT NOT NULL,
    CHECK (length(prefix) > 0),
    CHECK (length(snapshot_version) > 0)
);
```

`content_hash` primary-key upsert gives last-writer-wins duplicate semantics. The `loaded_prefixes` freshness column `snapshot_version` holds the latest snapshot blob name. Pragmas applied at schema creation are `journal_mode = wal` and `synchronous = normal`. Archive `AddEntry` upserts the row with `pending_flush = 1`, so same-run lookup naturally sees the new value from the same table.

`metadata` includes `schema_version = 1`.

Store route-related values in a way that can evolve later. For the fixed-prefix change, `prefix` is the current two-character prefix from `Shard.PrefixOf`. A later dynamic-prefix change can replace that with a leaf prefix from a route table without changing feature callers.

### Lookup freshness and validation

The loaded-prefix state is snapshot-scoped. The implementation should resolve the current latest snapshot identity once per archive, restore, or list operation and use it as the local cache epoch for clean chunk-index rows. The identity can be the latest snapshot blob name or another stable repository snapshot identity already available through snapshot resolution. SQLite remains hidden behind `ChunkIndexService`; the implementation can pass the identity into chunk-index operations or provide it through an internal shared repository-state dependency.

When the current latest snapshot identity matches `loaded_prefixes.snapshot_version`, clean rows for that prefix are trusted without any remote call. When it differs, the prefix is not automatically purged. Instead, the next operation that touches that prefix revalidates only that prefix by fetching the remote shard. For Azure Blob Storage the remote identity is the blob ETag. Core treats it as an opaque string exposed by the storage boundary, not as an Azure-specific type.

`EnsurePrefixLoadedAndSynchronizedAsync` performs this per prefix, serialized by a per-prefix gate:

```text
1. If loaded_prefixes.snapshot_version for the prefix equals the current latest snapshot version, trust local clean rows and return (no remote call).
2. Otherwise TryDownloadAsync chunk-index/<prefix>:
   a. missing shard -> AddEmptyPrefix: delete clean rows for the prefix, record loaded_prefixes(remote_exists = 0, snapshot_version).
   b. existing shard whose ETag equals loaded_prefixes.remote_blob_identity -> SetPrefixSnapshotVersion: keep clean rows, advance snapshot_version, discard the downloaded body.
   c. existing shard with a different ETag -> deserialize the body and UpdatePrefix: replace clean rows for the prefix, preserve pending-flush rows, record loaded_prefixes(remote_exists = 1, remote_blob_identity = ETag, snapshot_version).
```

This avoids a repository-wide cache purge when only one shard changed. It also avoids listing or checking every chunk-index shard on every operation. Remote shard fetches are proportional to prefixes actually touched by the operation and only needed when a prefix was loaded under a different snapshot version or was not loaded locally.

In the normal archive path, the deduplication lookup for a content hash should validate that prefix before `AddEntry` records a dirty row for the same content hash. Dirty-row preservation during prefix refresh is still required as defensive behavior, not as the expected happy path. `AddEntry` is a service operation and should not depend on every caller having just performed a validating lookup. A remote shard can also change after an earlier lookup but before archive-tail flush, and a failed previous flush can leave dirty rows locally for a later retry. In those cases, refreshing stale clean rows must merge the refreshed remote base with protected dirty rows rather than silently discarding current-run or retryable archive state.

Cross-machine explicit repair invalidation is out of scope for this change. A repair on another machine can rewrite chunk-index shards without publishing a new snapshot. Detecting that immediately requires a separate chunk-index epoch/manifest marker or equivalent remote coordination and is tracked as future work.

### Lookup flow

Single-hash lookup:

```text
1. Resolve the hash's current prefix.
2. Ensure the prefix is loaded and validated for the current latest snapshot identity using the lazy per-prefix validation flow.
3. Check chunk_index_entries by content_hash.
4. Return miss if the validated loaded prefix does not contain the hash.
```

Batched lookup groups hashes by prefix and ensures each prefix is loaded at most once. It does not materialize full shard dictionaries: it returns an `IReadOnlyDictionary` (the public API shape) but reads only the requested hashes from SQLite via `FindEntry`/`FindPendingFlushEntry`. Pending-flush rows are returned before any remote validation; remaining hashes are resolved after their prefix is ensured loaded.

### Archive entry recording

`AddEntry` upserts directly into `chunk_index_entries` with `pending_flush = 1`. This preserves same-session visibility without an in-memory dictionary or a separate pending table.

Dirty rows are retryable local operational state, not just current-process memory. That retry contract depends on a strict ordering invariant: chunk-index code may record a dirty row only after the referenced large chunk or thin chunk is durably uploaded. A dirty row that survives a failed archive-tail flush can therefore be retried by a later operation without requiring the failed archive run to have published a snapshot, because the row only points at already committed chunk storage. If local SQLite corruption interrupts dirty state before flush succeeds, the active operation must fail clearly rather than silently discarding rows whose referenced chunks may already exist.

Concurrent archive workers may call `AddEntry`. The local store serializes writes through a single write gate, safe with SQLite's single-writer model. Each write is small and disk-backed, so per-entry serialization is acceptable and there is no managed-memory queue.

`AddEntry` should still fail fast when a flush is already in progress, matching the existing archive-tail contract.

### Flush flow

Flush uses dirty rows as the source of touched prefixes:

```text
1. Set a simple in-process flush-in-progress guard so accidental overlapping `AddEntry` calls throw.
2. Query distinct touched prefixes from chunk_index_entries where pending_flush = 1 after the guard is set.
3. For each touched prefix with bounded parallelism:
   a. ensure the remote/base prefix is loaded and validated for the current latest snapshot identity, preserving dirty rows while refreshing stale clean rows
   b. stream all chunk_index_entries rows for that prefix ordered by content_hash into remote shard serialization
   c. upload chunk-index/<prefix>
4. Set pending_flush = 0 for flushed rows and update loaded-prefix remote identity only after all touched prefixes upload successfully.
```

Ordering the streamed output preserves deterministic shard serialization. This does not require keeping an in-memory `Shard` dictionary.

Because the current shard count and future dynamic split direction bound individual shard size, upload may materialize one prefix at a time into `Shard` and a serialized `byte[]`. This is accepted bounded memory. It must not reintroduce a repository-wide memory cache or keep materialized shards beyond the bounded workers currently flushing or repairing prefixes.

Partial failure behavior remains conservative: if any prefix upload fails, the archive fails and no snapshot is published. Dirty rows remain in SQLite for the current local state. A later rerun can retry the flush or explicit repair can rebuild from committed chunks.

After a shard upload succeeds, `MarkPendingFlushesSynchronized` updates loaded-prefix state with the new remote blob identity. `IBlobContainerService.UploadAsync` returns an `UploadResult` exposing the resulting `ETag`, so flush reads the identity directly from the upload result without an extra HEAD.

Shard upload uses `overwrite: true`. A SQLite transaction and a remote blob upload cannot be made atomically transactional, so the safety rule is: snapshot publication waits for flush success, and explicit repair can recover from partial remote shard updates.

### Full repair flow

Full repair should stop grouping all reconstructed entries in memory. Instead:

```text
1. Write the repair-in-progress marker outside the purgeable local chunk-index cache.
2. Move any existing cache.sqlite aside to a best-effort .bak file and create a fresh SQLite database in place.
3. Stream one metadata-aware chunks/ listing.
4. For each large or thin committed chunk, reconstruct one ShardEntry and upsert it into chunk_index_entries with pending_flush = 0.
5. Derive rebuilt prefixes from SQLite, not a managed all-entry grouping.
6. Stream each rebuilt prefix from SQLite to remote shard upload with bounded parallelism.
7. List existing remote chunk-index shards and delete stale shard blobs whose prefixes were not rebuilt.
8. Clear the repair marker only after successful upload and stale deletion.
```

For the current fixed two-character prefix layout, rebuilt prefix count is at most 256, so the prefix set is not the main memory risk. Still, putting repair state in SQLite keeps the design aligned with future dynamic prefixes where leaf count can be much larger.

Repair remains explicit and idempotent. It does not publish snapshots.

### Cache invalidation and local corruption

`InvalidateCaches` delegates to `ClearRemoteBackedCache`, which deletes the discardable remote-backed rows (`pending_flush = 0`), clears all `loaded_prefixes`, and deletes leftover legacy two-character plaintext shard files, while preserving `pending_flush = 1` rows. Routine snapshot changes do not call this — they rely on lazy per-prefix revalidation. The archive handler calls `InvalidateCaches` when filetree validation detects a snapshot mismatch, before flush; pending-flush rows recorded for the current archive survive. A preserved pending-flush row alone does not mean its prefix is fully loaded; flush still ensures the prefix is loaded from remote before uploading the complete shard. Invalidation does not touch the repair-in-progress marker.

Any local SQLite failure surfaces as a clear `ChunkIndexLocalStoreException` instructing the user to delete the local chunk-index cache directory and retry, or run explicit repair. The operation fails rather than continuing as if pending-flush rows were flushed or safely discarded. Deleting the local `.arius` folder is an acceptable emergency restore path because restore rehydrates remote-backed rows from remote shard blobs.

### Prepare for dynamic chunk prefixes without implementing them

Dynamic prefixes are intentionally deferred, but this change should avoid baking fixed-prefix assumptions into feature callers or the local-store schema.

Introduce or preserve an internal routing seam such as:

```text
ChunkIndexRouter.GetLeafPrefix(ContentHash hash)
```

For this change it returns the current fixed two-character prefix. Later it can perform longest-prefix lookup using route metadata.

Do not add route-manifest storage, split thresholds, split publication, or stale-parent cleanup now. Those behaviors need a separate design because their correctness depends on remote publication order:

```text
1. upload child shards
2. publish route manifest
3. delete stale parent shard later
```

The local SQLite schema can later cache route leaves or loaded leaf prefixes without changing `ChunkIndexService` callers.

### Snapshot-version promotion

After archive flush succeeds and a new snapshot is published, `ChunkIndexService.PromoteToSnapshotVersionAsync(newSnapshotVersion)` rewrites `loaded_prefixes.snapshot_version` from the snapshot version resolved at the start of the run to the newly published snapshot version (a no-op when they are equal). Prefixes validated during the run therefore remain trusted under the new epoch without re-probing remote on the next operation. The full archive sequence is `LookupAsync` (per content hash) → `AddEntry` (after upload) → `FlushAsync` → `PromoteToSnapshotVersionAsync`.

### Restore and list depend only on the facade

Restore and list resolve chunk-index entries solely through `ChunkIndexService.LookupAsync`; SQLite and the local store stay hidden behind the facade. `RestoreCommandHandler` looks up the distinct content hashes for a restore plan and groups files by chunk hash. `ListQueryHandler` looks up file sizes one directory at a time so it can keep streaming directory output. Both map unresolved hashes to a clear repair-guidance error (restore) or `OriginalSize = null` (list).

## Risks / Trade-offs

- **[Risk] SQLite native packaging/runtime issues** -> Use `Microsoft.Data.Sqlite` only inside chunk-index internals and verify test/CLI packaging on supported platforms.
- **[Risk] SQLite write serialization slows archive workers** -> Use transactions, reused parameterized commands, and bounded ingestion batches without unbounded memory queues.
- **[Risk] Local SQLite dirty rows blur cache vs operational state** -> Treat hydrated shard rows as discardable, and fail the active operation if local corruption interrupts dirty state before snapshot publication.
- **[Risk] Dynamic prefixes may later need schema changes** -> Keep routing internal and store prefix values generically as leaf prefixes, not as externally meaningful fixed two-character values.
- **[Risk] Remote shard uploads still rewrite whole touched prefixes** -> Accepted for this change. Dynamic prefixes are the follow-up intended to reduce remote rewrite amplification.

## Migration Plan

No remote migration is required. Existing remote chunk-index shard blobs remain valid.

Local migration can be cache-based:

1. New versions create `cache.sqlite` under the chunk-index local state directory.
2. Existing plaintext L2 shard files may be ignored or deleted as stale cache state.
3. Needed prefixes are rehydrated from remote `chunk-index/<prefix>` blobs on demand.
4. When moving or deleting the SQLite cache, include `cache.sqlite`, `cache.sqlite-wal`, and `cache.sqlite-shm` after all local-store connections are closed and `SqliteConnection.ClearAllPools()` has been called. Best-effort backups should use a `.bak` suffix for the database family.
5. If rollback occurs, the old implementation can ignore or delete `cache.sqlite` and rehydrate plaintext L2 files from remote shards as before.

Because local cache state is not the source of truth, no durable data migration is required.
