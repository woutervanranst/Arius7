## ADDED Requirements

### Requirement: Disk-backed local chunk-index state ownership
The chunk-index implementation SHALL store hydrated shard rows, loaded-prefix state, pending archive entries, and repair staging state in one SQLite-backed local store owned by `Shared/ChunkIndex` internals. The local store SHALL be the only component that owns SQLite schema, SQLite connections, and transactions for chunk-index local state.

Archive write-session or writer components SHALL NOT own an independent pending-entry collection after pending entries move to SQLite. A writer component MAY remain as an internal orchestration or policy boundary for entry-recording and flush behavior, but it SHALL delegate pending-entry persistence, pending-prefix queries, pending-to-base application, and pending cleanup to the local store.

The local store SHALL NOT upload or download remote shard blobs and SHALL NOT own archive-tail publication policy. Remote shard download/upload, shard wire serialization, flush-in-progress rejection, and partial-flush behavior SHALL remain coordinated outside the local store through `ChunkIndexService` and focused chunk-index internals.

#### Scenario: Local store owns pending state
- **WHEN** archive workers record new chunk-index entries during an archive run
- **THEN** the entries SHALL be persisted to the SQLite-backed local store as pending entries
- **AND** no write-session component SHALL keep an independent content-hash-keyed pending-entry collection in managed memory

#### Scenario: Writer coordinates without owning state
- **WHEN** chunk-index flush starts for pending archive entries
- **THEN** an internal writer or `ChunkIndexService` SHALL enforce flush-in-progress and concurrent-flush policy
- **AND** it SHALL ask the local store for touched pending prefixes and pending rows
- **AND** it SHALL NOT bypass the local store's transactions for pending-entry state changes

#### Scenario: Local store does not own remote publication
- **WHEN** a touched prefix must be flushed to the remote chunk index
- **THEN** the local store SHALL provide disk-backed rows for that prefix
- **AND** a separate chunk-index component SHALL serialize and upload the remote shard blob
- **AND** the local store SHALL NOT treat a SQLite transaction as an atomic transaction with remote blob upload

#### Scenario: Single owner for SQLite schema
- **WHEN** chunk-index internals need to read or mutate local chunk-index tables
- **THEN** they SHALL do so through the SQLite-backed local store
- **AND** no separate reader, writer, repair, or shard-cache component SHALL define or independently manage its own SQLite tables for chunk-index state
