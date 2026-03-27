# Restore Pipeline Spec

## Purpose

Defines the restore pipeline for Arius: snapshot resolution, tree traversal, conflict checking, chunk resolution, rehydration, cost estimation, streaming restore, and pointer file creation.

## Requirements

### Requirement: Snapshot resolution
The system SHALL resolve the target snapshot for restore operations. When `-v` is specified, the system SHALL locate the exact snapshot. When `-v` is omitted, the system SHALL use the latest snapshot. The system SHALL download and parse the snapshot manifest to obtain the root tree hash.

#### Scenario: Restore latest snapshot
- **WHEN** `arius restore /photos/` is run without `-v`
- **THEN** the system SHALL list all snapshots and select the most recent one

#### Scenario: Restore specific version
- **WHEN** `arius restore /photos/ -v 2026-03-21T140000.000Z` is run
- **THEN** the system SHALL locate and use the specified snapshot

#### Scenario: Snapshot not found
- **WHEN** a specified snapshot version does not exist
- **THEN** the system SHALL report an error and exit

### Requirement: Tree traversal to target path
The system SHALL walk the merkle tree from the root to the requested path, downloading tree blobs on demand through the tiered cache (L1 memory → L2 disk → L3 Azure). Tree blobs SHALL be parsed as text format: each line is either `<hash> F <created> <modified> <name>` (file entry) or `<hash> D <name>` (directory entry). For `F` lines, the system SHALL split on the first 4 spaces to extract hash, type, created, modified, and name (remainder). For `D` lines, the system SHALL split on the first 2 spaces to extract hash, type, and name (remainder). For a file restore, traversal stops at the file's directory. For a directory restore, the system SHALL enumerate the full subtree. For a full restore, the entire tree SHALL be traversed.

#### Scenario: Restore single file
- **WHEN** restoring `photos/2024/june/vacation.jpg`
- **THEN** the system SHALL download tree blobs for `/`, `photos/`, `photos/2024/`, `photos/2024/june/` and locate the file entry

#### Scenario: Restore directory
- **WHEN** restoring `photos/2024/`
- **THEN** the system SHALL traverse the full subtree under `photos/2024/` and collect all file entries

#### Scenario: Restore full snapshot
- **WHEN** restoring with `--full`
- **THEN** the system SHALL traverse the entire tree and collect all file entries

#### Scenario: Parse file entry from tree blob
- **WHEN** a tree blob line is `abc123... F 2026-03-25T10:00:00.0000000+00:00 2026-03-25T12:30:00.0000000+00:00 my vacation photo.jpg`
- **THEN** the system SHALL parse hash as `abc123...`, type as file, created and modified as the ISO-8601 timestamps, and name as `my vacation photo.jpg`

#### Scenario: Parse directory entry from tree blob
- **WHEN** a tree blob line is `def456... D 2024 trip/`
- **THEN** the system SHALL parse hash as `def456...`, type as directory, and name as `2024 trip/`

### Requirement: Restore conflict check
The system SHALL check each target file against the local filesystem before restoring. If a local file exists and its hash matches the snapshot entry, it SHALL be skipped. If a local file exists with a different hash, the system SHALL prompt the user (overwrite? y/N/all). This check makes restore idempotent on re-run.

#### Scenario: Local file matches snapshot
- **WHEN** local file `photos/vacation.jpg` exists and its hash matches the snapshot entry
- **THEN** the system SHALL skip the file (already restored correctly)

#### Scenario: Local file differs from snapshot
- **WHEN** local file `photos/vacation.jpg` exists but its hash differs from the snapshot entry
- **THEN** the system SHALL prompt the user whether to overwrite

#### Scenario: Local file does not exist
- **WHEN** the target path has no local file
- **THEN** the system SHALL proceed with restore

### Requirement: Chunk resolution from index
The system SHALL look up each content hash in the chunk index to determine the chunk hash and chunk type. For large files, the content-hash equals the chunk-hash (reconstructed on parse from 3-field entries). For tar-bundled files, the content-hash maps to a different chunk-hash (the tar, from 4-field entries). The system SHALL group all file entries by chunk hash to minimize downloads (multiple files from the same tar → one download).

#### Scenario: Large file chunk resolution
- **WHEN** content hash `abc123` is looked up and the in-memory entry has content-hash equal to chunk-hash
- **THEN** the system SHALL identify this as a large file chunk for direct streaming restore

#### Scenario: Tar-bundled file chunk resolution
- **WHEN** content hash `def456` is looked up and the in-memory entry has a different chunk-hash `tar789`
- **THEN** the system SHALL identify this as a tar-bundled file and group it with other files from `tar789`

#### Scenario: Multiple files from same tar
- **WHEN** 5 files all map to the same tar chunk hash
- **THEN** the system SHALL download the tar once and extract all 5 files in a single pass

### Requirement: Rehydration status check
The system SHALL check the availability of each needed chunk before downloading. It SHALL check: (1) `chunks-rehydrated/<chunk-hash>` exists → already rehydrated, (2) `chunks/<chunk-hash>` blob tier is Hot/Cool → directly downloadable, (3) blob tier is Archive → needs rehydration. The result SHALL categorize chunks into: ready (direct download), rehydrated (download from rehydrated copy), and needs-rehydration.

#### Scenario: Chunk already rehydrated from previous run
- **WHEN** `chunks-rehydrated/<chunk-hash>` exists from a previous restore attempt
- **THEN** the system SHALL download from `chunks-rehydrated/` without starting a new rehydration

#### Scenario: Chunk in Hot tier
- **WHEN** a chunk was uploaded with `--tier Hot`
- **THEN** the system SHALL download directly from `chunks/` without rehydration

#### Scenario: Chunk in Archive tier
- **WHEN** a chunk is in Archive tier with no rehydrated copy
- **THEN** the system SHALL mark it as needing rehydration

### Requirement: Cost estimation and user confirmation
The system SHALL display a detailed cost breakdown before restoring and require user confirmation. The cost model SHALL include four components: data retrieval cost, read operation cost, write operation cost, and storage cost for rehydrated copies. Rates SHALL be loaded from a pricing configuration file (JSON) with override capability. The breakdown SHALL show both Standard and High Priority costs per component. The chunk index entry's `compressed-size` field SHALL be used for size calculations. The default assumption for storage duration SHALL be 1 month.

The cost algorithm SHALL be:
- `retrievalCost = totalGB * archive.retrievalPerGB` (or `retrievalHighPerGB` for High Priority)
- `readOpsCost = ceil(numberOfBlobs / 10000) * archive.readOpsPer10000` (or `readOpsHighPer10000`)
- `writeOpsCost = ceil(numberOfBlobs / 10000) * targetTier.writeOpsPer10000`
- `storageCost = totalGB * targetTier.storagePerGBPerMonth * monthsStored`
- `totalCost = retrievalCost + readOpsCost + writeOpsCost + storageCost`

Where `targetTier` is Hot (matching `chunks-rehydrated/` convention) and `monthsStored` defaults to 1.

#### Scenario: Cost estimation with full breakdown
- **WHEN** a restore requires 200 archive-tier chunks totaling 20 GB compressed
- **THEN** the system SHALL compute retrieval cost, read ops cost, write ops cost, and storage cost using config-driven rates and display all four components

#### Scenario: User declines
- **WHEN** the user responds "N" to the confirmation prompt
- **THEN** the system SHALL exit without restoring or rehydrating

#### Scenario: Rehydration priority selection
- **WHEN** archive-tier chunks need rehydration
- **THEN** the system SHALL prompt the user to choose Standard or High Priority and display the cost difference per component

#### Scenario: Pricing config loaded from file
- **WHEN** a `pricing.json` exists in the working directory or `~/.arius/`
- **THEN** the system SHALL load rates from that file instead of the embedded defaults

#### Scenario: Default pricing used when no override
- **WHEN** no `pricing.json` override file exists
- **THEN** the system SHALL use the embedded EUR West Europe rates

### Requirement: Restore cost estimate model
The system SHALL use a `RestoreCostEstimate` record (renamed from `RehydrationCostEstimate`) with per-component cost fields: `RetrievalCostStandard`, `RetrievalCostHigh`, `ReadOpsCostStandard`, `ReadOpsCostHigh`, `WriteOpsCost`, `StorageCost`, plus computed `TotalStandard` and `TotalHigh` properties. The model SHALL also include chunk availability counts: `ChunksAvailable`, `ChunksAlreadyRehydrated`, `ChunksNeedingRehydration`, `ChunksPendingRehydration`, `RehydrationBytes`, and `DownloadBytes`.

#### Scenario: RestoreCostEstimate computed properties
- **WHEN** a `RestoreCostEstimate` is constructed with per-component costs
- **THEN** `TotalStandard` SHALL equal `RetrievalCostStandard + ReadOpsCostStandard + WriteOpsCost + StorageCost` and `TotalHigh` SHALL equal `RetrievalCostHigh + ReadOpsCostHigh + WriteOpsCost + StorageCost`

#### Scenario: Zero chunks needing rehydration
- **WHEN** all chunks are already available (Hot/Cool tier or already rehydrated)
- **THEN** `RestoreCostEstimate` SHALL have zero costs for all rehydration components and the system SHALL skip the rehydration confirmation prompt

### Requirement: Pricing configuration
The system SHALL load Azure pricing rates from a JSON configuration file. The file SHALL contain rate structures for: `archive` (retrievalPerGB, retrievalHighPerGB, readOpsPer10000, readOpsHighPer10000), `hot` (writeOpsPer10000, storagePerGBPerMonth), `cool` (writeOpsPer10000, storagePerGBPerMonth), `cold` (writeOpsPer10000, storagePerGBPerMonth). The embedded default SHALL use EUR West Europe rates. The pricing file SHALL be overridable by placing a file in the working directory or `~/.arius/` config path.

#### Scenario: Pricing file structure
- **WHEN** the pricing configuration is loaded
- **THEN** it SHALL contain archive retrieval rates, archive read operation rates, and target tier write/storage rates

#### Scenario: Override pricing file
- **WHEN** a `pricing.json` file is placed in the working directory
- **THEN** the system SHALL use those rates instead of the embedded defaults

#### Scenario: Malformed pricing file
- **WHEN** a pricing override file cannot be parsed as valid JSON
- **THEN** the system SHALL report an error and exit

### Requirement: Streaming restore (no local cache)
The system SHALL restore files by streaming chunks directly to their final path without intermediate local caching. For large files: download → decrypt → gunzip → write to final path. For tar bundles: download → decrypt → gunzip → iterate tar entries → extract needed files by content-hash name → write to final paths. Peak temp disk usage SHALL be zero (fully streaming). All files needing a given chunk SHALL be grouped and extracted in a single streaming pass.

#### Scenario: Large file streaming restore
- **WHEN** restoring a 2 GB large file
- **THEN** the system SHALL stream download → decrypt → gunzip → write directly to the target path with no intermediate temp file

#### Scenario: Tar bundle streaming extract
- **WHEN** restoring 3 files that are bundled in the same tar
- **THEN** the system SHALL download the tar once, stream through the tar entries, and extract the 3 matching content-hash entries to their final paths

#### Scenario: Tar entry not needed
- **WHEN** a tar contains 300 files but only 2 are needed for this restore
- **THEN** the system SHALL skip the 298 unneeded entries during streaming tar iteration

### Requirement: Rehydration kick-off
The system SHALL start rehydration for all archive-tier chunks that are not yet rehydrated, using the user-selected priority (Standard or High). Rehydration SHALL copy blobs to `chunks-rehydrated/` (Hot tier). If Azure throttles the request, the system SHALL retry with exponential backoff. After starting rehydration, the system SHALL exit with a message indicating how many chunks are pending and suggesting the user re-run later.

#### Scenario: Start rehydration
- **WHEN** 62 chunks need rehydration with Standard priority
- **THEN** the system SHALL issue copy-to-rehydrate requests for all 62 and report "Re-run this command in ~15 hours to complete the restore"

#### Scenario: Azure throttling during rehydration
- **WHEN** Azure returns a throttling error during rehydration requests
- **THEN** the system SHALL retry with exponential backoff

#### Scenario: Rehydration already pending
- **WHEN** a chunk already has a pending rehydration request from a previous run
- **THEN** the system SHALL recognize the pending state and not issue a duplicate request

### Requirement: Idempotent restore
Restore SHALL be fully idempotent. Re-running the same restore command SHALL: skip files already restored correctly (hash match), download newly rehydrated chunks, skip still-pending rehydrations (must not issue duplicate requests), and report remaining files. Each run is a self-contained scan-and-act cycle with no persistent local state. The restore pipeline SHALL publish notification events throughout:

- `RestoreStartedEvent(TotalFiles)` before beginning downloads
- `FileRestoredEvent(RelativePath, FileSize)` after each file is written to disk
- `FileSkippedEvent(RelativePath, FileSize)` for each file skipped due to hash match
- `RehydrationStartedEvent(ChunkCount, TotalBytes)` when rehydration is kicked off

`FileRestoredEvent` and `FileSkippedEvent` SHALL carry `long FileSize` (the file's uncompressed size in bytes) so the CLI can accumulate bytes-restored/skipped and show per-file sizes in the restore tail display.

#### Scenario: Partial restore re-run
- **WHEN** a restore previously restored 500 of 1000 files and rehydration has completed for 300 more chunks
- **THEN** re-running SHALL skip the 500 completed files, restore the 300 newly available, and report 200 still pending

#### Scenario: Full restore complete
- **WHEN** all files have been restored across multiple runs
- **THEN** the system SHALL report all files restored and prompt to clean up `chunks-rehydrated/`

#### Scenario: Progress events emitted during restore
- **WHEN** a restore operation begins
- **THEN** the system SHALL publish `RestoreStartedEvent(TotalFiles)` before downloading, and `FileRestoredEvent(path, size)` / `FileSkippedEvent(path, size)` for each file processed

### Requirement: FileSize source at each publish site
The pipeline SHALL obtain `FileSize` from the most direct available source at each of the three publish sites:

| Site | Event | FileSize source |
|------|-------|-----------------|
| Conflict check (step 3) — file skipped | `FileSkippedEvent` | `fs.Length` — the `FileStream` used for hash comparison is still open |
| Large file restore (step 7, `RestoreLargeFileAsync`) | `FileRestoredEvent` | `indexEntry.OriginalSize` — from the index lookup already in scope |
| Tar bundle restore (`RestoreTarBundleAsync`) | `FileRestoredEvent` | `dataBuffer?.Length ?? 0` — the buffered decompressed entry data |

No changes to `FileToRestore`, `TreeEntry`, or `IndexEntry` are needed.

#### Scenario: Skip site file size
- **WHEN** a file is skipped because its local hash matches the archive hash
- **THEN** `FileSkippedEvent` SHALL carry the size of the existing local file (obtained from the open `FileStream`)

#### Scenario: Large file restore site file size
- **WHEN** a large file is restored via `RestoreLargeFileAsync`
- **THEN** `FileRestoredEvent` SHALL carry `indexEntry.OriginalSize`

#### Scenario: Tar entry restore site file size
- **WHEN** a file is extracted from a tar bundle in `RestoreTarBundleAsync`
- **THEN** `FileRestoredEvent` SHALL carry `dataBuffer?.Length ?? 0` (0 for empty files)

### Requirement: Cleanup of rehydrated blobs
After a full restore is complete, the system SHALL prompt the user to delete blobs in `chunks-rehydrated/`. These are Hot-tier copies that incur ongoing storage costs.

#### Scenario: Cleanup prompt after full restore
- **WHEN** all requested files are restored
- **THEN** the system SHALL prompt "Delete N rehydrated chunks (X GB) from Azure? [Y/n]"

#### Scenario: User declines cleanup
- **WHEN** user responds "n" to the cleanup prompt
- **THEN** the blobs SHALL be retained (useful if another restore is planned soon)

### Requirement: Pointer file creation during restore
The system SHALL create `.pointer.arius` files alongside each restored file (unless `--no-pointers` is set). The pointer SHALL contain the content hash. File metadata (created, modified dates) SHALL be set from the tree blob entry.

#### Scenario: Restored file gets pointer
- **WHEN** `photos/vacation.jpg` is restored from a snapshot
- **THEN** `photos/vacation.jpg.pointer.arius` SHALL be created with the content hash, and the file's modified date SHALL be set from the snapshot metadata

### Requirement: Rehydration state machine test coverage
The system SHALL have test coverage for all three rehydration states in the restore pipeline: (1) chunk needs rehydration (initiates copy-to-rehydrate), (2) chunk rehydration is pending (recognizes pending state, no duplicate request), (3) chunk is already rehydrated (downloads from `chunks-rehydrated/`). Both mock-based unit tests and real Azure E2E tests SHALL exercise these states.

#### Scenario: Mock test - initiate rehydration
- **WHEN** a unit test runs with a mock `IBlobStorageService` returning `Tier: Archive` and no rehydrated copy exists
- **THEN** the restore pipeline SHALL call the copy-to-rehydrate method for the chunk

#### Scenario: Mock test - pending rehydration detected
- **WHEN** a unit test runs with a mock returning pending rehydration state for a chunk
- **THEN** the restore pipeline SHALL NOT issue a duplicate rehydration request

#### Scenario: Mock test - already rehydrated
- **WHEN** a unit test runs with a mock where `chunks-rehydrated/<hash>` exists
- **THEN** the restore pipeline SHALL download from the rehydrated path

### Requirement: E2E rehydration test with sideload
The system SHALL have an E2E test against real Azure Blob Storage that: archives small files (<1 KB) to Archive tier, verifies blobs land in Archive tier, attempts restore (expects rehydration initiation), verifies re-run detects pending rehydration, then sideloads rehydrated content to `chunks-rehydrated/{hash}` in Hot tier and verifies full restore with byte-identical content. The test SHALL be gated by `ARIUS_E2E_ACCOUNT` / `ARIUS_E2E_KEY` environment variables.

#### Scenario: Archive to Archive tier
- **WHEN** the E2E test archives 2-3 files of ~100-500 bytes with `--tier Archive`
- **THEN** the blobs SHALL be verified as Archive tier via `GetProperties`

#### Scenario: Restore initiates rehydration
- **WHEN** restore is run against Archive-tier blobs
- **THEN** the system SHALL initiate rehydration and report chunks pending

#### Scenario: Re-run detects pending rehydration
- **WHEN** restore is re-run while rehydration is pending
- **THEN** the system SHALL recognize the pending state and not duplicate rehydration requests

#### Scenario: Sideloaded rehydration enables full restore
- **WHEN** chunk content is manually uploaded to `chunks-rehydrated/{hash}` in Hot tier
- **THEN** re-running restore SHALL detect the sideloaded blob, download from it, and restore files with byte-identical content

#### Scenario: Test cost documentation
- **WHEN** the E2E test completes and the container is deleted
- **THEN** the Azure cost SHALL be negligible (prorated early deletion for tiny files = fractions of a cent), documented in test comments
