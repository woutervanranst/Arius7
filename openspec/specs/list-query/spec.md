# List Query Spec

## Purpose

Defines the `ls` command for listing and searching repository entries within snapshots, including streaming `RepositoryEntry` output, prefix and recursion semantics, optional local filesystem merge behavior, per-directory size lookup, and error handling.

## Requirements

### Requirement: List files in snapshot
The system SHALL list entries in a snapshot by traversing the Merkle tree as a streaming `IAsyncEnumerable<RepositoryEntry>` via Mediator's `IStreamQuery<RepositoryEntry>` / `IStreamQueryHandler`. The stream SHALL emit both directory entries (`RepositoryDirectoryEntry`) and file entries (`RepositoryFileEntry`) as a discriminated union (abstract base record `RepositoryEntry`). Tree blobs SHALL be read through `TreeCacheService.ReadAsync` (cache-first with disk write-through), replacing direct `_blobs.DownloadAsync` calls. File entries SHALL include relative path, content hash, file metadata (created date, modified date), and file size from the chunk index. Directory entries SHALL include relative path and tree hash. The default snapshot SHALL be the latest; `-v` SHALL select a specific version.

#### Scenario: List all entries in latest snapshot
- **WHEN** `arius ls` is run without filters or `-v`
- **THEN** the system SHALL traverse the latest snapshot's tree, reading tree blobs via `TreeCacheService.ReadAsync`, and stream all entries (files and directories) with path, size, and dates

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
- **THEN** `TreeCacheService.ReadAsync` SHALL return the cached version from disk without contacting Azure

### Requirement: Path prefix filter
The system SHALL support filtering by path prefix to list entries within a specific subdirectory. The filter SHALL navigate to the target subtree by descending through the Merkle tree, reading tree blobs via `TreeCacheService.ReadAsync` (only downloading tree blobs on the path to the target prefix on cache miss). Once at the target directory, entries are streamed from that point. Prefix and Recursive are orthogonal: `Prefix` navigates to a starting directory, `Recursive` controls whether to descend further.

#### Scenario: Filter by directory prefix
- **WHEN** `arius ls --prefix photos/2024/` is run
- **THEN** the system SHALL only stream entries rooted at `photos/2024/`

#### Scenario: Efficient subtree traversal
- **WHEN** filtering by `photos/2024/`
- **THEN** the system SHALL only read tree blobs for `/`, `photos/`, `photos/2024/`, and (if recursive) its children — not unrelated directories

#### Scenario: Prefix with non-recursive
- **WHEN** `ListQuery` is executed with `Prefix=photos/2024/` and `Recursive=false`
- **THEN** the system SHALL stream only the immediate children of `photos/2024/`

### Requirement: Filename substring filter
The system SHALL support filtering by filename substring to search for files across the full snapshot. This requires traversing the entire tree (all directories).

#### Scenario: Search by filename part
- **WHEN** `arius ls --filter vacation` is run
- **THEN** the system SHALL stream all files whose filename contains "vacation" (case-insensitive)

#### Scenario: Combined prefix and filter
- **WHEN** `arius ls --prefix photos/ --filter .jpg` is run
- **THEN** the system SHALL stream files under `photos/` whose filename contains `.jpg`

### Requirement: Snapshot not found handling
The system SHALL report a clear error when a requested snapshot version does not exist, and list available snapshots.

#### Scenario: Invalid snapshot version
- **WHEN** `arius ls -v nonexistent` is run
- **THEN** the system SHALL report the snapshot was not found and list available snapshot timestamps

### Requirement: Size lookup from chunk index
The system SHALL retrieve file sizes from the chunk index `original-size` field when streaming file entries. Sizes SHALL be looked up in per-directory batches (all file hashes in a single directory are batched into one `LookupAsync` call). If the chunk index lookup fails (e.g., shard not available), the size SHALL be `null`.

#### Scenario: Size displayed from index
- **WHEN** streaming file entries
- **THEN** each file's size SHALL be retrieved from the chunk index entry's `original-size` field via a per-directory batch lookup

#### Scenario: Size unavailable
- **WHEN** a content hash is not found in the chunk index during ls
- **THEN** the system SHALL set `OriginalSize` to `null` for that entry

### Requirement: Recursive flag
The `ListQuery` SHALL accept a `Recursive` property (default `true`). When `Recursive=true`, the system SHALL perform a full depth-first tree walk, streaming all entries in all subdirectories. When `Recursive=false`, the system SHALL stream only the immediate children of the target directory (one level deep). `Recursive` and `Prefix` are orthogonal: `Prefix` navigates to the starting directory, `Recursive` controls depth.

#### Scenario: Recursive listing (default)
- **WHEN** `ListQuery` is executed with `Recursive=true` (or default)
- **THEN** the system SHALL stream entries from all directories recursively

#### Scenario: Single-directory listing
- **WHEN** `ListQuery` is executed with `Recursive=false`
- **THEN** the system SHALL stream only the immediate children of the root directory

#### Scenario: Single-directory listing with prefix
- **WHEN** `ListQuery` is executed with `Recursive=false` and `Prefix=photos/2024/`
- **THEN** the system SHALL stream only the immediate children of the `photos/2024/` directory

### Requirement: Local filesystem merge
The `ListQuery` SHALL accept an optional `LocalPath` parameter. When provided, the handler SHALL merge Merkle tree (cloud) state with local filesystem state using a two-phase algorithm per directory, modeled after the `LocalFileEnumerator` pattern.

**Phase 1 - Cloud iteration**: Iterate over the Merkle tree entries for the current directory. For each entry, check whether it exists locally (via `File.Exists` / `Directory.Exists`). If the local counterpart exists, yield a combined cloud+local entry. If not, yield a cloud-only entry. Track which names have been yielded.

**Phase 2 - Local iteration**: Iterate over the local filesystem entries for the current directory. For each entry, if it was already yielded in Phase 1 (present in the Merkle tree), skip it. Otherwise, yield a local-only entry.

For files that exist locally, the entry SHALL include `FilePair` information (pointer file presence, binary existence). The merge SHALL handle three directory recursion cases: cloud+local (recurse with both tree hash and local path), cloud-only (recurse with tree hash only, null local path), local-only (recurse with local path only, null tree hash).

#### Scenario: File exists in both cloud and local
- **WHEN** `ListQuery` is executed with `LocalPath` set and a file `photo.jpg` exists in both the Merkle tree and the local directory
- **THEN** the system SHALL yield a single `RepositoryFileEntry` for `photo.jpg` with both cloud metadata (hash, dates, size) and local state (pointer file presence) during Phase 1

#### Scenario: File exists only in cloud
- **WHEN** `ListQuery` is executed with `LocalPath` set and a file `old.jpg` exists only in the Merkle tree
- **THEN** the system SHALL yield a `RepositoryFileEntry` for `old.jpg` with cloud metadata and no local state during Phase 1

#### Scenario: File exists only locally
- **WHEN** `ListQuery` is executed with `LocalPath` set and a file `new.jpg` exists only in the local directory
- **THEN** the system SHALL yield a `RepositoryFileEntry` for `new.jpg` with local state and no cloud metadata during Phase 2

#### Scenario: Directory exists only in cloud
- **WHEN** a subdirectory `archive/` exists in the Merkle tree but not locally
- **THEN** the system SHALL yield a `RepositoryDirectoryEntry` for `archive/` (cloud-only) and recurse with tree hash only

#### Scenario: Directory exists only locally
- **WHEN** a subdirectory `drafts/` exists locally but not in the Merkle tree
- **THEN** the system SHALL yield a `RepositoryDirectoryEntry` for `drafts/` (local-only) and recurse with local path only

#### Scenario: No local path provided
- **WHEN** `ListQuery` is executed without `LocalPath`
- **THEN** the system SHALL list only cloud (Merkle tree) entries, with no local filesystem scanning

### Requirement: Streaming output via Mediator
The `ListQuery` SHALL implement `IStreamQuery<RepositoryEntry>`. The handler SHALL implement `IStreamQueryHandler<ListQuery, RepositoryEntry>` and return `IAsyncEnumerable<RepositoryEntry>`. Consumers SHALL use `mediator.CreateStream(command)` to consume results. The handler SHALL NOT buffer all results before returning; each entry SHALL be yielded as soon as its directory is processed.

#### Scenario: Progressive streaming
- **WHEN** the `ListQuery` is consumed via `mediator.CreateStream(command)`
- **THEN** entries SHALL appear progressively as directories are processed, not after the full tree walk completes

#### Scenario: Cancellation support
- **WHEN** the consumer cancels enumeration (e.g., `CancellationToken` is triggered)
- **THEN** the system SHALL stop traversing and release resources without completing the full tree walk
