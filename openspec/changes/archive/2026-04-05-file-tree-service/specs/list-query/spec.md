## MODIFIED Requirements

### Requirement: List files in snapshot
The system SHALL list entries in a snapshot by traversing the Merkle tree as a streaming `IAsyncEnumerable<RepositoryEntry>` via Mediator's `IStreamQuery<RepositoryEntry>` / `IStreamQueryHandler`. The stream SHALL emit both directory entries (`RepositoryDirectoryEntry`) and file entries (`RepositoryFileEntry`) as a discriminated union (abstract base record `RepositoryEntry`). Tree blobs SHALL be read through `FileTreeService.ReadAsync` (cache-first with disk write-through), replacing direct `_blobs.DownloadAsync` calls. File entries SHALL include relative path, content hash, file metadata (created date, modified date), and file size from the chunk index. Directory entries SHALL include relative path and tree hash. The default snapshot SHALL be the latest; `-v` SHALL select a specific version.

#### Scenario: List all entries in latest snapshot
- **WHEN** `arius ls` is run without filters or `-v`
- **THEN** the system SHALL traverse the latest snapshot's tree, reading tree blobs via `FileTreeService.ReadAsync`, and stream all entries (files and directories) with path, size, and dates

#### Scenario: List files in specific snapshot
- **WHEN** `arius ls -v 2026-03-21T140000.000Z` is run
- **THEN** the system SHALL stream entries from the specified snapshot

#### Scenario: Parse file entry during ls
- **WHEN** a tree blob line is `abc123... F 2026-03-25T10:00:00.0000000+00:00 2026-03-25T12:30:00.0000000+00:00 vacation.jpg`
- **THEN** the system SHALL yield a `RepositoryFileEntry` with name `vacation.jpg`, hash `abc123...`, and timestamps

#### Scenario: Parse directory entry during ls
- **WHEN** a tree blob line is `def456... D photos`
- **THEN** the system SHALL yield a `RepositoryDirectoryEntry` with name `photos` and tree hash `def456...`

#### Scenario: Cached tree blob reuse during ls
- **WHEN** a tree blob was downloaded during a previous `ls` or `restore` invocation
- **THEN** `FileTreeService.ReadAsync` SHALL return the cached version from disk without contacting Azure

### Requirement: Path prefix filter
The system SHALL support filtering by path prefix to list entries within a specific subdirectory. The filter SHALL navigate to the target subtree by descending through the Merkle tree, reading tree blobs via `FileTreeService.ReadAsync` (only downloading tree blobs on the path to the target prefix on cache miss). Once at the target directory, entries are streamed from that point. Prefix and Recursive are orthogonal: `Prefix` navigates to a starting directory, `Recursive` controls whether to descend further.

#### Scenario: Filter by directory prefix
- **WHEN** `arius ls --prefix photos/2024/` is run
- **THEN** the system SHALL only stream entries rooted at `photos/2024/`

#### Scenario: Efficient subtree traversal
- **WHEN** filtering by `photos/2024/`
- **THEN** the system SHALL only read tree blobs for `/`, `photos/`, `photos/2024/`, and (if recursive) its children — not unrelated directories

#### Scenario: Prefix with non-recursive
- **WHEN** `ListQuery` is executed with `Prefix=photos/2024/` and `Recursive=false`
- **THEN** the system SHALL stream only the immediate children of `photos/2024/`
