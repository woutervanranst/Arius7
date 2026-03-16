## ADDED Requirements

### Requirement: Parallelism CLI option
The `backup` and `restore` commands SHALL accept an optional `--parallelism <N>` argument that sets the overall concurrency level. When specified, it SHALL scale all pipeline stage limits proportionally.

#### Scenario: Backup with custom parallelism
- **WHEN** user runs `backup --parallelism 2 /path/to/data`
- **THEN** the backup pipeline SHALL use reduced concurrency across all stages

#### Scenario: Default parallelism
- **WHEN** user runs `backup /path/to/data` without `--parallelism`
- **THEN** the pipeline SHALL use `ParallelismOptions.Default` (CPU-count-based)

### Requirement: Error event display
The CLI SHALL handle `BackupFileError` and `RestoreFileError` events by displaying them as styled warnings (e.g., `[red]ERROR[/] path: message`) without stopping the progress display.

#### Scenario: File error during backup progress
- **WHEN** a `BackupFileError` event is received during backup
- **THEN** the CLI SHALL display the error in red below the progress bar
- **AND** the progress bar SHALL continue advancing for remaining files

#### Scenario: Error summary in completion
- **WHEN** a backup completes with `Failed > 0`
- **THEN** the CLI SHALL display a summary line: `"N files failed"` in red after the completion message

### Requirement: Chunk-level dedup statistics display
The CLI SHALL display chunk-level dedup statistics from `BackupCompleted` including total chunks, new chunks, deduplicated chunks, and bytes saved.

#### Scenario: Backup completion with dedup stats
- **WHEN** a backup completes successfully
- **THEN** the CLI SHALL display: files processed/deduplicated/stored/failed, chunks total/new/deduplicated, bytes total/new/deduplicated — all formatted with Humanizer

### Requirement: Humanizer formatting
The CLI SHALL use `Humanizer.Core` for all human-readable formatting of byte sizes, durations, quantities, and relative timestamps. Manual `FormatBytes` helpers SHALL be removed.

#### Scenario: Byte size formatting
- **WHEN** the CLI displays a byte count (e.g., restore plan total bytes, backup dedup bytes)
- **THEN** it SHALL use Humanizer's `.Bytes().Humanize()` (e.g., `"14.2 GB"` instead of `"14200000000"`)

#### Scenario: Duration formatting
- **WHEN** the CLI displays an operation duration
- **THEN** it SHALL use Humanizer's `.Humanize(precision: 2)` (e.g., `"2 minutes, 30 seconds"`)

#### Scenario: Relative timestamp formatting
- **WHEN** the CLI displays a snapshot timestamp in the snapshots list
- **THEN** it SHALL use Humanizer's `.Humanize()` (e.g., `"3 hours ago"`)

#### Scenario: Quantity formatting
- **WHEN** the CLI displays a count with a unit (e.g., files, chunks, packs)
- **THEN** it SHALL use Humanizer's `.ToQuantity()` (e.g., `"1,247 files"`, `"1 chunk"`)
