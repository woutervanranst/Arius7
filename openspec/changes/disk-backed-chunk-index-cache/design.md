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
- Keep the SQLite dependency isolated to `src/Arius.Core/Shared/ChunkIndex/` and use raw ADO.NET-style access, not EF Core.
- Preserve same-service visibility for entries recorded during the current archive session before they are flushed.
- Make full repair stream reconstructed entries into disk-backed local state instead of grouping all entries in memory.
- Leave clear seams for a later dynamic chunk-prefix change.

**Non-Goals:**

- Do not introduce dynamic prefix splitting, route manifests, longest-prefix lookup, or stale-parent cleanup in this change.
- Do not upload or download a local SQLite database as the remote chunk index.
- Do not change remote shard serialization or encryption behavior.
- Do not use EF Core.
- Do not redesign restore or list as fully streaming pipelines in this change.
- Do not make restore/list callers depend on SQLite or chunk-index implementation details.
- Do not introduce distributed locking for concurrent archive/repair operations.

## Decisions

### Use SQLite as the local chunk-index store

Use `Microsoft.Data.Sqlite` directly through raw commands, readers, and transactions. The database lives under the existing repository local state area, for example:

```text
~/.arius/{account}-{container}/chunk-index/cache.sqlite
```

SQLite replaces both the current L1 shard-page cache and the plaintext per-prefix L2 shard files. It stores local hydrated index rows and pending archive rows in B-tree indexes so point lookup does not require loading a whole shard into managed memory.

The local database is discardable. If it is missing, stale, or corrupt, chunk-index code can recreate it and rehydrate needed prefixes from remote `chunk-index/<prefix>` blobs. Explicit repair can rebuild remote shards from committed chunks when remote shard state itself is corrupt or incomplete.

Use conservative SQLite settings appropriate for a local cache:

- WAL mode for append/update behavior during archive and repair.
- A bounded SQLite page cache rather than managed object caches.
- Explicit transactions for shard ingestion, dirty-row recording, flush prefix updates, and repair staging.

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
- loaded-prefix freshness state
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
    prefix          TEXT NOT NULL PRIMARY KEY,
    loaded_at_utc   TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS metadata (
    key             TEXT NOT NULL PRIMARY KEY,
    value           TEXT NOT NULL
);
```

`recorded_order` is available if deterministic last-writer-wins behavior needs to be audited or tested, though `content_hash` primary-key upsert is enough for current duplicate semantics. Archive `AddEntry` upserts the row with `dirty = 1`, so same-run lookup naturally sees the new value from the same table.

Store route-related values in a way that can evolve later. For the fixed-prefix change, `prefix` is the current two-character prefix from `Shard.PrefixOf`. A later dynamic-prefix change can replace that with a leaf prefix from a route table without changing feature callers.

### Lookup flow

Single-hash lookup:

```text
1. Check chunk_index_entries by content_hash.
2. If not found and the hash's current prefix is not loaded:
   a. download remote chunk-index/<prefix> if it exists
   b. ingest entries into chunk_index_entries in one transaction with dirty = 0
   c. mark loaded_prefixes(prefix)
   d. retry pending/base lookup
3. Return miss if the loaded prefix does not contain the hash.
```

Batched lookup groups hashes by prefix and ensures each prefix is loaded at most once. It should avoid materializing full shard dictionaries. It may still return an `IReadOnlyDictionary` because that is the current public API, but internally it should read only requested hashes from SQLite.

Restore/list pipeline scalability remains a follow-up. This change should not make those handlers depend on SQLite, but the design should leave room for later APIs such as bounded-batch lookup or streaming lookup results.

### Archive entry recording

`AddEntry` upserts directly into `chunk_index_entries` with `dirty = 1`. This preserves same-session visibility without an in-memory dictionary or a separate pending table.

Concurrent archive workers may call `AddEntry`. The local store should serialize writes through one connection/gate or use short transactions in a way that is safe with SQLite's single-writer model. It is acceptable for dirty-row writes to be serialized because each write is small and disk-backed; if profiling shows overhead, the implementation can batch through a bounded channel, but it must not reintroduce an unbounded managed-memory queue.

`AddEntry` should still fail fast when a flush is already in progress, matching the existing archive-tail contract.

### Flush flow

Flush uses dirty rows as the source of touched prefixes:

```text
1. Prevent concurrent flush and reject overlapping AddEntry calls.
2. Query distinct touched prefixes from chunk_index_entries where dirty = 1.
3. For each touched prefix with bounded parallelism:
   a. ensure the remote/base prefix is loaded into chunk_index_entries
   b. stream all chunk_index_entries rows for that prefix ordered by content_hash into remote shard serialization
   c. upload chunk-index/<prefix>
4. Set dirty = 0 for flushed dirty rows only after all touched prefixes upload successfully.
```

Ordering the streamed output preserves deterministic shard serialization. This does not require keeping an in-memory `Shard` dictionary.

Partial failure behavior remains conservative: if any prefix upload fails, the archive fails and no snapshot is published. Dirty rows remain in SQLite for the current local state. A later rerun can retry the flush or explicit repair can rebuild from committed chunks.

The implementation should be careful when combining SQLite transactions and remote upload. It cannot make a remote blob upload and SQLite update atomically transactional. The existing safety rule remains: snapshot publication waits for flush success, and repair can recover from partial remote shard updates.

### Full repair flow

Full repair should stop grouping all reconstructed entries in memory. Instead:

```text
1. Write the repair-in-progress marker outside the purgeable local chunk-index cache.
2. Recreate or clear local SQLite chunk-index state for repair.
3. Stream one metadata-aware chunks/ listing.
4. For each large or thin committed chunk, reconstruct one ShardEntry and upsert it into a repair table or directly into chunk_index_entries.
5. Track rebuilt prefixes in SQLite, not in a managed HashSet when the representation may grow later.
6. Stream each rebuilt prefix from SQLite to remote shard upload with bounded parallelism.
7. List existing remote chunk-index shards and delete stale shard blobs whose prefixes were not rebuilt.
8. Clear dirty state and the repair marker only after successful upload and stale deletion.
```

For the current fixed two-character prefix layout, rebuilt prefix count is at most 256, so the prefix set is not the main memory risk. Still, putting repair state in SQLite keeps the design aligned with future dynamic prefixes where leaf count can be much larger.

Repair remains explicit and idempotent. It does not publish snapshots.

### Cache invalidation and local corruption

`InvalidateCaches` should clear discardable local chunk-index cache rows (`dirty = 0`) and loaded-prefix state, but it must not delete the repair-in-progress marker and must not silently discard dirty rows. Archive-tail snapshot mismatch invalidation must preserve dirty rows recorded for the current archive.

Local SQLite corruption should be treated like local cache corruption when safe:

- If no archive flush is in progress and no dirty rows are needed, delete/recreate the database and rehydrate from remote as needed.
- If dirty rows exist and the database is corrupt, fail clearly rather than silently discarding unflushed archive state.

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

### Restore and list scalability follow-up

`RestoreCommandHandler` currently materializes all distinct content hashes and all returned index entries for a restore plan. This does not scale for very large restores, but solving it properly likely requires restore-specific streaming or disk-backed planning.

Keep that out of this change to preserve dependency boundaries. SQLite should not leak into restore. A later restore-pipeline change can use chunk-index APIs such as:

```csharp
IAsyncEnumerable<ShardEntry> LookupStreamingAsync(
    IAsyncEnumerable<ContentHash> contentHashes,
    CancellationToken cancellationToken);
```

or bounded-batch APIs that keep `ChunkIndexService` as the only dependency.

## Risks / Trade-offs

- **[Risk] SQLite native packaging/runtime issues** -> Use `Microsoft.Data.Sqlite` only inside chunk-index internals and verify test/CLI packaging on supported platforms.
- **[Risk] SQLite write serialization slows archive workers** -> Start with simple short transactions; introduce bounded batching only if profiling proves needed, without unbounded memory queues.
- **[Risk] Local SQLite pending entries blur cache vs operational state** -> Treat hydrated shard rows as discardable, but fail clearly if local corruption would discard unflushed pending entries.
- **[Risk] Dynamic prefixes may later need schema changes** -> Keep routing internal and store prefix values generically as leaf prefixes, not as externally meaningful fixed two-character values.
- **[Risk] Remote shard uploads still rewrite whole touched prefixes** -> Accepted for this change. Dynamic prefixes are the follow-up intended to reduce remote rewrite amplification.
- **[Risk] Restore/list still materialize large caller-side collections** -> Document as a follow-up and avoid exposing SQLite outside chunk-index to solve it prematurely.

## Migration Plan

No remote migration is required. Existing remote chunk-index shard blobs remain valid.

Local migration can be cache-based:

1. New versions create `cache.sqlite` under the chunk-index local state directory.
2. Existing plaintext L2 shard files may be ignored or deleted as stale cache state.
3. Needed prefixes are rehydrated from remote `chunk-index/<prefix>` blobs on demand.
4. If rollback occurs, the old implementation can ignore or delete `cache.sqlite` and rehydrate plaintext L2 files from remote shards as before.

Because local cache state is not the source of truth, no durable data migration is required.
