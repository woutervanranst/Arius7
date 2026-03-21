## ADDED Requirements

### Requirement: Archive a local directory tree
The system SHALL archive a local directory tree (the "local root") and all its contents (subfolders and files) to blob storage, creating a new snapshot.

#### Scenario: Archive a directory with files and subdirectories
- **WHEN** the user runs `arius archive` pointing to a local directory
- **THEN** the system SHALL recursively enumerate all files under the local root, hash their content, upload new content as encrypted chunks, and create a new snapshot capturing the current state of the directory tree

#### Scenario: Archive with configurable storage tier
- **WHEN** the user specifies `--tier <Hot|Cool|Cold|Archive>` (default: Archive)
- **THEN** all newly uploaded chunks SHALL be stored at the specified tier

### Requirement: File-level deduplication via content hashing
The system SHALL compute a passphrase-seeded SHA-256 hash of each file's content to determine identity. Files with identical content SHALL share the same chunk in blob storage.

#### Scenario: File already archived (content hash exists as chunk)
- **WHEN** a file is hashed and a chunk with that content hash already exists in blob storage
- **THEN** the system SHALL skip uploading and record the file's path and hash in the snapshot

#### Scenario: Duplicate file at different path
- **WHEN** two files at different paths have identical content
- **THEN** only one chunk SHALL be uploaded and both paths SHALL reference the same content hash in the snapshot

#### Scenario: File content changed since last archive
- **WHEN** a file has been modified since the last archive (content hash differs from pointer file)
- **THEN** the system SHALL upload the new content as a new chunk and update the pointer file

### Requirement: Pointer file management
The system SHALL maintain pointer files (`<filename>.pointer.arius`) alongside local files containing the hex content hash. Pointer files are a local performance cache and SHALL be rebuildable by re-hashing local files.

#### Scenario: Pointer file matches binary hash
- **WHEN** a file is hashed and the pointer file contains the same hash
- **THEN** the system SHALL skip re-uploading and use the pointer file hash directly

#### Scenario: Pointer file is missing
- **WHEN** a file has no corresponding pointer file
- **THEN** the system SHALL hash the file, check if the chunk exists remotely, upload if needed, and create the pointer file

#### Scenario: Pointer file is out of sync with binary
- **WHEN** a file's pointer file contains a hash that doesn't match the file's current content
- **THEN** the system SHALL re-hash the file, upload the new content if needed, and update the pointer file

#### Scenario: All pointer files are gone
- **WHEN** no pointer files exist on the local filesystem
- **THEN** the system SHALL hash every file, check remote chunk existence via HEAD requests, upload as needed, and recreate all pointer files

#### Scenario: File renamed with pointer file
- **WHEN** a file and its pointer file are renamed together (content unchanged)
- **THEN** the system SHALL record the new path in the snapshot without re-uploading the chunk

#### Scenario: File duplicated with pointer file
- **WHEN** a file is duplicated to a new path (with its pointer file)
- **THEN** the system SHALL record both paths referencing the same content hash in the snapshot

### Requirement: Large file upload
The system SHALL upload files at or above the small file threshold (default: 1 MB, configurable) as individual chunks: gzip-compressed then encrypted.

#### Scenario: Large file upload
- **WHEN** a file is >= the small file threshold and its content hash doesn't exist in blob storage
- **THEN** the system SHALL gzip compress the file, encrypt it with AES-256-CBC, and upload it as `chunks/<content-hash>` with content type `application/aes256cbc+gzip`

### Requirement: Small file tar bundling
The system SHALL bundle files below the small file threshold into tar archives up to the tar target size (default: 64 MB, configurable), then gzip-compress and encrypt the tar.

#### Scenario: Small files bundled into tar
- **WHEN** files below the small file threshold are queued for upload
- **THEN** the system SHALL add them to a tar buffer and seal+upload the tar when it reaches the target size, storing it as `chunks/<tar-hash>` with content type `application/aes256cbc+tar+gzip`

#### Scenario: Tar sealed at target size
- **WHEN** the cumulative size of files in the current tar buffer reaches the tar target size
- **THEN** the system SHALL seal the tar, gzip compress, encrypt, upload, and record each file's content-hash → tar-chunk-hash mapping in the chunk index

#### Scenario: Remaining small files at end of archive
- **WHEN** the archive completes and the tar buffer contains files but hasn't reached the target size
- **THEN** the system SHALL seal and upload the partial tar

### Requirement: Snapshot creation
The system SHALL create a new encrypted snapshot manifest after all files are processed, capturing the complete state of the local root at that point in time.

#### Scenario: Snapshot created after successful archive
- **WHEN** all files have been hashed, uploaded, and pointer files written
- **THEN** the system SHALL build a merkle tree from all file entries, upload new tree blobs, and upload a snapshot manifest containing the root tree hash and metadata (timestamp, file count, total size, Arius version)

#### Scenario: Snapshot naming
- **WHEN** a snapshot is created
- **THEN** it SHALL be named with UTC timestamp and sub-second precision (e.g., `2026-03-21T140000.000Z`), ensuring uniqueness and sortability

### Requirement: Remove local files after archive
The system SHALL support a `--remove-local` option that deletes local binary files after successful archive, keeping only pointer files.

#### Scenario: Remove local with --remove-local
- **WHEN** the user specifies `--remove-local` and archive completes successfully
- **THEN** the system SHALL delete the original binary files, leaving only the `.pointer.arius` files in place

#### Scenario: Remove local only after successful upload
- **WHEN** `--remove-local` is specified but a file's chunk upload fails
- **THEN** the system SHALL NOT delete the local binary for that file

### Requirement: Graceful filesystem enumeration
The system SHALL gracefully handle files and directories that cannot be read during enumeration.

#### Scenario: Permission denied on file
- **WHEN** a file cannot be read due to permission restrictions
- **THEN** the system SHALL log a warning with the file path and reason, skip that file, and continue processing other files

#### Scenario: Permission denied on directory
- **WHEN** a directory cannot be enumerated due to permission restrictions
- **THEN** the system SHALL log a warning with the directory path and reason, skip that directory and all its contents, and continue processing other directories

### Requirement: Relative path storage
The system SHALL store file paths relative to the local root in all snapshots and tree nodes. No absolute paths or drive letters SHALL appear in stored state.

#### Scenario: File path stored as relative
- **WHEN** a file at `/archive/photos/2024/vacation/beach.jpg` is archived with local root `/archive`
- **THEN** its path in the snapshot tree SHALL be `photos/2024/vacation/beach.jpg`

#### Scenario: Cross-platform path normalization
- **WHEN** archiving on Windows with paths using `\` separators
- **THEN** all stored paths SHALL use `/` as the separator
