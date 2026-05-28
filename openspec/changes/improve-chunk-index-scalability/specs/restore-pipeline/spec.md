## MODIFIED Requirements

### Requirement: Chunk resolution from index
The restore pipeline SHALL look up each content hash through `ChunkIndexService` to determine the chunk hash and chunk type from the resolved chunk metadata. Restore SHALL use lookup repair behavior that probes and repairs missing shard blobs. For large files, the content hash equals the chunk hash. For tar-bundled files, the content hash maps to a different chunk hash for the parent tar. The system SHALL group all file entries by chunk hash to minimize downloads (multiple files from the same tar → one download).

If any snapshot-referenced content hash remains unresolved after the initial chunk-index lookup, restore SHALL run full chunk-index repair once, retry unresolved lookups, and only fail if entries remain unresolved after full repair.

#### Scenario: Large file chunk resolution
- **WHEN** content hash `abc123` is looked up and the in-memory entry has content-hash equal to chunk-hash
- **THEN** the system SHALL identify this as a large file chunk for direct streaming restore

#### Scenario: Tar-bundled file chunk resolution
- **WHEN** content hash `def456` is looked up and the in-memory entry has a different chunk-hash `tar789`
- **THEN** the system SHALL identify this as a tar-bundled file and group it with other files from `tar789`

#### Scenario: Multiple files from same tar
- **WHEN** 5 files all map to the same tar chunk hash
- **THEN** the system SHALL download the tar once and extract all 5 files in a single pass

#### Scenario: Missing shard repaired during restore lookup
- **WHEN** restore looks up a snapshot-referenced content hash and its chunk-index shard blob is missing
- **AND** probing `chunks/<prefix>*` finds chunk data for that prefix
- **THEN** restore SHALL rebuild that prefix through `ChunkIndexService`
- **AND** it SHALL retry the lookup before classifying the content hash as unresolved

#### Scenario: Unresolved entries trigger full repair once
- **WHEN** restore completes initial chunk-index lookup and one or more snapshot-referenced content hashes remain unresolved
- **THEN** restore SHALL run full chunk-index repair once
- **AND** it SHALL retry lookup for the unresolved content hashes

#### Scenario: Restore fails only after repair retry misses
- **WHEN** restore has run full chunk-index repair and retried unresolved content hash lookups
- **AND** one or more content hashes are still unresolved
- **THEN** restore SHALL fail with a clear error that identifies missing chunk-index entries
