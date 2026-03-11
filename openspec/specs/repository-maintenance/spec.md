# repository-maintenance

## Purpose
Defines operations for maintaining repository health and storage efficiency: pruning unreferenced data, repacking mixed packs, integrity checking, and repair operations.

## Requirements

### Requirement: Prune unreferenced data
The system SHALL identify and remove pack files that contain no blobs referenced by any snapshot, reclaiming storage space.

#### Scenario: Remove fully unreferenced packs
- **WHEN** user runs `prune` and pack files exist whose blobs are not referenced by any snapshot
- **THEN** those pack files are deleted from Azure and the index is updated

### Requirement: Repack mixed packs
The system SHALL identify packs where some blobs are referenced and others are not. It SHALL rehydrate these packs, extract the referenced blobs, repack them, upload new packs, and delete the old ones.

#### Scenario: Repack partially used pack
- **WHEN** a pack contains 50% referenced and 50% unreferenced blobs
- **THEN** the referenced blobs are repacked into new packs, the old pack is deleted, and the index is updated

### Requirement: Prune cost awareness
Prune SHALL warn about archive tier early deletion fees when packs are younger than 180 days.

#### Scenario: Early deletion warning
- **WHEN** prune identifies packs younger than 180 days for deletion
- **THEN** the system warns the user about early deletion charges and displays the estimated cost

#### Scenario: Min-age filter
- **WHEN** user runs `prune --min-age 180d`
- **THEN** only packs older than 180 days are considered for pruning

### Requirement: Prune rehydration cost estimate
Before repacking, prune SHALL display the rehydration cost for packs that need repacking and request confirmation.

#### Scenario: Prune cost estimate
- **WHEN** prune determines packs need repacking
- **THEN** it displays the estimated rehydration cost, number of packs, and total bytes before proceeding

### Requirement: Prune dry run
The system SHALL support `--dry-run` to show what prune would do without making changes.

#### Scenario: Dry run prune
- **WHEN** user runs `prune --dry-run`
- **THEN** the system shows packs to delete, packs to repack, estimated cost, and space to reclaim, without modifying the repository

### Requirement: Check repository integrity
The system SHALL verify repository consistency by checking that all snapshots reference valid trees, all trees reference valid blobs, and all blob references in indexes point to existing packs.

#### Scenario: Metadata check
- **WHEN** user runs `check`
- **THEN** the system verifies snapshot→tree→blob→pack reference chains using only metadata (cold tier, no rehydration)

#### Scenario: Data check
- **WHEN** user runs `check --read-data`
- **THEN** the system additionally rehydrates and reads all pack files, verifying their SHA-256 hashes and internal blob integrity (with cost estimate and confirmation first)

### Requirement: Repair index
The system SHALL support rebuilding the index from pack file headers when the index is corrupted or missing.

#### Scenario: Repair index
- **WHEN** user runs `repair index`
- **THEN** the system rehydrates all packs, reads their headers, and rebuilds the index files

### Requirement: Repair snapshots
The system SHALL support repairing snapshots that reference missing trees by removing broken references.

#### Scenario: Repair snapshots
- **WHEN** user runs `repair snapshots`
- **THEN** snapshots referencing missing trees are updated to remove broken references

### Requirement: Prune progress streaming
Prune SHALL stream progress events including pack analysis, repack progress, and deletion progress.

#### Scenario: Prune events
- **WHEN** a prune operation is in progress
- **THEN** the handler yields `IAsyncEnumerable<PruneEvent>` with analysis, rehydration, repack, and deletion progress
