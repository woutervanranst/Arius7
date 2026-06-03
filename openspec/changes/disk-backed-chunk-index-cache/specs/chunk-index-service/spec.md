## ADDED Requirements

### Requirement: Disk-backed local chunk-index state ownership
The chunk-index implementation SHALL store hydrated shard rows, loaded-prefix state, unflushed archive entries, and repair staging state in one SQLite-backed local store owned by `Shared/ChunkIndex` internals. The local store SHALL be the only component that owns SQLite schema, SQLite connections, and transactions for chunk-index local state. Loaded-prefix state SHALL include the latest snapshot identity and remote shard identity used when the prefix was last validated.

The local store SHALL represent hydrated remote entries and unflushed archive entries in one `chunk_index_entries` table keyed by content hash. Rows with `dirty = 0` SHALL be treated as discardable hydrated cache rows. Rows with `dirty = 1` SHALL be treated as protected unflushed archive operational state and SHALL NOT be silently lost during cache invalidation or local corruption handling.

Dirty rows SHALL be retryable across process restarts only because they are recorded after the referenced large chunk or thin chunk has been durably uploaded. Chunk-index entry recording SHALL NOT write a dirty row before the referenced chunk storage artifact is committed. A dirty row preserved after a failed archive-tail flush MAY be flushed by a later operation without requiring the failed archive run to have published a snapshot.

Archive write-session or writer components SHALL NOT own an independent pending-entry collection after pending entries move to SQLite. A writer component MAY remain as an internal orchestration or policy boundary for entry-recording and flush behavior, but it SHALL delegate dirty-row persistence, dirty-prefix queries, dirty-row lookup visibility, and dirty cleanup to the local store.

The local store SHALL NOT upload or download remote shard blobs and SHALL NOT own archive-tail publication policy. Remote shard download/upload, shard wire serialization, flush-in-progress rejection, and partial-flush behavior SHALL remain coordinated outside the local store through `ChunkIndexService` and focused chunk-index internals.

The local store SHALL use synchronous `Microsoft.Data.Sqlite` ADO.NET APIs for SQLite work. It SHALL NOT use `Microsoft.Data.Sqlite` async command or reader methods because SQLite does not support asynchronous I/O and those async overloads execute synchronously. Chunk-index operations MAY remain async at higher orchestration boundaries where they perform remote blob I/O.

The local store SHALL build SQLite connection strings with `SqliteConnectionStringBuilder`. The local store SHALL enable write-ahead logging for the local cache database and SHALL avoid SQLite shared-cache mode with WAL. SQLite writes SHALL be serialized by the local store. Prefix hydration and prefix flush/upload coordination SHALL be serialized per prefix so concurrent operations cannot download, ingest, stream, or upload the same prefix at the same time.

Local SQLite corruption SHALL be treated as local cache corruption. The implementation MAY move the corrupt database aside to a `.bak` file or delete and recreate it. If corruption is detected during an active archive or flush before a snapshot is published, the operation SHALL fail clearly and SHALL NOT continue as if dirty rows were flushed.

The implementation SHALL keep dirty rows, corrupt local SQLite state, and corrupt remote chunk-index shards as separate states. Dirty rows are valid unflushed archive state. Corrupt local SQLite is local cache corruption and MAY be deleted or recreated. Corrupt remote chunk-index shards are repository metadata corruption and SHALL be handled by failing normal operations with repair guidance and by explicit full repair from committed chunks.

The local SQLite schema SHALL use a single entry table with a dirty flag equivalent to:

```sql
CREATE TABLE chunk_index_entries (
    content_hash    BLOB NOT NULL PRIMARY KEY,
    chunk_hash      BLOB NOT NULL,
    original_size   INTEGER NOT NULL CHECK (original_size >= 0),
    compressed_size INTEGER NOT NULL CHECK (compressed_size >= 0),
    prefix          TEXT NOT NULL,
    dirty           INTEGER NOT NULL DEFAULT 0 CHECK (dirty IN (0, 1)),
    recorded_order  INTEGER,
    CHECK (length(content_hash) = 32),
    CHECK (length(chunk_hash) = 32),
    CHECK (length(prefix) > 0)
);

CREATE INDEX ix_chunk_index_entries_prefix
    ON chunk_index_entries(prefix, content_hash);

CREATE INDEX ix_chunk_index_entries_dirty_prefix
    ON chunk_index_entries(dirty, prefix);

CREATE TABLE loaded_prefixes (
    prefix                      TEXT NOT NULL PRIMARY KEY,
    remote_exists               INTEGER NOT NULL CHECK (remote_exists IN (0, 1)),
    remote_etag                 TEXT,
    validated_snapshot_identity TEXT NOT NULL,
    CHECK (length(prefix) > 0),
    CHECK (length(validated_snapshot_identity) > 0)
);

CREATE TABLE metadata (
    key             TEXT NOT NULL PRIMARY KEY,
    value           TEXT NOT NULL,
    CHECK (length(key) > 0),
    CHECK (length(value) > 0)
);
```

The local store SHALL expose a domain-specific API for local chunk-index facts and mutations. The API SHALL provide operations equivalent to:

```csharp
void Initialize();

ShardEntry? GetValueOrDefault(ContentHash contentHash);

void UpsertDirty(ShardEntry entry);

void UpsertDirtyRange(IEnumerable<ShardEntry> entries);

void IngestCleanPrefix(
    PathSegment prefix,
    IEnumerable<ShardEntry> entries);

LoadedPrefixState? GetLoadedPrefixState(PathSegment prefix);

IEnumerable<PathSegment> GetDirtyPrefixes();

void ReadPrefixEntries(
    PathSegment prefix,
    Action<ShardEntry> consume);

void MarkDirtyPrefixesClean(IReadOnlyCollection<PathSegment> prefixes);

void ClearCleanCache();

bool HasDirtyRows();
```

`IngestCleanPrefix` SHALL ingest rows loaded from a remote shard with `dirty = 0`, SHALL NOT overwrite a local row whose current value has `dirty = 1`, and SHALL mark the prefix loaded in the same SQLite transaction as the clean-row ingestion. The loaded-prefix state SHALL record whether the remote shard existed, the remote shard ETag when available, and the current latest snapshot identity used for validation. `IngestCleanPrefix` and `UpsertDirtyRange` SHALL use one transaction and a reused parameterized command for bulk insert/update behavior. The command SHALL be assigned to the active transaction, and its parameter objects SHALL be created once and assigned new values per row. Hash, size, prefix, and dirty-flag parameters SHOULD use explicit SQLite types. Prefix-loading behavior SHALL be coordinated outside the local store because it requires remote shard metadata lookup, remote shard download, and wire deserialization, but the spec does not require a dedicated prefix-loader class. The implementation MAY keep that coordination inside the reader/writer path or extract a focused internal helper when both lookup and flush share enough behavior to justify it.

Chunk-index lookup and flush SHALL validate loaded prefixes lazily against the current latest snapshot identity. When a loaded prefix was validated under the same latest snapshot identity, the implementation MAY trust local clean rows without probing the remote shard. When a loaded prefix is missing or was validated under a different latest snapshot identity, the implementation SHALL revalidate only that touched prefix against the current remote shard identity before trusting clean rows. If the remote identity matches the stored identity, the implementation SHALL update the prefix's validated snapshot identity without deleting clean rows. If the remote identity differs, the implementation SHALL delete clean rows for that prefix only, preserve dirty rows for that prefix, download and ingest the current remote shard as clean rows, or ingest an empty prefix when the remote shard is missing.

Routine snapshot changes SHALL NOT require a repository-wide purge of all clean chunk-index rows. The implementation SHALL avoid listing or checking every remote shard as the normal freshness path. Remote shard identity checks SHALL be proportional to prefixes touched by the current operation unless a later optimization deliberately chooses a bounded bulk identity listing for a command that touches many prefixes.

`ReadPrefixEntries` SHALL keep SQLite connection and reader lifetime owned by the local store. It SHALL consume rows through a synchronous callback or equivalent local-store-owned iteration pattern rather than returning a deferred sequence whose database resources can escape the local store boundary. Chunk-index serialization MAY materialize one prefix at a time into a `Shard` and serialized byte array for remote upload. This is accepted bounded memory when limited to one prefix per bounded worker and when materialized shard objects are not cached after the operation.

The local store SHALL track `schema_version = 1` in the `metadata` table. If the local SQLite schema version is incompatible, the local store MAY recreate the database because it is cache state.

#### Scenario: Local store owns pending state
- **WHEN** archive workers record new chunk-index entries during an archive run
- **THEN** the entries SHALL be upserted to the SQLite-backed local store with `dirty = 1`
- **AND** no write-session component SHALL keep an independent content-hash-keyed pending-entry collection in managed memory

#### Scenario: Dirty rows are lookup-visible
- **WHEN** a chunk-index lookup requests a content hash recorded earlier in the same archive run
- **THEN** the lookup SHALL read the matching `chunk_index_entries` row
- **AND** the row SHALL be visible even when it has not yet been flushed to the remote chunk index

#### Scenario: Dirty rows are recorded after durable chunk upload
- **WHEN** archive code records a dirty chunk-index row for a large chunk or thin chunk
- **THEN** the referenced chunk storage artifact SHALL already have been durably uploaded
- **AND** a later retry MAY flush the dirty row even if the archive run that recorded it failed before publishing a snapshot

#### Scenario: Remote rows are ingested as clean cache rows
- **WHEN** chunk-index code downloads and deserializes a remote shard for a prefix
- **THEN** it SHALL ingest the remote shard entries through `IngestCleanPrefix`
- **AND** the ingested rows SHALL be stored with `dirty = 0`
- **AND** the prefix SHALL be marked loaded in the same SQLite transaction as row ingestion
- **AND** the loaded-prefix row SHALL record the current latest snapshot identity and remote shard identity used for validation
- **AND** any existing local dirty row for the same content hash SHALL remain protected and SHALL NOT be overwritten by clean remote ingestion

#### Scenario: Missing remote shard marks empty prefix loaded
- **WHEN** chunk-index lookup or flush ensures a prefix is loaded and the remote shard blob does not exist
- **THEN** it SHALL ingest an empty clean prefix through `IngestCleanPrefix`
- **AND** the prefix SHALL be marked loaded in SQLite in the same transaction
- **AND** the loaded-prefix row SHALL record that the remote shard did not exist
- **AND** later misses for that prefix SHALL NOT repeatedly download or probe the missing remote shard during the same local cache epoch

#### Scenario: Same snapshot identity trusts loaded prefix
- **WHEN** lookup or flush touches a prefix whose loaded-prefix row was validated against the current latest snapshot identity
- **THEN** chunk-index code MAY trust the local clean rows for that prefix
- **AND** it SHALL NOT perform a remote shard metadata call solely to validate freshness

#### Scenario: Changed snapshot identity revalidates touched prefix lazily
- **WHEN** lookup or flush touches a prefix whose loaded-prefix row was validated against an older latest snapshot identity
- **THEN** chunk-index code SHALL check the current remote identity for `chunk-index/<prefix>`
- **AND** it SHALL NOT purge clean rows for unrelated prefixes solely because the latest snapshot identity changed

#### Scenario: Unchanged remote identity advances prefix validation
- **WHEN** a touched prefix has an older validated snapshot identity
- **AND** the current remote shard existence and ETag match the stored loaded-prefix remote identity
- **THEN** the implementation SHALL keep the existing clean rows for that prefix
- **AND** it SHALL update the loaded-prefix row to the current latest snapshot identity

#### Scenario: Changed remote identity refreshes one prefix
- **WHEN** a touched prefix has an older validated snapshot identity
- **AND** the current remote shard existence or ETag differs from the stored loaded-prefix remote identity
- **THEN** the implementation SHALL delete clean rows for that prefix only
- **AND** it SHALL preserve dirty rows for that prefix
- **AND** it SHALL ingest the current remote shard contents as clean rows or mark the prefix loaded-empty when the remote shard is missing
- **AND** it SHALL store the current latest snapshot identity and remote shard identity for that prefix

#### Scenario: Snapshot change does not force all-shard validation
- **WHEN** the latest snapshot identity differs from the snapshot identity used by some loaded prefixes
- **THEN** normal lookup and flush SHALL validate only prefixes touched by the current operation
- **AND** they SHALL NOT list or HEAD every remote chunk-index shard as the required freshness path

#### Scenario: SQLite work uses synchronous APIs
- **WHEN** chunk-index local-store code executes SQLite commands or reads SQLite rows
- **THEN** it SHALL use synchronous Microsoft.Data.Sqlite APIs
- **AND** it SHALL NOT call async Microsoft.Data.Sqlite command or reader methods for local SQLite work

#### Scenario: SQLite connection strings are builder-created
- **WHEN** the local store opens the local SQLite cache database
- **THEN** it SHALL build the connection string with `SqliteConnectionStringBuilder`
- **AND** the database file path SHALL be assigned as the `DataSource` value rather than interpolated into a raw connection string

#### Scenario: Bulk ingestion uses transactions and prepared commands
- **WHEN** the local store ingests many clean or dirty chunk-index entries
- **THEN** it SHALL perform the writes in a SQLite transaction
- **AND** it SHALL reuse a parameterized command across rows rather than compiling one command per row
- **AND** the command SHALL be assigned to the active transaction
- **AND** command parameters SHALL be created once and assigned new values per row

#### Scenario: Async-to-sync ingestion uses bounded channels
- **WHEN** remote shard download or deserialization feeds entries into synchronous SQLite ingestion
- **THEN** the bridge between asynchronous remote I/O and synchronous SQLite writes SHALL be bounded
- **AND** it SHALL NOT buffer an unbounded number of shard entries in managed memory

#### Scenario: Writer coordinates without owning state
- **WHEN** chunk-index flush starts for pending archive entries
- **THEN** an internal writer or `ChunkIndexService` SHALL enforce flush-in-progress and concurrent-flush policy
- **AND** it SHALL ask the local store for prefixes with `dirty = 1`
- **AND** it SHALL NOT bypass the local store's transactions for dirty-row state changes

#### Scenario: Flush streams dirty prefixes
- **WHEN** chunk-index flush processes a prefix containing dirty rows
- **THEN** the flush SHALL ensure the prefix is fully loaded and validated against the current latest snapshot identity before upload
- **AND** stale clean rows for that prefix MAY be refreshed while preserving dirty rows recorded by the current archive run
- **AND** it SHALL stream all rows for that prefix ordered by content hash from `chunk_index_entries`
- **AND** it SHALL upload the resulting shard to the remote chunk index
- **AND** dirty rows SHALL be marked `dirty = 0` only after all touched prefixes upload successfully

#### Scenario: Dirty rows survive prefix refresh before flush
- **WHEN** archive-tail flush refreshes a touched prefix because the stored remote identity is stale after an earlier lookup, a direct `AddEntry`, or a failed previous flush left dirty rows locally
- **THEN** clean rows for that prefix MAY be deleted and replaced from the remote shard
- **AND** dirty rows for current-run archive entries SHALL be preserved
- **AND** the uploaded shard SHALL include both refreshed clean rows and preserved dirty rows

#### Scenario: Normal archive lookup validates before entry recording
- **WHEN** the archive pipeline performs deduplication lookup for a content hash before recording a new chunk-index entry for that same content hash
- **THEN** the lookup SHOULD validate the hash's prefix against the current latest snapshot identity before `AddEntry` records the dirty row
- **AND** later dirty-row preservation during flush SHALL remain a defensive protection for direct entry recording, remote changes after lookup, and failed-flush retry state

#### Scenario: One-prefix materialization is allowed
- **WHEN** chunk-index code serializes a loaded prefix for remote upload
- **THEN** it MAY materialize that one prefix into an in-memory `Shard` and serialized byte array
- **AND** the number of concurrently materialized prefixes SHALL be bounded by the configured flush or repair worker count
- **AND** materialized `Shard` instances SHALL NOT be retained as an in-memory cache after upload or ingestion completes

#### Scenario: Prefix operations are serialized by prefix
- **WHEN** two chunk-index operations need to hydrate, ingest, stream, or upload the same prefix
- **THEN** those operations SHALL be serialized through a prefix-scoped gate
- **AND** operations for different prefixes MAY proceed concurrently subject to bounded parallelism

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

#### Scenario: Prefix row streaming does not leak SQLite resources
- **WHEN** chunk-index code streams rows for a prefix from the local store
- **THEN** the local store SHALL own and dispose the SQLite connection and reader used for that stream
- **AND** the local store SHALL NOT return a deferred sequence that lets SQLite reader lifetime escape the local-store boundary

#### Scenario: Local schema version mismatch recreates cache
- **WHEN** the local SQLite metadata schema version is missing or incompatible
- **THEN** the local store MAY recreate the local SQLite cache
- **AND** subsequent lookup SHALL rehydrate needed prefixes from remote chunk-index shards

#### Scenario: SQLite cache file family is moved or deleted together
- **WHEN** chunk-index code moves aside, deletes, or recreates the local SQLite cache
- **THEN** it SHALL first close local-store SQLite connections
- **AND** it SHALL call `SqliteConnection.ClearAllPools()` before moving or deleting SQLite files
- **AND** it SHALL handle `cache.sqlite`, `cache.sqlite-wal`, and `cache.sqlite-shm` as one cache file family
- **AND** best-effort backup files SHALL use a `.bak` suffix

#### Scenario: Single owner for SQLite schema
- **WHEN** chunk-index internals need to read or mutate local chunk-index tables
- **THEN** they SHALL do so through the SQLite-backed local store
- **AND** no separate reader, writer, repair, or shard-cache component SHALL define or independently manage its own SQLite tables for chunk-index state

#### Scenario: Cache invalidation preserves dirty rows
- **WHEN** chunk-index cache invalidation clears local cache state
- **THEN** rows with `dirty = 0` SHALL be discarded
- **AND** loaded-prefix state SHALL be cleared
- **AND** rows with `dirty = 1` SHALL be preserved or the operation SHALL fail clearly without treating the dirty rows as successfully discarded
- **AND** a preserved dirty row SHALL NOT by itself mean that its prefix is fully loaded

#### Scenario: Corrupt local SQLite cache can be rebuilt
- **WHEN** chunk-index startup or lookup detects that the local SQLite cache is corrupt
- **THEN** the implementation MAY move the corrupt database aside or recreate it
- **AND** subsequent lookup SHALL rehydrate needed prefixes from remote chunk-index shards

#### Scenario: Corrupt local SQLite during archive fails clearly
- **WHEN** local SQLite corruption is detected during an active archive or flush
- **THEN** the archive operation SHALL fail clearly
- **AND** it SHALL NOT publish a snapshot for work whose dirty chunk-index rows were not successfully flushed

#### Scenario: Corrupt remote shard remains repair condition
- **WHEN** normal chunk-index lookup downloads a remote shard blob that cannot be deserialized
- **THEN** lookup SHALL fail with a clear chunk-index corruption error
- **AND** the error SHALL instruct the user to run explicit full chunk-index repair

## MODIFIED Requirements

### Requirement: Explicit full chunk-index repair
The system SHALL provide an explicit full chunk-index repair API and command that rebuilds all chunk-index shards from committed chunk blobs using the configured shard prefix length. Full repair SHALL use SQLite-backed local repair state instead of grouping all reconstructed entries in managed memory. Repair SHALL stream one metadata-aware chunk listing from `ListAsync("chunks/", includeMetadata: true, ...)`, reconstruct large and thin chunk-index entries, and upsert each reconstructed entry into disk-backed local repair state as it is discovered.

Full repair SHALL write a repair-in-progress marker before clearing or rebuilding local chunk-index state. The repair marker SHALL live outside the purgeable local chunk-index cache and SHALL NOT be deleted by cache invalidation. Repair SHALL move any existing local SQLite cache aside to a best-effort `.bak` file or otherwise replace it with a fresh local SQLite cache before staging reconstructed entries. Normal chunk-index operations that trust or mutate chunk-index state, including lookup, entry recording, and pending-entry flush, SHALL fail clearly while the marker exists. The explicit repair API SHALL be allowed to run when the marker already exists so an interrupted repair can be rerun.

After reconstructed entries are staged locally, full repair SHALL stream each rebuilt prefix from SQLite ordered by content hash, serialize each remote shard using the existing shard wire format, upload every non-empty rebuilt shard to `chunk-index/<prefix>`, and delete stale remote shard blobs whose prefixes were not rebuilt. Full repair SHALL clear dirty repair state and the repair marker only after rebuilt shard upload and stale-shard deletion succeed. Full repair SHALL NOT publish snapshots.

Full repair remains explicit and idempotent. If full repair is interrupted while staging entries, uploading rebuilt shards, or deleting stale remote shards, a later full repair SHALL purge partial repair state and reconstruct shard contents again from committed chunks. Distributed locking or remote repair leases remain out of scope.

#### Scenario: Full repair stages reconstructed entries on disk
- **WHEN** full chunk-index repair scans committed large and thin chunk blobs
- **THEN** it SHALL reconstruct chunk-index entries for those chunks
- **AND** it SHALL upsert each reconstructed entry into SQLite-backed local repair state
- **AND** it SHALL NOT group all reconstructed entries by shard prefix in managed memory

#### Scenario: Full repair replaces local SQLite cache
- **WHEN** full chunk-index repair starts
- **THEN** it SHALL write the repair-in-progress marker
- **AND** it SHALL move aside or replace the existing local SQLite cache before staging rebuilt entries

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
