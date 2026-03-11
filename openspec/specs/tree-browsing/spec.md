# tree-browsing

## Purpose
Defines listing files within snapshots, searching across snapshots, streaming tree walks, displaying raw internal objects, and computing repository statistics.

## Requirements

### Requirement: List files in snapshot
The system SHALL list files and directories within a snapshot, similar to `ls -la`, showing name, type, size, modification time, and permissions.

#### Scenario: List root directory
- **WHEN** user runs `ls <snapshot-id>`
- **THEN** the system displays all entries in the snapshot's root tree

#### Scenario: List subdirectory
- **WHEN** user runs `ls <snapshot-id> /documents/work/`
- **THEN** the system displays entries under the specified path

#### Scenario: Recursive listing
- **WHEN** user runs `ls --recursive <snapshot-id>`
- **THEN** the system displays all entries in the snapshot recursively

### Requirement: Tree browsing without rehydration
Listing files SHALL NOT require rehydration of archive-tier data packs. Tree blobs are stored in cold tier and are immediately accessible.

#### Scenario: Instant ls
- **WHEN** user runs `ls <snapshot-id>`
- **THEN** the operation completes without any archive-tier rehydration

### Requirement: Find files across snapshots
The system SHALL search for files matching a pattern across all or specified snapshots.

#### Scenario: Find by name
- **WHEN** user runs `find --pattern "*.pdf"`
- **THEN** the system searches all snapshots for files matching `*.pdf` and shows the snapshot, path, and metadata for each match

#### Scenario: Find in specific snapshot
- **WHEN** user runs `find --pattern "report.xlsx" --snapshot <id>`
- **THEN** only the specified snapshot is searched

#### Scenario: Find by path
- **WHEN** user runs `find --path "/documents/taxes/"`
- **THEN** all snapshots containing entries under that path are listed

### Requirement: Streaming tree walk
The `ls` and `find` operations SHALL produce results as `IAsyncEnumerable<TreeEntry>` and `IAsyncEnumerable<SearchResult>` for streaming to CLI and web UI.

#### Scenario: Streamed ls
- **WHEN** a directory with 100,000 entries is listed
- **THEN** entries are yielded one at a time via `IAsyncEnumerable`, enabling progressive rendering

### Requirement: Cat internal objects
The system SHALL support displaying raw internal objects: config, snapshot, index, tree, key metadata.

#### Scenario: Cat snapshot
- **WHEN** user runs `cat snapshot <id>`
- **THEN** the decrypted snapshot JSON is printed

#### Scenario: Cat tree
- **WHEN** user runs `cat tree <hash>`
- **THEN** the decrypted tree blob JSON is printed

### Requirement: Repository statistics
The system SHALL compute and display repository statistics: total size, number of snapshots, number of packs, number of unique blobs, deduplication ratio.

#### Scenario: Show stats
- **WHEN** user runs `stats`
- **THEN** the system displays total data size, unique data size, dedup ratio, snapshot count, pack count, and blob count
