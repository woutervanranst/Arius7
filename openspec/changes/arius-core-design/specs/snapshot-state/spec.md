## ADDED Requirements

### Requirement: Merkle tree snapshot structure
The system SHALL store snapshots as a content-addressed merkle tree where each directory is an individual encrypted blob and the snapshot manifest points to the root tree hash.

#### Scenario: Snapshot manifest content
- **WHEN** a snapshot is created
- **THEN** the manifest SHALL contain the root tree hash, timestamp, file count, total size, and Arius version, encrypted and stored as `snapshots/<UTC-timestamp>.enc`

#### Scenario: Tree node per directory
- **WHEN** a directory is part of the archive
- **THEN** it SHALL be represented as a single encrypted tree blob stored at `trees/<tree-hash>.enc`

#### Scenario: Tree node content-addressed
- **WHEN** a directory's contents haven't changed between snapshots
- **THEN** its tree hash SHALL be identical and the existing tree blob SHALL be reused (no re-upload)

### Requirement: Tree node format
Each tree node SHALL be a versioned JSON document (before encryption) containing an array of entries with extensible metadata.

#### Scenario: File entry in tree node
- **WHEN** a file is recorded in a tree node
- **THEN** the entry SHALL include: name, type ("file"), content hash, size (bytes), created date (UTC ISO 8601), modified date (UTC ISO 8601)

#### Scenario: Directory entry in tree node
- **WHEN** a subdirectory is recorded in a tree node
- **THEN** the entry SHALL include: name, type ("tree"), and the child tree's hash

#### Scenario: Extensible format
- **WHEN** the tree node format is versioned (field `v`)
- **THEN** unknown fields in future versions SHALL be ignored by older readers, and missing optional fields SHALL have sensible defaults

### Requirement: Chunk index for content-hash to chunk-hash resolution
The system SHALL maintain a chunk index mapping content hashes to tar-chunk hashes for files stored in tar bundles. The index SHALL be sharded by 2-byte content-hash prefix (65,536 shards).

#### Scenario: Chunk index lookup for tar-bundled file
- **WHEN** a file's content hash does not exist as a direct chunk (`HEAD chunks/<hash>` returns 404)
- **THEN** the system SHALL look up `chunk-index/<2-byte-prefix>/index.enc`, decrypt it, and find the tar-chunk-hash containing that file

#### Scenario: Chunk index shard size
- **WHEN** there are 500M small files in the archive
- **THEN** each shard SHALL contain approximately 7,600 entries at ~150-200 KB compressed

#### Scenario: Chunk index update during archive
- **WHEN** new small files are archived into tar bundles
- **THEN** the system SHALL rewrite the affected chunk index shards with the new entries added

### Requirement: Content-hash to chunk resolution strategy
The system SHALL first attempt to resolve a content hash as a direct chunk, then fall back to the chunk index.

#### Scenario: Large file resolved directly
- **WHEN** `HEAD chunks/<content-hash>` returns 200
- **THEN** the system SHALL treat it as a solo large file chunk and download directly

#### Scenario: Small file resolved via chunk index
- **WHEN** `HEAD chunks/<content-hash>` returns 404
- **THEN** the system SHALL consult the chunk index to find the tar-chunk-hash

### Requirement: Snapshot immutability
Snapshots SHALL never be deleted or modified. Each archive run creates a new snapshot. All previous snapshots remain accessible.

#### Scenario: Previous snapshot accessible after new archive
- **WHEN** a new snapshot is created
- **THEN** all previous snapshots SHALL remain in blob storage and be accessible for restore and ls operations

### Requirement: Local tree cache
The system SHALL cache downloaded tree blobs locally. Since tree blobs are content-addressed and immutable, cached blobs SHALL be valid indefinitely.

#### Scenario: Tree blob cached after download
- **WHEN** a tree blob is downloaded for ls or restore
- **THEN** it SHALL be stored in the local cache and reused for subsequent operations without re-downloading

#### Scenario: Cache survives across CLI invocations
- **WHEN** the user runs `arius ls` and then runs it again later
- **THEN** previously cached tree blobs SHALL be available without re-downloading
