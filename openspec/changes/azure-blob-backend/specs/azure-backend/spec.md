## MODIFIED Requirements

### Requirement: Upload blobs
The system SHALL upload blobs to the backend with the correct tier assignment. For data packs, the tier SHALL be caller-specified via `BackupRequest.TargetTier` (default Archive). Metadata blobs (snapshots, index deltas, key files, config) SHALL always be uploaded at Cold tier regardless of `TargetTier`.

#### Scenario: Upload pack file at default Archive tier
- **WHEN** a pack file is uploaded without an explicit `TargetTier`
- **THEN** it is stored in `data/{prefix2}/{packId}.pack` with Archive access tier

#### Scenario: Upload pack file at caller-specified tier
- **WHEN** a pack file is uploaded with `TargetTier = Cold`
- **THEN** it is stored in `data/{prefix2}/{packId}.pack` with Cold access tier

#### Scenario: Upload metadata
- **WHEN** a snapshot, index delta, key file, or config blob is uploaded
- **THEN** it is stored with Cold access tier regardless of the `TargetTier` on the backup request
