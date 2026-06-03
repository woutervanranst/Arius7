## MODIFIED Requirements

### Requirement: Chunk resolution from index
The restore pipeline SHALL look up each content hash through `ChunkIndexService` to determine the chunk hash and chunk type from the resolved chunk metadata. Restore SHALL NOT run automatic chunk-index repair. If chunk-index lookup detects a corrupt remote shard or interrupted local repair state, restore SHALL fail with a clear error that instructs the user to run the explicit chunk-index repair command. For large files, the content hash equals the chunk hash. For tar-bundled files, the content hash maps to a different chunk hash for the parent tar. The system SHALL group file entries by chunk hash to minimize downloads (multiple files from the same tar -> one download).

Restore chunk resolution SHALL use bounded streaming or bounded-batch chunk-index lookup. It SHALL NOT require materializing all distinct content hashes for the restore operation and all returned chunk-index entries in memory at once. SQLite and chunk-index local-store details SHALL remain hidden behind `ChunkIndexService`; restore SHALL depend only on chunk-index service APIs.

If any snapshot-referenced content hash remains unresolved after chunk-index lookup, restore SHALL fail with a clear error that identifies missing chunk-index entries and instructs the user to run the explicit chunk-index repair command.

#### Scenario: Large file chunk resolution
- **WHEN** content hash `abc123` is looked up and the chunk-index entry has content-hash equal to chunk-hash
- **THEN** the system SHALL identify this as a large file chunk for direct streaming restore

#### Scenario: Tar-bundled file chunk resolution
- **WHEN** content hash `def456` is looked up and the chunk-index entry has a different chunk-hash `tar789`
- **THEN** the system SHALL identify this as a tar-bundled file and group it with other files from `tar789`

#### Scenario: Multiple files from same tar
- **WHEN** 5 files all map to the same tar chunk hash
- **THEN** the system SHALL download the tar once and extract all 5 files in a single pass

#### Scenario: Missing shard leaves entries unresolved
- **WHEN** restore looks up a snapshot-referenced content hash and its chunk-index shard blob is missing
- **THEN** restore SHALL treat the shard as empty
- **AND** it SHALL classify the content hash as unresolved

#### Scenario: Unresolved entries fail with repair instruction
- **WHEN** restore completes chunk-index lookup and one or more snapshot-referenced content hashes remain unresolved
- **THEN** restore SHALL fail with a clear error that identifies missing chunk-index entries
- **AND** the error SHALL instruct the user to run the explicit chunk-index repair command

#### Scenario: Corrupt chunk index fails with repair instruction
- **WHEN** restore lookup detects a corrupt remote shard or interrupted local repair state
- **THEN** restore SHALL fail with a clear chunk-index error
- **AND** the error SHALL instruct the user to run the explicit chunk-index repair command

#### Scenario: Restore chunk resolution is bounded
- **WHEN** restore processes a very large snapshot or subtree
- **THEN** it SHALL resolve content hashes through chunk-index streaming or bounded lookup batches
- **AND** it SHALL NOT create one full in-memory list of all distinct content hashes plus one full in-memory dictionary of all resolved chunk-index entries for the entire restore operation

#### Scenario: Restore does not depend on SQLite
- **WHEN** restore performs chunk resolution
- **THEN** it SHALL call `ChunkIndexService` APIs
- **AND** it SHALL NOT reference SQLite, `Microsoft.Data.Sqlite`, or chunk-index local-store internals directly
