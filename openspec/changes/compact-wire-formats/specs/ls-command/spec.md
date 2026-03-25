## MODIFIED Requirements

### Requirement: List files in snapshot
The system SHALL list all files in a snapshot by traversing the merkle tree. Tree blobs SHALL be parsed as text format: each line is either `<hash> F <created> <modified> <name>` (file entry) or `<hash> D <name>` (directory entry). The output SHALL include relative path, content hash, and file metadata (created date, modified date). File size SHALL be retrieved from the chunk index. The default snapshot SHALL be the latest; `-v` SHALL select a specific version.

#### Scenario: List all files in latest snapshot
- **WHEN** `arius ls` is run without filters or `-v`
- **THEN** the system SHALL traverse the latest snapshot's tree, parsing text-format tree blobs, and display all files with path, size, and dates

#### Scenario: List files in specific snapshot
- **WHEN** `arius ls -v 2026-03-21T140000.000Z` is run
- **THEN** the system SHALL display files from the specified snapshot

#### Scenario: Parse file entry during ls
- **WHEN** a tree blob line is `abc123... F 2026-03-25T10:00:00.0000000+00:00 2026-03-25T12:30:00.0000000+00:00 vacation.jpg`
- **THEN** the system SHALL extract name `vacation.jpg`, hash `abc123...`, and timestamps for display
