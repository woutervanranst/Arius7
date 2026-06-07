## MODIFIED Requirements

### Requirement: Size lookup from chunk index
The system SHALL retrieve file sizes from the chunk index `original-size` field when streaming file entries. Sizes SHALL be looked up through `ChunkIndexService` one directory at a time, so `ls` preserves progressive directory-by-directory streaming output and never materializes all content hashes for the full recursive listing at once. If a content hash is not found in the chunk index during `ls`, the size SHALL be `null`. If chunk-index lookup detects a corrupt remote shard or interrupted local repair state, `ls` SHALL fail with a clear error that instructs the user to run the explicit chunk-index repair command.

SQLite and chunk-index local-store details SHALL remain hidden behind `ChunkIndexService`; list query code SHALL NOT reference SQLite, `Microsoft.Data.Sqlite`, or chunk-index local-store internals directly.

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

#### Scenario: List size lookup is per directory
- **WHEN** `ls` streams entries from a large recursive subtree
- **THEN** it SHALL perform chunk-index size lookup one directory at a time rather than once for the whole listing
- **AND** it SHALL NOT hold all visible file content hashes or all size lookup results for the full recursive listing in memory at once

#### Scenario: List does not depend on SQLite
- **WHEN** list query code performs size lookup
- **THEN** it SHALL call `ChunkIndexService` APIs
- **AND** it SHALL NOT reference SQLite, `Microsoft.Data.Sqlite`, or chunk-index local-store internals directly
