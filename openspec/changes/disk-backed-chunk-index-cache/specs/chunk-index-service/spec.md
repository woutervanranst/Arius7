## ADDED Requirements

### Requirement: Disk-backed local chunk-index state ownership
The chunk-index implementation SHALL store hydrated shard rows, loaded-prefix state, unflushed archive entries, and repair staging state in one SQLite-backed local store owned by `Shared/ChunkIndex` internals. The local store SHALL be the only component that owns SQLite schema, SQLite connections, and transactions for chunk-index local state.

The local store SHALL represent hydrated remote entries and unflushed archive entries in one `chunk_index_entries` table keyed by content hash. Rows with `dirty = 0` SHALL be treated as discardable hydrated cache rows. Rows with `dirty = 1` SHALL be treated as protected unflushed archive operational state and SHALL NOT be silently lost during cache invalidation or local corruption handling.

Archive write-session or writer components SHALL NOT own an independent pending-entry collection after pending entries move to SQLite. A writer component MAY remain as an internal orchestration or policy boundary for entry-recording and flush behavior, but it SHALL delegate dirty-row persistence, dirty-prefix queries, dirty-row lookup visibility, and dirty cleanup to the local store.

The local store SHALL NOT upload or download remote shard blobs and SHALL NOT own archive-tail publication policy. Remote shard download/upload, shard wire serialization, flush-in-progress rejection, and partial-flush behavior SHALL remain coordinated outside the local store through `ChunkIndexService` and focused chunk-index internals.

The local SQLite schema SHALL use a single entry table with a dirty flag equivalent to:

```sql
CREATE TABLE chunk_index_entries (
    content_hash    BLOB NOT NULL PRIMARY KEY,
    chunk_hash      BLOB NOT NULL,
    original_size   INTEGER NOT NULL,
    compressed_size INTEGER NOT NULL,
    prefix          TEXT NOT NULL,
    dirty           INTEGER NOT NULL DEFAULT 0,
    recorded_order  INTEGER
);

CREATE INDEX ix_chunk_index_entries_prefix
    ON chunk_index_entries(prefix, content_hash);

CREATE INDEX ix_chunk_index_entries_dirty_prefix
    ON chunk_index_entries(dirty, prefix);
```

The local store SHALL expose a domain-specific API for local chunk-index facts and mutations. The API SHALL provide operations equivalent to:

```csharp
Task InitializeAsync(CancellationToken cancellationToken);

Task<ShardEntry?> GetValueOrDefaultAsync(
    ContentHash contentHash,
    CancellationToken cancellationToken);

Task UpsertDirtyAsync(
    ShardEntry entry,
    CancellationToken cancellationToken);

Task UpsertCleanRangeAsync(
    PathSegment prefix,
    IAsyncEnumerable<ShardEntry> entries,
    CancellationToken cancellationToken);

Task<bool> IsPrefixLoadedAsync(
    PathSegment prefix,
    CancellationToken cancellationToken);

Task MarkPrefixLoadedAsync(
    PathSegment prefix,
    CancellationToken cancellationToken);

IAsyncEnumerable<PathSegment> GetDirtyPrefixesAsync(
    CancellationToken cancellationToken);

IAsyncEnumerable<ShardEntry> ReadPrefixEntriesAsync(
    PathSegment prefix,
    CancellationToken cancellationToken);

Task MarkDirtyPrefixesCleanAsync(
    IReadOnlyCollection<PathSegment> prefixes,
    CancellationToken cancellationToken);

Task ClearCleanCacheAsync(CancellationToken cancellationToken);

Task<bool> HasDirtyRowsAsync(CancellationToken cancellationToken);
```

`UpsertCleanRangeAsync` SHALL ingest rows loaded from a remote shard with `dirty = 0` and SHALL NOT overwrite a local row whose current value has `dirty = 1`. Prefix-loading behavior SHALL be coordinated outside the local store because it requires remote shard download and wire deserialization, but the spec does not require a dedicated prefix-loader class. The implementation MAY keep that coordination inside the reader/writer path or extract a focused internal helper when both lookup and flush share enough behavior to justify it.

#### Scenario: Local store owns pending state
- **WHEN** archive workers record new chunk-index entries during an archive run
- **THEN** the entries SHALL be upserted to the SQLite-backed local store with `dirty = 1`
- **AND** no write-session component SHALL keep an independent content-hash-keyed pending-entry collection in managed memory

#### Scenario: Dirty rows are lookup-visible
- **WHEN** a chunk-index lookup requests a content hash recorded earlier in the same archive run
- **THEN** the lookup SHALL read the matching `chunk_index_entries` row
- **AND** the row SHALL be visible even when it has not yet been flushed to the remote chunk index

#### Scenario: Remote rows are ingested as clean cache rows
- **WHEN** chunk-index code downloads and deserializes a remote shard for a prefix
- **THEN** it SHALL ingest the remote shard entries through `UpsertCleanRangeAsync`
- **AND** the ingested rows SHALL be stored with `dirty = 0`
- **AND** any existing local dirty row for the same content hash SHALL remain protected and SHALL NOT be overwritten by clean remote ingestion

#### Scenario: Writer coordinates without owning state
- **WHEN** chunk-index flush starts for pending archive entries
- **THEN** an internal writer or `ChunkIndexService` SHALL enforce flush-in-progress and concurrent-flush policy
- **AND** it SHALL ask the local store for prefixes with `dirty = 1`
- **AND** it SHALL NOT bypass the local store's transactions for dirty-row state changes

#### Scenario: Flush streams dirty prefixes
- **WHEN** chunk-index flush processes a prefix containing dirty rows
- **THEN** the flush SHALL ensure the prefix is fully loaded from the remote chunk index before upload
- **AND** it SHALL stream all rows for that prefix ordered by content hash from `chunk_index_entries`
- **AND** it SHALL upload the resulting shard to the remote chunk index
- **AND** dirty rows SHALL be marked `dirty = 0` only after all touched prefixes upload successfully

#### Scenario: Local store API stays storage-focused
- **WHEN** chunk-index internals need to ensure a prefix is loaded from remote storage
- **THEN** they SHALL coordinate remote download and shard deserialization outside the local store
- **AND** they SHALL use the local store only for loaded-prefix checks, clean-row ingestion, loaded-prefix marking, and row reads
- **AND** the implementation SHALL NOT add a separate prefix-loader class solely to expose a collection-style `GetOrAdd` method

#### Scenario: Local store does not own remote publication
- **WHEN** a touched prefix must be flushed to the remote chunk index
- **THEN** the local store SHALL provide disk-backed rows for that prefix
- **AND** a separate chunk-index component SHALL serialize and upload the remote shard blob
- **AND** the local store SHALL NOT treat a SQLite transaction as an atomic transaction with remote blob upload

#### Scenario: Single owner for SQLite schema
- **WHEN** chunk-index internals need to read or mutate local chunk-index tables
- **THEN** they SHALL do so through the SQLite-backed local store
- **AND** no separate reader, writer, repair, or shard-cache component SHALL define or independently manage its own SQLite tables for chunk-index state

#### Scenario: Cache invalidation preserves dirty rows
- **WHEN** chunk-index cache invalidation clears local cache state
- **THEN** rows with `dirty = 0` MAY be discarded
- **AND** rows with `dirty = 1` SHALL be preserved or the operation SHALL fail clearly without treating the dirty rows as successfully discarded

## MODIFIED Requirements

### Requirement: Explicit full chunk-index repair
The system SHALL provide an explicit full chunk-index repair API and command that rebuilds all chunk-index shards from committed chunk blobs using the configured shard prefix length. Full repair SHALL use SQLite-backed local repair state instead of grouping all reconstructed entries in managed memory. Repair SHALL stream one metadata-aware chunk listing from `ListAsync("chunks/", includeMetadata: true, ...)`, reconstruct large and thin chunk-index entries, and upsert each reconstructed entry into disk-backed local repair state as it is discovered.

Full repair SHALL write a repair-in-progress marker before clearing or rebuilding local chunk-index state. The repair marker SHALL live outside the purgeable local chunk-index cache and SHALL NOT be deleted by cache invalidation. Normal chunk-index operations that trust or mutate chunk-index state, including lookup, entry recording, and pending-entry flush, SHALL fail clearly while the marker exists. The explicit repair API SHALL be allowed to run when the marker already exists so an interrupted repair can be rerun.

After reconstructed entries are staged locally, full repair SHALL stream each rebuilt prefix from SQLite ordered by content hash, serialize each remote shard using the existing shard wire format, upload every non-empty rebuilt shard to `chunk-index/<prefix>`, and delete stale remote shard blobs whose prefixes were not rebuilt. Full repair SHALL clear dirty repair state and the repair marker only after rebuilt shard upload and stale-shard deletion succeed. Full repair SHALL NOT publish snapshots.

Full repair remains explicit and idempotent. If full repair is interrupted while staging entries, uploading rebuilt shards, or deleting stale remote shards, a later full repair SHALL purge partial repair state and reconstruct shard contents again from committed chunks. Distributed locking or remote repair leases remain out of scope.

#### Scenario: Full repair stages reconstructed entries on disk
- **WHEN** full chunk-index repair scans committed large and thin chunk blobs
- **THEN** it SHALL reconstruct chunk-index entries for those chunks
- **AND** it SHALL upsert each reconstructed entry into SQLite-backed local repair state
- **AND** it SHALL NOT group all reconstructed entries by shard prefix in managed memory

#### Scenario: Full repair streams rebuilt prefixes from SQLite
- **WHEN** full chunk-index repair has staged reconstructed entries locally
- **THEN** it SHALL stream rows for each rebuilt prefix from SQLite ordered by content hash
- **AND** it SHALL serialize and upload a remote chunk-index shard for every non-empty rebuilt prefix
- **AND** persisted shard output SHALL remain deterministic

#### Scenario: Full repair scans chunks once
- **WHEN** full chunk-index repair runs
- **THEN** it SHALL perform one metadata-aware listing for `chunks/`
- **AND** it SHALL NOT rebuild by issuing one chunk listing per possible shard prefix

#### Scenario: Full repair deletes stale shards
- **WHEN** full chunk-index repair completes rebuilt shard uploads
- **AND** an existing `chunk-index/` shard blob is not in the rebuilt prefix set tracked by local repair state
- **THEN** full repair SHALL delete that stale shard blob

#### Scenario: Full repair can be rerun after interruption
- **WHEN** full chunk-index repair is interrupted after staging only some entries or writing only some remote shard prefixes
- **THEN** rerunning full repair SHALL purge partial repair state
- **AND** it SHALL reconstruct shard contents again from committed chunks

#### Scenario: Full repair does not publish snapshot
- **WHEN** full chunk-index repair writes repaired shard blobs
- **THEN** it SHALL NOT create or update any snapshot manifest
