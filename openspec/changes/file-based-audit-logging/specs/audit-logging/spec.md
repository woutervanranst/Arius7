## ADDED Requirements

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
The system SHALL log every file at every stage of the archive pipeline using `LogInformation`. Each log message SHALL include the file's relative path (as the trace key), a stage tag, and relevant context. The stages and their log content SHALL be:

- **[scan]**: Enumeration summary (total file count)
- **[hash]**: File path, computed content hash (8-char truncated), file size (humanized)
- **[dedup]**: File path, content hash, dedup result (hit or miss), routing decision (large/small) for misses
- **[upload]**: Content hash, size (original and compressed, humanized) — for large file uploads
- **[tar]**: Tar seal summary (tar hash, file count, total size); individual file listing per tar; upload result (compressed size)
- **[index]**: Flush summary (new entry count)
- **[tree]**: Build summary (root hash, level count)
- **[snapshot]**: Creation timestamp
- **[pointer]**: Warnings only (failures to write pointer files)

#### Scenario: Trace a file through archive
- **WHEN** file `photos/2024/sunset.jpg` (4.2 MB) is archived as a new large file with content hash `a1b2c3d4e5f6a7b8...`
- **THEN** the log file SHALL contain lines with `photos/2024/sunset.jpg` at the `[hash]`, `[dedup]`, and `[upload]` stages, each showing the truncated hash `a1b2c3d4`

#### Scenario: Dedup hit logged
- **WHEN** file `photos/2024/beach.jpg` has a content hash already in the chunk index
- **THEN** the log file SHALL contain a `[dedup]` line indicating a dedup hit for that file

#### Scenario: Small file tar bundling logged
- **WHEN** file `photos/2024/icon.png` (12 KB) is bundled into a tar with hash `b3c4d5e6...`
- **THEN** the log file SHALL contain a `[dedup]` line routing it to small, and a `[tar]` line listing it as part of tar `b3c4d5e6`

#### Scenario: Batch dedup summary logged
- **WHEN** a dedup batch of 512 hashes is checked
- **THEN** the log file SHALL contain a `[dedup]` summary line with the batch size, hit count, and miss count

### Requirement: Per-file audit trail in restore pipeline
The system SHALL log every file at every stage of the restore pipeline using `LogInformation`. The stages and their log content SHALL be:

- **[snapshot]**: Resolved snapshot timestamp and root hash
- **[tree]**: Tree traversal progress (directories traversed, files collected)
- **[conflict]**: Files skipped (hash match), files to overwrite, files new
- **[chunk]**: Chunk resolution summary (large vs tar-bundled, grouped by chunk hash)
- **[rehydration]**: Status check results (available, rehydrated, needs rehydration, pending)
- **[download]**: Per-chunk download progress (hash, compressed size, file count)
- **[pointer]**: Warnings only (failures to write pointer files)

#### Scenario: Trace a file through restore
- **WHEN** file `photos/2024/sunset.jpg` is restored from a snapshot
- **THEN** the log file SHALL contain lines tracing its resolution through `[tree]`, `[chunk]`, and `[download]` stages

#### Scenario: Skipped file logged
- **WHEN** a local file matches the snapshot hash during restore
- **THEN** the log file SHALL contain a `[conflict]` line indicating the file was skipped

### Requirement: Audit trail in ls command
The system SHALL log the ls operation using `LogInformation`: snapshot resolved, tree traversal scope, files matched, and any lookup failures.

#### Scenario: Ls operation logged
- **WHEN** `arius ls --prefix photos/ -f .jpg` is run
- **THEN** the log file SHALL contain the resolved snapshot, prefix/filter used, and count of files matched

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
Each log file SHALL begin with an operation start marker including the command, source/target paths, account, container, and relevant options. Each log file SHALL end with an operation end marker including summary statistics and duration.

#### Scenario: Archive start marker
- **WHEN** an archive operation begins
- **THEN** the first log lines SHALL include the command (`archive`), source directory, target account/container, and options (tier, remove-local, no-pointers)

#### Scenario: Archive end marker
- **WHEN** an archive operation completes
- **THEN** the log file SHALL contain a summary with files scanned, uploaded, deduped, data transferred, and wall-clock duration
