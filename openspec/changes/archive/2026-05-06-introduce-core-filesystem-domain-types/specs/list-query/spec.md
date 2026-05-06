## MODIFIED Requirements

### Requirement: Path prefix filter
The system SHALL support filtering by path prefix to list entries within a specific subdirectory. The handler SHALL parse the public prefix string into a validated relative domain path before traversal. The filter SHALL navigate to the target subtree by descending through the Merkle tree, reading tree blobs via `FileTreeService.ReadAsync` (only downloading tree blobs on the path to the target prefix on cache miss). Once at the target directory, entries are streamed from that point. Prefix and Recursive are orthogonal: `Prefix` navigates to a starting directory, `Recursive` controls whether to descend further. Prefix matching and traversal SHALL be segment-aware and SHALL NOT rely on raw string prefix matching.

#### Scenario: Filter by directory prefix
- **WHEN** `arius ls --prefix photos/2024/` is run
- **THEN** the system SHALL only stream entries rooted at `photos/2024/`

#### Scenario: Efficient subtree traversal
- **WHEN** filtering by `photos/2024/`
- **THEN** the system SHALL only read tree blobs for `/`, `photos/`, `photos/2024/`, and (if recursive) its children — not unrelated directories

#### Scenario: Prefix with non-recursive
- **WHEN** `ListQuery` is executed with `Prefix=photos/2024/` and `Recursive=false`
- **THEN** the system SHALL stream only the immediate children of `photos/2024/`

#### Scenario: Prefix does not match partial path segment
- **WHEN** `ListQuery` is executed with `Prefix=photos/`
- **THEN** an entry under `photoshop/` SHALL NOT match the prefix

### Requirement: Local filesystem merge
The `ListQuery` SHALL accept an optional `LocalPath` parameter. When provided, the handler SHALL merge Merkle tree (cloud) state with local filesystem state using a two-phase algorithm per directory, modeled after the archive file-pair enumeration pattern and using relative domain paths internally.

**Phase 1 - Cloud iteration**: Iterate over the Merkle tree entries for the current directory. For each entry, check whether it exists locally through the filesystem domain boundary. If the local counterpart exists, yield a combined cloud+local entry. If not, yield a cloud-only entry. Track which relative path names have been yielded.

**Phase 2 - Local iteration**: Iterate over the local filesystem entries for the current directory through the filesystem domain boundary. For each entry, if it was already yielded in Phase 1 (present in the Merkle tree), skip it. Otherwise, yield a local-only entry.

For files that exist locally, the entry SHALL include FilePair information (pointer file presence, binary existence). The merge SHALL handle three directory recursion cases: cloud+local (recurse with both tree hash and local relative path), cloud-only (recurse with tree hash only, no local path), local-only (recurse with local relative path only, no tree hash).

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
