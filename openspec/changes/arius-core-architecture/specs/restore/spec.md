## ADDED Requirements

### Requirement: Multi-phase restore
The system SHALL restore files from a snapshot through three overlapping phases: plan (identify needed packs), rehydrate (initiate and poll archive-tier rehydration), and restore (download, decrypt, reassemble files).

#### Scenario: Full restore
- **WHEN** user runs `restore <snapshot-id> --target /restore/path`
- **THEN** the system plans the restore, shows a cost estimate, requests confirmation, rehydrates packs, and restores files to the target path

### Requirement: Cost estimation before rehydration
Before initiating rehydration, the system SHALL calculate and display the estimated cost (rehydration cost + read operation cost) and the number of packs/bytes to rehydrate.

#### Scenario: Cost estimate display
- **WHEN** the restore plan is complete
- **THEN** the system displays: number of packs to rehydrate, total bytes, estimated dollar cost, estimated time (standard vs high priority), and waits for user confirmation

#### Scenario: User declines restore
- **WHEN** the user declines the cost estimate confirmation
- **THEN** no rehydration is initiated and the operation is cancelled

### Requirement: Overlapping rehydrate and restore
As individual packs become rehydrated, the system SHALL immediately begin downloading and restoring files from those packs without waiting for all packs to complete rehydration.

#### Scenario: Progressive restore
- **WHEN** pack A is rehydrated while packs B and C are still pending
- **THEN** files whose blobs are all in pack A are restored immediately

### Requirement: Rehydration staging area
Rehydrated packs SHALL be downloaded to a staging directory (`~/.arius/hydrated/`), decrypted, decompressed (gunzip), and extracted (tar) to recover individual blobs. Staging files SHALL be deleted after their blobs are written to the target.

#### Scenario: Pack extraction pipeline
- **WHEN** a rehydrated pack is downloaded
- **THEN** the system decrypts (AES-256-CBC) → decompresses (gzip) → extracts (TAR) to recover individual blob files named by their SHA-256 hash

#### Scenario: Staging cleanup
- **WHEN** all blobs from a downloaded pack have been written to the target
- **THEN** the staging file for that pack is deleted

#### Scenario: Keep staged packs
- **WHEN** user runs restore with `--keep-hydrated`
- **THEN** staging files are retained after extraction

### Requirement: Resumable restore
If a restore is interrupted, re-running the same command SHALL resume from where it left off by checking which packs are already rehydrated in Azure and which files already exist in the target.

#### Scenario: Resume after interruption
- **WHEN** a restore is interrupted and re-run
- **THEN** the system queries Azure for rehydrated packs, skips already-restored files, and continues with remaining work

### Requirement: Rehydration priority selection
The user SHALL be able to choose between standard (up to 15h, cheaper) and high priority (under 1h, more expensive) rehydration.

#### Scenario: High priority restore
- **WHEN** user runs `restore --priority high <snapshot> --target /path`
- **THEN** rehydration is initiated with high priority

### Requirement: Partial restore
The system SHALL support restoring specific files or directories from a snapshot using `--include` and `--exclude` patterns, rehydrating only the packs that contain needed blobs.

#### Scenario: Restore single directory
- **WHEN** user runs `restore <snapshot> --target /path --include "/documents/taxes/"`
- **THEN** only packs containing blobs for files under `/documents/taxes/` are rehydrated and restored

### Requirement: Restore progress streaming
The restore operation SHALL stream progress events for both rehydration and file restoration phases.

#### Scenario: Streaming restore events
- **WHEN** a restore is in progress
- **THEN** the handler yields `IAsyncEnumerable<RestoreEvent>` including `RestorePlanReady`, `RehydrationProgress`, `FileRestored`, and `RestoreComplete` events

### Requirement: Integrity verification on restore
After restoring each file, the system SHALL verify the SHA-256 hash of the reassembled plaintext content against the stored blob hashes.

#### Scenario: Integrity check
- **WHEN** a file is restored from its component blobs
- **THEN** each blob's SHA-256 hash is verified against the index entry; a mismatch causes an error
