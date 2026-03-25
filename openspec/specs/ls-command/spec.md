# Ls Command Spec

## Purpose

Defines the `ls` command for listing and searching files within snapshots, including path prefix filtering, filename search, size lookup from the chunk index, and error handling.

## Requirements

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

### Requirement: Path prefix filter
The system SHALL support filtering by path prefix to list files within a specific directory. The filter SHALL match from the beginning of the relative path. Only tree blobs in the matching subtree need to be downloaded.

#### Scenario: Filter by directory prefix
- **WHEN** `arius ls --prefix photos/2024/` is run
- **THEN** the system SHALL only display files whose path starts with `photos/2024/`

#### Scenario: Efficient subtree traversal
- **WHEN** filtering by `photos/2024/`
- **THEN** the system SHALL only download tree blobs for `/`, `photos/`, `photos/2024/`, and its children — not unrelated directories

### Requirement: Filename substring filter
The system SHALL support filtering by filename substring to search for files across the full snapshot. This requires traversing the entire tree (all directories).

#### Scenario: Search by filename part
- **WHEN** `arius ls --filter vacation` is run
- **THEN** the system SHALL display all files whose filename contains "vacation" (case-insensitive)

#### Scenario: Combined prefix and filter
- **WHEN** `arius ls --prefix photos/ --filter .jpg` is run
- **THEN** the system SHALL display files under `photos/` whose filename contains `.jpg`

### Requirement: Snapshot not found handling
The system SHALL report a clear error when a requested snapshot version does not exist, and list available snapshots.

#### Scenario: Invalid snapshot version
- **WHEN** `arius ls -v nonexistent` is run
- **THEN** the system SHALL report the snapshot was not found and list available snapshot timestamps

### Requirement: Size lookup from chunk index
The system SHALL retrieve file sizes from the chunk index `original-size` field when displaying file listings. If the chunk index lookup fails (e.g., shard not available), the size SHALL be displayed as unknown.

#### Scenario: Size displayed from index
- **WHEN** listing files
- **THEN** each file's size SHALL be retrieved from the chunk index entry's `original-size` field

#### Scenario: Size unavailable
- **WHEN** a content hash is not found in the chunk index during ls
- **THEN** the system SHALL display "?" or "unknown" for the size
