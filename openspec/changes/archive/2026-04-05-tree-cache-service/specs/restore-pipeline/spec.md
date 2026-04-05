## MODIFIED Requirements

### Requirement: Tree traversal to target path
The system SHALL walk the merkle tree from the root to the requested path, reading tree blobs through `TreeCacheService.ReadAsync` (cache-first with disk write-through), replacing direct `_blobs.DownloadAsync` calls. Tree blobs SHALL be parsed as text format: each line is either `<hash> F <created> <modified> <name>` (file entry) or `<hash> D <name>` (directory entry). For `F` lines, the system SHALL split on the first 4 spaces to extract hash, type, created, modified, and name (remainder). For `D` lines, the system SHALL split on the first 2 spaces to extract hash, type, and name (remainder). For a file restore, traversal stops at the file's directory. For a directory restore, the system SHALL enumerate the full subtree. For a full restore, the entire tree SHALL be traversed.

#### Scenario: Restore single file
- **WHEN** restoring `photos/2024/june/vacation.jpg`
- **THEN** the system SHALL read tree blobs for `/`, `photos/`, `photos/2024/`, `photos/2024/june/` via `TreeCacheService.ReadAsync` and locate the file entry

#### Scenario: Restore directory
- **WHEN** restoring `photos/2024/`
- **THEN** the system SHALL traverse the full subtree under `photos/2024/` reading tree blobs via `TreeCacheService.ReadAsync` and collect all file entries

#### Scenario: Restore full snapshot
- **WHEN** restoring with `--full`
- **THEN** the system SHALL traverse the entire tree reading all tree blobs via `TreeCacheService.ReadAsync` and collect all file entries

#### Scenario: Parse file entry from tree blob
- **WHEN** a tree blob line is `abc123... F 2026-03-25T10:00:00.0000000+00:00 2026-03-25T12:30:00.0000000+00:00 my vacation photo.jpg`
- **THEN** the system SHALL parse hash as `abc123...`, type as file, created and modified as the ISO-8601 timestamps, and name as `my vacation photo.jpg`

#### Scenario: Parse directory entry from tree blob
- **WHEN** a tree blob line is `def456... D 2024 trip/`
- **THEN** the system SHALL parse hash as `def456...`, type as directory, and name as `2024 trip/`

#### Scenario: Cached tree blob reuse during restore
- **WHEN** a tree blob was downloaded during a previous `ls` or `restore` invocation
- **THEN** `TreeCacheService.ReadAsync` SHALL return the cached version from disk without contacting Azure
