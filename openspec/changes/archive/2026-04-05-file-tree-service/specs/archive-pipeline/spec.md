## MODIFIED Requirements

### Requirement: Merkle tree construction
The system SHALL build a content-addressed merkle tree of directories after all uploads complete. Tree construction SHALL use a two-phase approach: (1) write completed file entries to an unsorted manifest temp file during the pipeline, (2) external sort by path, then stream through building tree blobs bottom-up one directory at a time.

Each tree blob SHALL be a text file with one entry per line, sorted by name (ordinal, case-sensitive). File entries SHALL use the format: `<hash> F <created> <modified> <name>`. Directory entries SHALL use the format: `<hash> D <name>`. Directory names SHALL have a trailing `/`. Timestamps SHALL use ISO-8601 round-trip format (`"O"` format specifier, UTC). Lines SHALL be terminated by `\n`.

File size SHALL NOT be stored in tree blobs (it is in the chunk index). Empty directories SHALL be skipped. Unchanged tree blobs SHALL be deduplicated by content hash.

Tree blob uploads and existence checks SHALL use `FileTreeService` instead of ad-hoc disk caching. Specifically:
- `FileTreeBuilder.EnsureUploadedAsync` SHALL use `FileTreeService.ExistsInRemote(hash)` to check whether a tree blob already exists in remote storage, replacing the current `File.Exists` + `GetMetadataAsync` (HEAD) pattern.
- When a new tree blob needs uploading, `FileTreeBuilder` SHALL use `FileTreeService.WriteAsync(hash, treeBlob)` for write-through to both Azure and the disk cache, replacing the current inline `_blobs.UploadAsync` + `File.WriteAllBytes` pattern.
- `FileTreeService.ValidateAsync` SHALL be called at the start of the archive pipeline (before flush or tree build) to determine the fast/slow path for existence checks.

#### Scenario: Directory with files produces tree blob
- **WHEN** directory `photos/2024/june/` contains files `a.jpg` and `b.jpg`
- **THEN** a tree blob SHALL be created with 2 file entries in text format and uploaded via `FileTreeService.WriteAsync` to `filetrees/<tree-hash>`

#### Scenario: File entry format
- **WHEN** a file `vacation.jpg` with hash `abc123...` created `2026-03-25T10:00:00.0000000+00:00` and modified `2026-03-25T12:30:00.0000000+00:00` is included in a tree blob
- **THEN** the entry SHALL be serialized as `abc123... F 2026-03-25T10:00:00.0000000+00:00 2026-03-25T12:30:00.0000000+00:00 vacation.jpg`

#### Scenario: Directory entry format
- **WHEN** a subdirectory `2024 trip/` with tree-hash `def456...` is included in a tree blob
- **THEN** the entry SHALL be serialized as `def456... D 2024 trip/`

#### Scenario: Entries sorted by name
- **WHEN** a directory contains entries `b.jpg`, `a.jpg`, and `subdir/`
- **THEN** the tree blob SHALL list entries sorted by name using ordinal case-sensitive comparison

#### Scenario: Filename with spaces
- **WHEN** a file is named `my vacation photo.jpg`
- **THEN** the name SHALL appear as-is after the last fixed field (no quoting or escaping required)

#### Scenario: Unchanged directory across runs (fast path)
- **WHEN** directory `documents/` has identical files and metadata between two archive runs on the same machine
- **THEN** `FileTreeService.ExistsInRemote` SHALL return `true` (disk cache hit on fast path) and the blob SHALL NOT be re-uploaded

#### Scenario: Unchanged directory across runs (slow path)
- **WHEN** directory `documents/` has identical files between two archive runs but on a different machine
- **THEN** `FileTreeService.ExistsInRemote` SHALL return `true` (empty marker file on disk from slow-path prefetch) and the blob SHALL NOT be re-uploaded

#### Scenario: Empty directory skipped
- **WHEN** a directory contains no files (directly or in subdirectories)
- **THEN** no tree blob SHALL be created for that directory

#### Scenario: Tree hash computation
- **WHEN** a tree blob is serialized to text
- **THEN** the tree hash SHALL be SHA256 of the UTF-8 encoded text bytes, optionally passphrase-seeded via `IEncryptionService.ComputeHash`

### Requirement: Snapshot creation
The system SHALL create a snapshot manifest after tree construction completes. The snapshot SHALL be stored at `snapshots/<UTC-timestamp>` (e.g., `2026-03-22T150000.000Z`) and SHALL contain the root tree hash, timestamp, file count, total size, and Arius version. Snapshots SHALL NEVER be deleted. `SnapshotService.CreateAsync` is responsible for both uploading the snapshot manifest to Azure and writing the full JSON manifest to `~/.arius/{repo}/snapshots/<timestamp>` on disk (write-through), enabling fast-path cache validation on subsequent runs.

#### Scenario: Successful snapshot creation
- **WHEN** the archive pipeline completes successfully
- **THEN** a snapshot manifest SHALL be uploaded to `snapshots/<timestamp>` with the root tree hash and metadata

#### Scenario: Snapshot reflects current state
- **WHEN** files have been added, modified, and deleted since the last archive
- **THEN** the new snapshot SHALL only contain files that exist at archive time (no deleted files)

#### Scenario: Snapshot JSON written to disk after archive
- **WHEN** the archive pipeline creates snapshot `2026-03-22T150000.000Z`
- **THEN** `SnapshotService.CreateAsync` SHALL write the full JSON manifest to `~/.arius/{repo}/snapshots/2026-03-22T150000.000Z`

#### Scenario: Cache validation runs before end-of-pipeline
- **WHEN** the archive pipeline begins the end-of-pipeline phase
- **THEN** `FileTreeService.ValidateAsync` SHALL have been called before any tree existence checks or chunk-index flush operations
