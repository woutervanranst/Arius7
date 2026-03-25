## MODIFIED Requirements

### Requirement: Chunk resolution from index
The system SHALL look up each content hash in the chunk index to determine the chunk hash and chunk type. For large files, the content-hash equals the chunk-hash (reconstructed on parse from 3-field entries). For tar-bundled files, the content-hash maps to a different chunk-hash (the tar, from 4-field entries). The system SHALL group all file entries by chunk hash to minimize downloads (multiple files from the same tar → one download).

#### Scenario: Large file chunk resolution
- **WHEN** content hash `abc123` is looked up and the in-memory entry has content-hash equal to chunk-hash
- **THEN** the system SHALL identify this as a large file chunk for direct streaming restore

#### Scenario: Tar-bundled file chunk resolution
- **WHEN** content hash `def456` is looked up and the in-memory entry has a different chunk-hash `tar789`
- **THEN** the system SHALL identify this as a tar-bundled file and group it with other files from `tar789`

#### Scenario: Multiple files from same tar
- **WHEN** 5 files all map to the same tar chunk hash
- **THEN** the system SHALL download the tar once and extract all 5 files in a single pass

### Requirement: Tree traversal to target path
The system SHALL walk the merkle tree from the root to the requested path, downloading tree blobs on demand through the tiered cache (L1 memory → L2 disk → L3 Azure). Tree blobs SHALL be parsed as text format: each line is either `<hash> F <created> <modified> <name>` (file entry) or `<hash> D <name>` (directory entry). For `F` lines, the system SHALL split on the first 4 spaces to extract hash, type, created, modified, and name (remainder). For `D` lines, the system SHALL split on the first 2 spaces to extract hash, type, and name (remainder). For a file restore, traversal stops at the file's directory. For a directory restore, the system SHALL enumerate the full subtree. For a full restore, the entire tree SHALL be traversed.

#### Scenario: Restore single file
- **WHEN** restoring `photos/2024/june/vacation.jpg`
- **THEN** the system SHALL download tree blobs for `/`, `photos/`, `photos/2024/`, `photos/2024/june/` and locate the file entry

#### Scenario: Restore directory
- **WHEN** restoring `photos/2024/`
- **THEN** the system SHALL traverse the full subtree under `photos/2024/` and collect all file entries

#### Scenario: Restore full snapshot
- **WHEN** restoring with `--full`
- **THEN** the system SHALL traverse the entire tree and collect all file entries

#### Scenario: Parse file entry from tree blob
- **WHEN** a tree blob line is `abc123... F 2026-03-25T10:00:00.0000000+00:00 2026-03-25T12:30:00.0000000+00:00 my vacation photo.jpg`
- **THEN** the system SHALL parse hash as `abc123...`, type as file, created and modified as the ISO-8601 timestamps, and name as `my vacation photo.jpg`

#### Scenario: Parse directory entry from tree blob
- **WHEN** a tree blob line is `def456... D 2024 trip/`
- **THEN** the system SHALL parse hash as `def456...`, type as directory, and name as `2024 trip/`
