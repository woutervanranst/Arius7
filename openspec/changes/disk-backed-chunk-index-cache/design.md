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

SQLite replaces both the current L1 shard-page cache and the plaintext per-prefix L2 shard files. It stores local hydrated index rows and pending archive rows in B-tree indexes so point lookup does not require loading a whole shard into managed memory.

The local database is discardable. If it is missing, stale, or corrupt, chunk-index code can recreate it and rehydrate needed prefixes from remote `chunk-index/<prefix>` blobs. Explicit repair can rebuild remote shards from committed chunks when remote shard state itself is corrupt or incomplete.

SQLite does not support asynchronous I/O, and `Microsoft.Data.Sqlite` async methods execute synchronously. The local store API should therefore be synchronous by design. Outer chunk-index operations can remain async because remote blob operations are async, but they should call synchronous local-store methods for SQLite work.

Use conservative SQLite settings appropriate for a local cache:

- WAL mode for append/update behavior during archive and repair.
- A bounded SQLite page cache rather than managed object caches.
- Explicit transactions for shard ingestion, dirty-row recording, flush prefix updates, and repair staging.
- Synchronous `ExecuteReader`, `ExecuteNonQuery`, and transaction APIs rather than `ExecuteReaderAsync`, `ExecuteNonQueryAsync`, or other async SQLite overloads.
- Bulk insert/update loops that use one transaction and reuse the same parameterized command, following `Microsoft.Data.Sqlite` bulk-insert guidance.

The recommended connection/concurrency model is:

- `ChunkIndexLocalStore` owns the connection string and SQLite schema, not a long-lived EF-style context.
- Store operations open short-lived `SqliteConnection` instances and rely on normal Microsoft.Data.Sqlite pooling unless profiling shows a reason to hold a dedicated connection.
- Do not use `Cache=Shared` with WAL; Microsoft guidance discourages mixing shared cache and write-ahead logging.
- Serialize SQLite writes through a local gate because SQLite still has a single-writer model.
- Allow read operations to use their own connection where safe. Streaming reads should own their connection and reader for the duration of enumeration and dispose both promptly.
- Use per-prefix gates around prefix hydration and flush/upload coordination so two operations do not concurrently download, ingest, stream, or upload the same prefix.

Keep three failure states separate:

- Dirty rows are not corruption. They are unflushed local archive state that should be flushed to remote shard blobs before snapshot publication.
- Local SQLite corruption is local cache corruption. Move aside or delete the local database and rehydrate needed clean rows from remote shard blobs. If this interrupts an active archive/flush, fail the operation and do not publish a snapshot.
- Remote chunk-index shard corruption is repository metadata corruption. Normal operations fail clearly and explicit full repair rebuilds remote shards from committed chunk blobs.

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

### Merge disk storage into one local-store component, keep write-session behavior separate only as policy

Do not keep `ChunkIndexLocalStore` and `ChunkIndexWriteSession` as two independent state owners. The local SQLite store should own all disk-backed tables and transactions for:

- loaded shard rows
- loaded-prefix freshness state, including the snapshot identity and remote shard identity used when the prefix was last validated
- pending archive entries
- repair staging rows or repair run state

However, keep archive-session semantics visible as a small policy layer if it remains useful. The important distinction is ownership:

```text
ChunkIndexService
  ├─ ChunkIndexReader        // groups lookups, asks local store/remote store
  ├─ ChunkIndexWriteSession  // optional thin policy: AddEntry/Flush semantics, no in-memory dictionary
  ├─ ChunkIndexLocalStore    // SQLite tables and transactions
  └─ ChunkIndexRemoteStore   // remote shard download/upload serialization
```

`ChunkIndexWriteSession` should not own a collection. It can remain as a behavior boundary that enforces flush-in-progress rules and calls `ChunkIndexLocalStore` for dirty-row persistence. If that layer becomes a pass-through after implementation, remove it and keep the behavior on `ChunkIndexService` or a focused writer component. The design preference is: one owner for local state, separate names only where they clarify behavior.

Rationale: two disk-backed components that both know about unflushed archive entries would split transaction ownership and make partial-flush recovery harder. A single local store can make dirty-row writes, prefix selection, and dirty cleanup transactional.

### Local store API

`ChunkIndexLocalStore` owns SQLite details, but its public internal methods should be chunk-index-domain operations rather than SQL-shaped commands. A representative API is:

```csharp
internal sealed class ChunkIndexLocalStore : IDisposable
{
    public void Initialize();

    public ShardEntry? GetValueOrDefault(ContentHash contentHash);

    public void UpsertDirty(ShardEntry entry);

    public void UpsertDirtyRange(IEnumerable<ShardEntry> entries);

    public void IngestCleanPrefix(
        PathSegment prefix,
        IEnumerable<ShardEntry> entries);

    public LoadedPrefixState? GetLoadedPrefixState(PathSegment prefix);

    public IEnumerable<PathSegment> GetDirtyPrefixes();

    public void ReadPrefixEntries(
        PathSegment prefix,
        Action<ShardEntry> consume);

    public void MarkDirtyPrefixesClean(IReadOnlyCollection<PathSegment> prefixes);

    public void ClearCleanCache();

    public bool HasDirtyRows();
}
```

`IngestCleanPrefix` is used when a remote shard has been downloaded and deserialized. It inserts or updates clean cache rows with `dirty = 0`, but it must not overwrite a local dirty row for the same content hash. It also marks the prefix loaded in the same SQLite transaction as the clean-row ingestion, including the current latest snapshot identity and the remote shard identity used for validation. Dirty rows represent current archive operational state and win over remote cache hydration.

`IngestCleanPrefix` and `UpsertDirtyRange` should perform bulk ingestion with one transaction and a reused parameterized command. A bounded channel SHALL be used when higher-level async workflows need to bridge remote shard download/deserialization into synchronous SQLite writes. The channel must have a fixed capacity and must be drained before the prefix is marked loaded or dirty rows are considered flushed.

`ReadPrefixEntries` uses a callback rather than returning a deferred `IEnumerable<ShardEntry>` so the local store owns the SQLite connection and reader lifetime. The callback should consume entries synchronously. For this change, it is acceptable to materialize one prefix at a time into a `Shard` and serialize that one prefix to a `byte[]` for remote upload. That keeps peak memory bounded by one remote shard plus bounded worker concurrency while avoiding a complicated streaming serializer rewrite. The implementation should not keep multiple materialized shards beyond the bounded flush/repair worker count and should not cache materialized `Shard` objects in memory after the operation.

Avoid putting `GetOrAdd`-style remote loading on the local store. The “add” side requires remote shard download and wire deserialization, so that coordination belongs in the reader/writer orchestration path. A dedicated prefix-loader helper is optional, not required. Use the decision rule: keep prefix-loading coordination inline while only one or two call sites need it; extract a focused internal helper only if lookup and flush share enough code that extraction reduces complexity.

### Suggested local schema

Use binary hash storage to avoid string overhead and keep indexes compact.
Use one row table for both hydrated remote entries and unflushed archive entries. The `dirty` flag distinguishes discardable cache rows from current archive operational state:

- `dirty = 0`: hydrated remote/cache row, safe to discard during cache invalidation.
- `dirty = 1`: unflushed archive row, protected state that must not be silently lost.

```sql
CREATE TABLE IF NOT EXISTS chunk_index_entries (
    content_hash    BLOB NOT NULL PRIMARY KEY,
    chunk_hash      BLOB NOT NULL,
    original_size   INTEGER NOT NULL,
    compressed_size INTEGER NOT NULL,
    prefix          TEXT NOT NULL,
    dirty           INTEGER NOT NULL DEFAULT 0,
    recorded_order  INTEGER
);

CREATE INDEX IF NOT EXISTS ix_chunk_index_entries_prefix
    ON chunk_index_entries(prefix, content_hash);

CREATE INDEX IF NOT EXISTS ix_chunk_index_entries_dirty_prefix
    ON chunk_index_entries(dirty, prefix);

CREATE TABLE IF NOT EXISTS loaded_prefixes (
    prefix                      TEXT NOT NULL PRIMARY KEY,
    remote_exists               INTEGER NOT NULL,
    remote_etag                 TEXT,
    validated_snapshot_identity TEXT NOT NULL,
    loaded_at_utc               TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS metadata (
    key             TEXT NOT NULL PRIMARY KEY,
    value           TEXT NOT NULL
);
```

`recorded_order` is available if deterministic last-writer-wins behavior needs to be audited or tested, though `content_hash` primary-key upsert is enough for current duplicate semantics. Archive `AddEntry` upserts the row with `dirty = 1`, so same-run lookup naturally sees the new value from the same table.

`metadata` should include `schema_version = 1`. On incompatible schema version, the local store can recreate the database because it is cache state.

Store route-related values in a way that can evolve later. For the fixed-prefix change, `prefix` is the current two-character prefix from `Shard.PrefixOf`. A later dynamic-prefix change can replace that with a leaf prefix from a route table without changing feature callers.

### Lookup freshness and validation

The loaded-prefix state is snapshot-scoped. The implementation should resolve the current latest snapshot identity once per archive, restore, or list operation and use it as the local cache epoch for clean chunk-index rows. The identity can be the latest snapshot blob name or another stable repository snapshot identity already available through snapshot resolution. SQLite remains hidden behind `ChunkIndexService`; the implementation can pass the identity into chunk-index operations or provide it through an internal shared repository-state dependency.

When the current latest snapshot identity matches `loaded_prefixes.validated_snapshot_identity`, clean rows for that prefix can be trusted without a remote shard metadata call. When it differs, the prefix is not automatically purged. Instead, the next operation that touches that prefix revalidates only that prefix against the remote shard identity:

```text
1. Read loaded_prefixes for the touched prefix.
2. If validated_snapshot_identity matches the current latest snapshot identity, trust local clean rows.
3. If it differs or no loaded-prefix row exists, read remote identity for chunk-index/<prefix>:
   a. missing shard -> remote identity is (exists = false, etag = null)
   b. existing shard -> remote identity is (exists = true, etag = remote ETag)
4. If the stored remote identity matches the current remote identity:
   a. keep existing clean rows
   b. update validated_snapshot_identity for that prefix to the current latest snapshot identity
5. If the remote identity differs:
   a. delete clean rows for that prefix only
   b. preserve dirty rows for that prefix
   c. download and ingest the remote shard as clean rows, or ingest an empty prefix if the shard is missing
   d. store the current remote identity and current latest snapshot identity in loaded_prefixes
```

This avoids a repository-wide cache purge when only one shard changed. It also avoids listing or checking every chunk-index shard on every operation. Remote shard metadata calls are proportional to prefixes actually touched by the operation and only needed when a prefix was loaded under a different snapshot identity or was not loaded locally.

Dirty rows must be preserved during prefix refresh because archive-tail flush can have current-run entries already recorded for a prefix whose remote base needs to be revalidated before upload. Failed flush retry paths can also leave dirty rows that must be merged with the refreshed remote base rather than silently discarded.

Cross-machine explicit repair invalidation is out of scope for this change. A repair on another machine can rewrite chunk-index shards without publishing a new snapshot. Detecting that immediately requires a separate chunk-index epoch/manifest marker or equivalent remote coordination and is tracked as future work.

### Lookup flow

Single-hash lookup:

```text
1. Resolve the hash's current prefix.
2. Ensure the prefix is loaded and validated for the current latest snapshot identity using the lazy per-prefix validation flow.
3. Check chunk_index_entries by content_hash.
4. Return miss if the validated loaded prefix does not contain the hash.
```

Batched lookup groups hashes by prefix and ensures each prefix is loaded at most once. It should avoid materializing full shard dictionaries. It may still return an `IReadOnlyDictionary` because that is the current public API, but internally it should read only requested hashes from SQLite.

Restore/list pipeline scalability is in scope at the chunk-index API boundary. The service should support bounded lookup consumption, either by adding a streaming lookup API or by changing restore/list callers to use bounded batches over the existing lookup API. SQLite remains hidden behind `ChunkIndexService`.

### Archive entry recording

`AddEntry` upserts directly into `chunk_index_entries` with `dirty = 1`. This preserves same-session visibility without an in-memory dictionary or a separate pending table.

Concurrent archive workers may call `AddEntry`. The local store should serialize writes through one connection/gate or use short transactions in a way that is safe with SQLite's single-writer model. It is acceptable for dirty-row writes to be serialized because each write is small and disk-backed; if profiling shows overhead, the implementation can batch through a bounded channel, but it must not reintroduce an unbounded managed-memory queue.

`AddEntry` should still fail fast when a flush is already in progress, matching the existing archive-tail contract.

### Flush flow

Flush uses dirty rows as the source of touched prefixes:

```text
1. Set a simple in-process flush-in-progress guard so accidental overlapping `AddEntry` calls throw.
2. Query distinct touched prefixes from chunk_index_entries where dirty = 1 after the guard is set.
3. For each touched prefix with bounded parallelism:
   a. ensure the remote/base prefix is loaded and validated for the current latest snapshot identity, preserving dirty rows while refreshing stale clean rows
   b. stream all chunk_index_entries rows for that prefix ordered by content_hash into remote shard serialization
   c. upload chunk-index/<prefix>
4. Set dirty = 0 for flushed dirty rows and update loaded-prefix remote identity only after all touched prefixes upload successfully.
```

Ordering the streamed output preserves deterministic shard serialization. This does not require keeping an in-memory `Shard` dictionary.

Because the current shard count and future dynamic split direction bound individual shard size, upload may materialize one prefix at a time into `Shard` and a serialized `byte[]`. This is accepted bounded memory. It must not reintroduce a repository-wide memory cache or keep materialized shards beyond the bounded workers currently flushing or repairing prefixes.

Partial failure behavior remains conservative: if any prefix upload fails, the archive fails and no snapshot is published. Dirty rows remain in SQLite for the current local state. A later rerun can retry the flush or explicit repair can rebuild from committed chunks.

If the storage boundary exposes conditional uploads by remote ETag or create-if-missing semantics, flush should use them for the touched prefix to avoid overwriting another writer's shard update between prefix validation and upload. On a conditional conflict, the implementation should refresh the prefix again, merge the preserved dirty rows with the new remote base, and retry within bounded policy or fail clearly. If conditional upload support is not yet available, the implementation must still preserve dirty rows and fail without snapshot publication on upload errors; stronger lost-update protection can be added when the storage boundary exposes ETag conditions.

The implementation should be careful when combining SQLite transactions and remote upload. It cannot make a remote blob upload and SQLite update atomically transactional. The existing safety rule remains: snapshot publication waits for flush success, and repair can recover from partial remote shard updates.

### Full repair flow

Full repair should stop grouping all reconstructed entries in memory. Instead:

```text
1. Write the repair-in-progress marker outside the purgeable local chunk-index cache.
2. Move any existing cache.sqlite aside to a best-effort .bak file and create a fresh SQLite database in place.
3. Stream one metadata-aware chunks/ listing.
4. For each large or thin committed chunk, reconstruct one ShardEntry and upsert it into chunk_index_entries with dirty = 0.
5. Derive rebuilt prefixes from SQLite, not a managed all-entry grouping.
6. Stream each rebuilt prefix from SQLite to remote shard upload with bounded parallelism.
7. List existing remote chunk-index shards and delete stale shard blobs whose prefixes were not rebuilt.
8. Clear the repair marker only after successful upload and stale deletion.
```

For the current fixed two-character prefix layout, rebuilt prefix count is at most 256, so the prefix set is not the main memory risk. Still, putting repair state in SQLite keeps the design aligned with future dynamic prefixes where leaf count can be much larger.

Repair remains explicit and idempotent. It does not publish snapshots.

### Cache invalidation and local corruption

`InvalidateCaches` should clear discardable local chunk-index cache rows (`dirty = 0`) and loaded-prefix state only when a full local cache purge is deliberately requested, but routine snapshot changes should prefer lazy per-prefix revalidation. In either case, invalidation must not delete the repair-in-progress marker and must not silently discard dirty rows. Archive-tail snapshot mismatch invalidation must preserve dirty rows recorded for the current archive.

`ClearCleanCache` should delete `dirty = 0` rows and clear all `loaded_prefixes`. It should preserve `dirty = 1` rows. A dirty row alone does not mean its prefix is fully loaded; later flush must still ensure the prefix is loaded from remote before uploading the complete shard.

Local SQLite corruption should be treated as local cache corruption. The implementation may move the corrupt database aside to a `.bak` file or delete/recreate it because committed chunks and remote shards remain the durable source of truth. If corruption is detected during an archive run with dirty rows, the operation must fail clearly and must not publish a snapshot; a rerun or explicit repair can recover from committed chunks.

- If no archive flush is in progress, delete/recreate the database and rehydrate from remote as needed.
- If corruption interrupts an active archive or flush, fail clearly rather than continuing as if dirty rows were flushed.

This preserves the distinction between discardable cache state and current-run operational state.

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

### Restore and list bounded lookup

`RestoreCommandHandler` currently materializes all distinct content hashes and all returned index entries for a restore plan. This does not scale for very large restores.

This change should stop restore and list from requiring unbounded chunk-index lookup materialization while preserving dependency boundaries. It does not require a full restore streaming-plan rewrite. SQLite must not leak into restore or list. Possible API shapes include:

```csharp
IAsyncEnumerable<ShardEntry> LookupStreamingAsync(
    IAsyncEnumerable<ContentHash> contentHashes,
    CancellationToken cancellationToken);
```

or bounded-batch APIs that keep `ChunkIndexService` as the only dependency. The exact API can be chosen during implementation, but restore and list should consume chunk-index lookup inputs and results in bounded batches or streams where possible rather than `ToList`-ing all candidate content hashes and one full lookup dictionary for very large operations.

## Risks / Trade-offs

- **[Risk] SQLite native packaging/runtime issues** -> Use `Microsoft.Data.Sqlite` only inside chunk-index internals and verify test/CLI packaging on supported platforms.
- **[Risk] SQLite write serialization slows archive workers** -> Use transactions, reused parameterized commands, and bounded ingestion batches without unbounded memory queues.
- **[Risk] Local SQLite dirty rows blur cache vs operational state** -> Treat hydrated shard rows as discardable, and fail the active operation if local corruption interrupts dirty state before snapshot publication.
- **[Risk] Dynamic prefixes may later need schema changes** -> Keep routing internal and store prefix values generically as leaf prefixes, not as externally meaningful fixed two-character values.
- **[Risk] Remote shard uploads still rewrite whole touched prefixes** -> Accepted for this change. Dynamic prefixes are the follow-up intended to reduce remote rewrite amplification.
- **[Risk] Restore/list bounded lookup expands scope** -> Keep the change at the chunk-index API/caller batching boundary and avoid introducing SQLite dependencies outside chunk-index internals.

## Migration Plan

No remote migration is required. Existing remote chunk-index shard blobs remain valid.

Local migration can be cache-based:

1. New versions create `cache.sqlite` under the chunk-index local state directory.
2. Existing plaintext L2 shard files may be ignored or deleted as stale cache state.
3. Needed prefixes are rehydrated from remote `chunk-index/<prefix>` blobs on demand.
4. When moving or deleting the SQLite cache, include `cache.sqlite`, `cache.sqlite-wal`, and `cache.sqlite-shm` after all local-store connections are closed. Best-effort backups should use a `.bak` suffix for the database family.
5. If rollback occurs, the old implementation can ignore or delete `cache.sqlite` and rehydrate plaintext L2 files from remote shards as before.

Because local cache state is not the source of truth, no durable data migration is required.
