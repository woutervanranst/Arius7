## MODIFIED Requirements

### Requirement: Size lookup from chunk index
The system SHALL retrieve file sizes from the chunk index `original-size` field when streaming file entries. Sizes SHALL be looked up through `ChunkIndexService` using bounded batches or streaming lookup so `ls` can preserve progressive streaming output without materializing unbounded content-hash lists or lookup result dictionaries. If a content hash is not found in the chunk index during `ls`, the size SHALL be `null`. If chunk-index lookup detects a corrupt remote shard or interrupted local repair state, `ls` SHALL fail with a clear error that instructs the user to run the explicit chunk-index repair command.

SQLite and chunk-index local-store details SHALL remain hidden behind `ChunkIndexService`; list query code SHALL NOT reference SQLite or chunk-index local-store internals directly.

#### Scenario: Size displayed from index
- **WHEN** streaming file entries
- **THEN** each file's size SHALL be retrieved from the chunk index entry's `original-size` field through `ChunkIndexService`

#### Scenario: Size unavailable
- **WHEN** a content hash is not found in the chunk index during ls
- **THEN** the system SHALL set `OriginalSize` to `null` for that entry

#### Scenario: Corrupt chunk index fails with repair instruction
- **WHEN** chunk-index lookup during `ls` detects a corrupt remote shard or interrupted local repair state
- **THEN** `ls` SHALL fail with a clear chunk-index error
- **AND** the error SHALL instruct the user to run the explicit chunk-index repair command

#### Scenario: List size lookup is bounded
- **WHEN** `ls` streams entries from a large directory or recursive subtree
- **THEN** it SHALL perform chunk-index size lookup in bounded batches or as a stream
- **AND** it SHALL NOT require all visible file content hashes or all size lookup results for the full listing to be held in memory at once

#### Scenario: List does not depend on SQLite
- **WHEN** list query code performs size lookup
- **THEN** it SHALL call `ChunkIndexService` APIs
- **AND** it SHALL NOT reference SQLite, `Microsoft.Data.Sqlite`, or chunk-index local-store internals directly
