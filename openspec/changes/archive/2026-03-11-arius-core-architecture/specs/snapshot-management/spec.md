## ADDED Requirements

### Requirement: List snapshots
The system SHALL list all snapshots in the repository with their ID, timestamp, host, paths, and tags.

#### Scenario: List all snapshots
- **WHEN** user runs `snapshots`
- **THEN** the system displays all snapshots sorted by time with fields: short ID, time, host, paths, tags

#### Scenario: Filter by host
- **WHEN** user runs `snapshots --host myserver`
- **THEN** only snapshots created from host `myserver` are shown

#### Scenario: Filter by tag
- **WHEN** user runs `snapshots --tag daily`
- **THEN** only snapshots with the `daily` tag are shown

### Requirement: Forget snapshots by policy
The system SHALL remove snapshots based on retention policies: `--keep-last N`, `--keep-hourly N`, `--keep-daily N`, `--keep-weekly N`, `--keep-monthly N`, `--keep-yearly N`, `--keep-within DURATION`, and `--keep-tag TAG`.

#### Scenario: Keep last N
- **WHEN** user runs `forget --keep-last 5`
- **THEN** the 5 most recent snapshots are kept and all others are removed

#### Scenario: Keep daily
- **WHEN** user runs `forget --keep-daily 7`
- **THEN** one snapshot per day for the last 7 days is kept

#### Scenario: Combined policies
- **WHEN** user runs `forget --keep-daily 7 --keep-weekly 4 --keep-monthly 12`
- **THEN** snapshots matching any of the retention criteria are kept; the rest are removed

#### Scenario: Dry run
- **WHEN** user runs `forget --dry-run --keep-last 3`
- **THEN** the system shows which snapshots would be kept and removed without actually deleting anything

### Requirement: Group-by for forget
The forget command SHALL support `--group-by host,paths,tags` to apply retention policies independently per group.

#### Scenario: Group by host
- **WHEN** user runs `forget --group-by host --keep-last 3`
- **THEN** the 3 most recent snapshots per host are kept

### Requirement: Tag snapshots
The system SHALL support modifying tags on existing snapshots via `--set`, `--add`, and `--remove` operations.

#### Scenario: Add tag
- **WHEN** user runs `tag --add important <snapshot-id>`
- **THEN** the tag `important` is added to the snapshot

#### Scenario: Set tags
- **WHEN** user runs `tag --set production --set verified <snapshot-id>`
- **THEN** the snapshot's tags are replaced with `["production", "verified"]`

### Requirement: Diff snapshots
The system SHALL show differences between two snapshots: added, removed, modified, and type-changed entries.

#### Scenario: Diff two snapshots
- **WHEN** user runs `diff <snap1> <snap2>`
- **THEN** the system shows entries that were added, removed, or modified between the two snapshots

### Requirement: Forget progress streaming
Forget operations SHALL stream progress events indicating which snapshots are being evaluated, kept, or removed.

#### Scenario: Forget events
- **WHEN** a forget operation is in progress
- **THEN** the handler yields `IAsyncEnumerable<ForgetEvent>` with kept/removed decisions per snapshot
