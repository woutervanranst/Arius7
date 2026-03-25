## MODIFIED Requirements

### Requirement: Index shard merge and upload
The system SHALL collect all new index entries and upload updated chunk index shards once at the end of the archive run. For each modified shard prefix, the system SHALL download the existing shard (if cached or from Azure), merge new entries, and upload.

The L3 wire format (Azure blobs) SHALL be: plaintext lines → gzip compressed → optionally AES-256-CBC encrypted. Content type SHALL be `application/aes256cbc+gzip` (encrypted) or `application/gzip` (plaintext).

The L2 local disk cache SHALL store shards as **plaintext lines only** — no gzip compression, no encryption. This makes the local cache human-readable and avoids unnecessary CPU overhead on every cache read/write. The L2 format SHALL NOT change based on whether a passphrase is provided.

The shard entry format SHALL use a field-count convention to distinguish large and small files:
- **Large file entries** (content-hash equals chunk-hash) SHALL be serialized as 3 space-separated fields: `<content-hash> <original-size> <compressed-size>\n`.
- **Small file entries** (content-hash differs from chunk-hash) SHALL be serialized as 4 space-separated fields: `<content-hash> <chunk-hash> <original-size> <compressed-size>\n`.

On parsing, the system SHALL reconstruct the in-memory entry for 3-field lines by setting the chunk-hash equal to the content-hash. The in-memory data model SHALL remain unchanged (4 properties: content-hash, chunk-hash, original-size, compressed-size).

#### Scenario: Large file entry serialized as 3 fields
- **WHEN** a shard entry has content-hash equal to chunk-hash (large file)
- **THEN** the entry SHALL be serialized as `<content-hash> <original-size> <compressed-size>` (3 space-separated fields)

#### Scenario: Small file entry serialized as 4 fields
- **WHEN** a shard entry has content-hash different from chunk-hash (tar-bundled file)
- **THEN** the entry SHALL be serialized as `<content-hash> <chunk-hash> <original-size> <compressed-size>` (4 space-separated fields)

#### Scenario: Parsing a 3-field entry
- **WHEN** a shard line contains exactly 3 space-separated fields
- **THEN** the system SHALL parse it as a large file entry and set chunk-hash equal to content-hash in the in-memory model

#### Scenario: Parsing a 4-field entry
- **WHEN** a shard line contains exactly 4 space-separated fields
- **THEN** the system SHALL parse it as a small file entry with an explicit chunk-hash

#### Scenario: New entries merged into existing shard
- **WHEN** 50 new files have content hashes with prefix `a1`
- **THEN** the system SHALL download/load the `a1` shard, append 50 entries, and upload the merged shard to Azure in gzip+encrypt format

#### Scenario: First archive (no existing shards)
- **WHEN** archiving to an empty repository
- **THEN** the system SHALL create new shards for each prefix that has entries

#### Scenario: L2 cache stores plaintext
- **WHEN** a shard is saved to the local L2 disk cache
- **THEN** the file SHALL contain raw plaintext lines with no compression or encryption, regardless of whether a passphrase is configured

#### Scenario: L3 upload uses wire format
- **WHEN** a shard is uploaded to Azure
- **THEN** the blob SHALL be gzip-compressed and AES-256-CBC encrypted if a passphrase is provided, or gzip-compressed only if no passphrase is provided

#### Scenario: Stale L2 file is self-healing
- **WHEN** an L2 cache file cannot be parsed as plaintext lines (e.g., it contains old encrypted bytes from a prior version)
- **THEN** the system SHALL treat it as a cache miss, fall through to L3, and re-cache the shard in plaintext format

### Requirement: Merkle tree construction
The system SHALL build a content-addressed merkle tree of directories after all uploads complete. Tree construction SHALL use a two-phase approach: (1) write completed file entries to an unsorted manifest temp file during the pipeline, (2) external sort by path, then stream through building tree blobs bottom-up one directory at a time.

Each tree blob SHALL be a text file with one entry per line, sorted by name (ordinal, case-sensitive). File entries SHALL use the format: `<hash> F <created> <modified> <name>`. Directory entries SHALL use the format: `<hash> D <name>`. Directory names SHALL have a trailing `/`. Timestamps SHALL use ISO-8601 round-trip format (`"O"` format specifier, UTC). Lines SHALL be terminated by `\n`.

File size SHALL NOT be stored in tree blobs (it is in the chunk index). Empty directories SHALL be skipped. Unchanged tree blobs SHALL be deduplicated by content hash.

#### Scenario: Directory with files produces tree blob
- **WHEN** directory `photos/2024/june/` contains files `a.jpg` and `b.jpg`
- **THEN** a tree blob SHALL be created with 2 file entries in text format and uploaded to `filetrees/<tree-hash>`

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

#### Scenario: Unchanged directory across runs
- **WHEN** directory `documents/` has identical files and metadata between two archive runs
- **THEN** the tree blob hash SHALL be identical and the blob SHALL NOT be re-uploaded

#### Scenario: Empty directory skipped
- **WHEN** a directory contains no files (directly or in subdirectories)
- **THEN** no tree blob SHALL be created for that directory

#### Scenario: Tree hash computation
- **WHEN** a tree blob is serialized to text
- **THEN** the tree hash SHALL be SHA256 of the UTF-8 encoded text bytes, optionally passphrase-seeded via `IEncryptionService.ComputeHash`
