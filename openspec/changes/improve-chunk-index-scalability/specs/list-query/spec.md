## MODIFIED Requirements

### Requirement: Size lookup from chunk index
The system SHALL retrieve file sizes from the chunk index `original-size` field when streaming file entries. Sizes SHALL be looked up in per-directory batches (all file hashes in a single directory are batched into one `LookupAsync` call). If a content hash is not found in the chunk index during `ls`, the size SHALL be `null`. If chunk-index lookup detects a corrupt remote shard or interrupted local repair state, `ls` SHALL fail with a clear error that instructs the user to run the explicit chunk-index repair command.

#### Scenario: Size displayed from index
- **WHEN** streaming file entries
- **THEN** each file's size SHALL be retrieved from the chunk index entry's `original-size` field via a per-directory batch lookup

#### Scenario: Size unavailable
- **WHEN** a content hash is not found in the chunk index during ls
- **THEN** the system SHALL set `OriginalSize` to `null` for that entry

#### Scenario: Corrupt chunk index fails with repair instruction
- **WHEN** chunk-index lookup during `ls` detects a corrupt remote shard or interrupted local repair state
- **THEN** `ls` SHALL fail with a clear chunk-index error
- **AND** the error SHALL instruct the user to run the explicit chunk-index repair command
