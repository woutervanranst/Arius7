# Audit Logging Spec

## Purpose

Defines the per-invocation file-based audit logging system for Arius CLI. Each archive, restore, ls, and explicit chunk-index repair operation produces a structured log file capturing the full pipeline trace, console output, and operation summary.

## Requirements

### Requirement: Log file location and naming
The system SHALL write one log file per CLI invocation to `~/.arius/{accountName}-{containerName}/logs/{timestamp}_{command}.txt`. The timestamp format SHALL be `yyyy-MM-dd_HH-mm-ss`. The command SHALL be the CLI verb (`archive`, `restore`, `ls`). The directory SHALL be created automatically if it does not exist. The `update` command SHALL NOT produce a log file.

#### Scenario: Archive log file created
- **WHEN** `arius archive /photos -a mystorageacct -k *** -c photos` is run at 2026-03-24 14:30:00
- **THEN** a log file SHALL be created at `~/.arius/mystorageacct-photos/logs/2026-03-24_14-30-00_archive.txt`

#### Scenario: Restore log file created
- **WHEN** `arius restore /photos -a mystorageacct -k *** -c photos` is run at 2026-03-24 15:45:12
- **THEN** a log file SHALL be created at `~/.arius/mystorageacct-photos/logs/2026-03-24_15-45-12_restore.txt`

#### Scenario: Logs directory created automatically
- **WHEN** the `logs/` directory does not exist under the repo directory
- **THEN** the system SHALL create it before writing the log file

#### Scenario: Update command has no log file
- **WHEN** `arius update` is run
- **THEN** no log file SHALL be created

### Requirement: Dual-sink logging configuration
The system SHALL configure Serilog with two sinks: a console sink at `LogLevel.Warning` (matching current behavior) and a file sink at `LogLevel.Information`. All existing `ILogger<T>` call sites SHALL continue to work unchanged. The console output SHALL remain identical to current behavior.

#### Scenario: Warning appears on both console and file
- **WHEN** a `LogWarning` call is made during archive
- **THEN** the message SHALL appear on stdout (console sink) AND in the log file (file sink)

#### Scenario: Information appears only in file
- **WHEN** a `LogInformation` call is made during archive
- **THEN** the message SHALL appear in the log file but NOT on stdout

#### Scenario: Existing console behavior unchanged
- **WHEN** comparing console output before and after this change
- **THEN** the output SHALL be identical (no new messages, no format changes)

### Requirement: Per-file audit trail in archive pipeline
The system SHALL log archive pipeline activity using `LogInformation` for benchmark-relevant phase entry markers and category-specific details. Logs SHALL follow ADR-0007: top-level `[archive]` lifecycle messages for start/done/failure, `[phase] <name>` markers for coarse phase entry, and category-specific detail tags only when the detail adds information beyond the phase marker.

Chunk-index scalability work SHALL NOT introduce redundant completion logs that merely restate a phase marker without additional payload. For overlapping archive-tail work, the handler SHALL log the phase where concurrent tail work becomes active rather than pretending chunk-index flush and filetree synchronization have simple sequential end boundaries.

The archive pipeline SHALL continue to use category-specific detail tags for meaningful events, including `[dedup]` lookup outcomes, `[tar]` tar-bundle and thin-chunk details, `[tree]` tree build details, and `[snapshot]` snapshot creation details. Chunk-index flush detail logs SHALL include useful payload such as touched shard count, flushed shard count, or failure details; they SHALL NOT duplicate `[phase]` messages with empty "complete" logs.

#### Scenario: Archive tail phase logging follows ADR-0007
- **WHEN** archive enters end-of-pipeline work after uploads complete
- **THEN** it SHALL emit a `[phase]` marker for archive-tail/cache-coordination work
- **AND** when chunk-index flush and filetree synchronization run concurrently, logs SHALL identify the concurrent phase activation without emitting misleading sequential end markers

#### Scenario: Chunk-index flush detail adds payload
- **WHEN** archive flushes pending chunk-index entries
- **THEN** any `[index]` detail log SHALL include useful payload such as touched shard count, flushed shard count, repaired/missing state, or failure details
- **AND** it SHALL NOT merely restate that the flush phase completed

#### Scenario: Thin chunk metadata detail logged under tar category
- **WHEN** archive creates thin chunks for a sealed tar bundle
- **THEN** any detail log for thin chunk creation SHALL use the `[tar]` category
- **AND** it SHALL include useful payload such as thin chunk count, parent tar hash, or proportional compressed size summary
- **AND** it SHALL NOT log full hashes

### Requirement: Per-file audit trail in restore pipeline
The system SHALL log restore pipeline activity using ADR-0007 phase/detail taxonomy. Restore SHALL report chunk-index corruption, interrupted repair state, and unresolved snapshot content hashes with category-specific detail logs that add actionable context while preserving the user-facing repair instruction.

#### Scenario: Restore logs chunk-index resolution failure
- **WHEN** restore fails because chunk-index lookup detects corruption, interrupted repair state, or unresolved snapshot content hashes
- **THEN** the log file SHALL contain a `[chunk]` or `[restore]` detail log identifying the failure category and repair instruction
- **AND** it SHALL NOT add redundant completion logs for phases that did not complete

### Requirement: Audit trail in ls command
The system SHALL log ls pipeline activity using ADR-0007 phase/detail taxonomy. Chunk-index lookup failures during size resolution SHALL be logged with actionable repair context.

#### Scenario: Ls operation logged
- **WHEN** `arius ls --prefix photos/ -f .jpg` is run
- **THEN** the log file SHALL contain the resolved snapshot, prefix/filter used, and count of files matched

#### Scenario: Ls logs chunk-index lookup failure
- **WHEN** `ls` fails because chunk-index lookup detects corruption or interrupted repair state
- **THEN** the log file SHALL contain a detail log identifying the chunk-index failure and repair instruction
- **AND** it SHALL preserve the existing console behavior for user-facing output

### Requirement: Hash truncation in log messages
All hashes in log messages SHALL be truncated to the first 8 hexadecimal characters. Full hashes SHALL remain in the underlying data structures and storage — truncation is a log formatting concern only.

#### Scenario: Content hash truncated
- **WHEN** a file with content hash `a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6...` is logged
- **THEN** the log message SHALL display the hash as `a1b2c3d4`

#### Scenario: Tar hash truncated
- **WHEN** a sealed tar with hash `b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8...` is logged
- **THEN** the log message SHALL display the hash as `b3c4d5e6`

### Requirement: Humanized sizes in log messages
All file sizes, chunk sizes, and transfer sizes in log messages SHALL be formatted using Humanizer (e.g. `4.2 MB`, `12 KB`, `1.8 GB`). Raw byte counts SHALL NOT appear in log messages.

#### Scenario: File size humanized
- **WHEN** a 4,404,019-byte file is logged at the hash stage
- **THEN** the log message SHALL display the size as a humanized string (e.g. `4.2 MB`)

#### Scenario: Compressed size humanized
- **WHEN** a chunk upload completes with a compressed size of 1,887,436 bytes
- **THEN** the log message SHALL display the compressed size as a humanized string (e.g. `1.8 MB`)

### Requirement: Thread ID in log output
Every log line in the file sink SHALL include the managed thread ID. The format SHALL include a thread ID field (e.g. `[T:12]`).

#### Scenario: Concurrent hash workers distinguishable
- **WHEN** 4 hash workers are processing files concurrently
- **THEN** the log file SHALL show different thread IDs for log lines from different workers

### Requirement: Log line format
The file sink log line format SHALL be: `[{Timestamp:HH:mm:ss.fff}] [{Level:u3}] [{Stage}] [T:{ThreadId}] {Message}{NewLine}`. Lines without a stage tag (e.g. operation start/end summaries) SHALL omit the `[{Stage}]` and `[T:{ThreadId}]` fields.

#### Scenario: Formatted log line
- **WHEN** a hash worker on thread 12 logs a file hash result
- **THEN** the log line SHALL be formatted as `[14:30:00.015] [INF] [hash] [T:12] photos/2024/sunset.jpg -> a1b2c3d4 (4.2 MB)`

### Requirement: Spectre.Console output capture
The system SHALL capture all Spectre.Console output using `Recorder(AnsiConsole.Console)` and append the plain-text export to the end of the log file after the operation completes. The captured output SHALL be separated by a header line `--- Console Output ---`. Tables, progress summaries, and error messages SHALL all be captured.

#### Scenario: Archive summary captured
- **WHEN** an archive operation completes and Spectre.Console displays a summary
- **THEN** the log file SHALL end with a `--- Console Output ---` section containing the plain-text version of the summary

#### Scenario: Ls table captured
- **WHEN** an ls command displays a table of files via Spectre.Console
- **THEN** the log file SHALL contain the table as ASCII art in the console output section

#### Scenario: Restore cost table captured
- **WHEN** a restore displays a rehydration cost estimate table
- **THEN** the log file SHALL contain the table in the console output section

### Requirement: Operation start and end markers
Each archive, restore, ls, and explicit chunk-index repair invocation SHALL use top-level operation lifecycle logs for start, done, and failure. Explicit chunk-index repair SHALL use `[repair]` lifecycle/detail logs and `[phase]` phase-entry markers for major repair stages such as marker setup, chunk scan, local shard rebuild, remote shard upload, stale shard deletion, and marker cleanup.

#### Scenario: Archive start marker
- **WHEN** an archive operation begins
- **THEN** the first log lines SHALL include the command (`archive`), source directory, target account/container, and options (tier, remove-local, no-pointers)

#### Scenario: Archive end marker
- **WHEN** an archive operation completes
- **THEN** the log file SHALL contain a summary with files scanned, uploaded, deduped, data transferred, and wall-clock duration

#### Scenario: Chunk-index repair lifecycle logged
- **WHEN** the explicit chunk-index repair command runs
- **THEN** the log file SHALL include `[repair]` start and done or failure lifecycle messages
- **AND** it SHALL include `[phase]` markers for major repair stages
- **AND** repair detail logs SHALL include useful payload such as listed chunk count, rebuilt shard count, uploaded shard count, and stale shard deletion count
