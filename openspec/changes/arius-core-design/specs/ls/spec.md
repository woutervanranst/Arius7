## ADDED Requirements

### Requirement: List files in a snapshot
The system SHALL list all files in a snapshot, displaying file name, size, created date, and modified date.

#### Scenario: List all files in latest snapshot
- **WHEN** the user runs `arius ls` without filters
- **THEN** the system SHALL download the latest snapshot manifest, traverse the tree, and display all files with their metadata (name, size, created, modified)

#### Scenario: List files in a specific snapshot
- **WHEN** the user specifies `-v <snapshot-name>`
- **THEN** the system SHALL list files from that exact snapshot

### Requirement: Filter by path/filename prefix
The system SHALL support filtering the file listing by a path or filename prefix.

#### Scenario: Filter by directory prefix
- **WHEN** the user provides a path prefix filter (e.g., `photos/2024/`)
- **THEN** the system SHALL list only files whose relative path starts with that prefix

#### Scenario: Filter matches no files
- **WHEN** the user provides a prefix that matches no files
- **THEN** the system SHALL display an empty result with no error

### Requirement: Filter by path/filename substring
The system SHALL support filtering the file listing by a substring match anywhere in the path or filename.

#### Scenario: Search for filename substring
- **WHEN** the user provides a substring filter (e.g., `vacation`)
- **THEN** the system SHALL list all files whose relative path contains that substring anywhere

#### Scenario: Full-text search on cold cache
- **WHEN** the user performs a substring search and the local tree cache is empty
- **THEN** the system SHALL traverse the entire tree (downloading all tree blobs), building the local cache as it goes, and return matching results. This MAY be slow on first invocation.

### Requirement: Path-based browsing via merkle tree
The system SHALL resolve path-based listings by traversing the merkle tree, downloading only the tree blobs needed for the requested path.

#### Scenario: List a deeply nested directory
- **WHEN** the user lists `photos/2024/vacation/`
- **THEN** the system SHALL download only the tree blobs for root → photos → 2024 → vacation (4 blobs), not the entire tree

#### Scenario: Cached tree blobs reused
- **WHEN** a tree blob has been previously downloaded and cached locally
- **THEN** the system SHALL use the cached version without re-downloading

### Requirement: Snapshot version selection for ls
The system SHALL allow specifying which snapshot to list, defaulting to the latest.

#### Scenario: Default to latest snapshot
- **WHEN** the user runs `arius ls` without `-v`
- **THEN** the system SHALL list from the most recent snapshot

#### Scenario: List from specific snapshot
- **WHEN** the user specifies `-v <snapshot-name>`
- **THEN** the system SHALL list from that exact snapshot

### Requirement: Metadata display
The system SHALL display file metadata including original size, created date, and modified date for each file in the listing.

#### Scenario: File metadata shown
- **WHEN** a file entry is displayed in the listing
- **THEN** the output SHALL include the file's original size (bytes), created date, and modified date as stored in the snapshot tree node
