## MODIFIED Requirements

### Requirement: Multi-phase restore
The system SHALL restore files from a snapshot through three overlapping phases: plan (identify needed packs and collect unique pack IDs), rehydrate (initiate and poll archive-tier rehydration), and restore (download packs in parallel, extract blobs to a temporary directory on disk, assemble output files in parallel, clean up temp directory). Pack data SHALL be staged to a temporary filesystem directory during restore rather than held in memory.

#### Scenario: Full restore
- **WHEN** user runs `restore <snapshot-id> --target /restore/path`
- **THEN** the system plans the restore, shows a cost estimate, requests confirmation, rehydrates packs, downloads and extracts packs to a temp directory, assembles files in parallel, and cleans up the temp directory

#### Scenario: Temp directory lifecycle
- **WHEN** a restore operation begins
- **THEN** the system SHALL create a temp directory (default: `{system-temp}/arius-restore-{guid}`, overridable via `TempPath`)
- **AND** extracted blobs SHALL be written as `{tempDir}/{blobHash}.bin`
- **AND** the temp directory SHALL be deleted in a `finally` block after restore completes or fails

### Requirement: In-memory pack streaming
Rehydrated packs SHALL be downloaded directly from Azure into memory, decrypted, decompressed (gunzip), and extracted (tar) to recover individual blobs. Extracted blobs SHALL be written to the temporary directory on disk as individual files. Pack data in memory SHALL be released after extraction to bound memory usage to O(concurrent_downloaders × pack_size).

#### Scenario: Pack extraction pipeline
- **WHEN** a rehydrated pack is downloaded from Azure
- **THEN** the system streams it into memory, decrypts (AES-256-CBC), decompresses (gzip), extracts (TAR) to recover individual blob bytes, writes each blob to `{tempDir}/{blobHash}.bin`, and releases the pack data from memory

#### Scenario: Large pack memory handling
- **WHEN** multiple packs are downloaded concurrently
- **THEN** memory usage SHALL be bounded to `MaxDownloaders × ~29MB` (one pack's decrypt/decompress peak per worker)
- **AND** previously extracted pack data SHALL not be retained in memory

## ADDED Requirements

### Requirement: Parallel pack fetching
The restore system SHALL download, decrypt, and extract packs concurrently using `MaxDownloaders` parallel workers (default: 4). Each worker SHALL download one pack, extract its blobs to the temp directory, and release the pack from memory before processing the next pack.

#### Scenario: Multiple packs downloaded concurrently
- **WHEN** a restore requires 20 packs and `MaxDownloaders = 4`
- **THEN** up to 4 packs SHALL be downloaded and extracted concurrently
- **AND** the `RestorePackFetched` event SHALL be emitted after each pack is extracted

### Requirement: Parallel file assembly
After all packs have been fetched and extracted to the temp directory, the restore system SHALL assemble output files concurrently using `MaxAssemblers` parallel workers (default: `Environment.ProcessorCount`). Each worker SHALL read chunk blobs from the temp directory, verify HMAC integrity, and write the output file.

#### Scenario: Multiple files assembled concurrently
- **WHEN** all packs have been extracted and `MaxAssemblers = 8`
- **THEN** up to 8 output files SHALL be assembled concurrently from temp directory blobs
- **AND** each file's chunks SHALL be read sequentially in order and verified via HMAC-SHA256

### Requirement: Restore error collection
Individual file restoration failures SHALL NOT cancel the overall operation. The system SHALL emit a `RestoreFileError` event for each failed file and continue with remaining files. The `RestoreCompleted` event SHALL include a `Failed` count.

#### Scenario: One file fails during restore
- **WHEN** a chunk blob is missing from the temp directory for one file
- **THEN** a `RestoreFileError` event SHALL be emitted for that file
- **AND** assembly of other files SHALL continue
- **AND** `RestoreCompleted.Failed` SHALL be `1`

### Requirement: Restore progress events
The restore system SHALL emit `RestorePackFetched(string PackId, int BlobCount)` events during the fetch phase to enable progress tracking of pack downloads.

#### Scenario: Pack fetch progress
- **WHEN** a pack is successfully downloaded and extracted to the temp directory
- **THEN** a `RestorePackFetched` event SHALL be emitted with the pack ID and the number of blobs extracted
