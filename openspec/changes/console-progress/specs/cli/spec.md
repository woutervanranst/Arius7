## MODIFIED Requirements

### Requirement: Archive progress display
The CLI SHALL display live progress during archive using Spectre.Console `Progress` with three concurrent progress tasks: Scanning (indeterminate until enumeration completes, then determinate), Hashing (files hashed / total), and Uploading (chunks uploaded, bytes uploaded). Below the aggregate bars, the display SHALL show per-file progress sub-lines for in-flight large file operations (hashing and uploading) with file name, percentage, and bytes processed vs total. Tar bundle status (sealing, uploading, entry count) SHALL also be reflected in the Uploading bar or a status line. The display SHALL be driven by a shared `ProgressState` singleton updated by `INotificationHandler<T>` implementations.

#### Scenario: Live progress during archive
- **WHEN** archiving 10,000 files
- **THEN** the CLI SHALL show three live-updating Spectre.Console progress bars (Scanning, Hashing, Uploading) driven by notification event handlers

#### Scenario: In-flight visibility
- **WHEN** 4 files are being hashed and 3 chunks are being uploaded concurrently
- **THEN** the display SHALL list each in-flight large file with its name, percentage, and byte progress

#### Scenario: Non-interactive terminal
- **WHEN** the terminal does not support interactive output (piped or CI)
- **THEN** the CLI SHALL fall back to static summary output (no progress bars)

### Requirement: Restore progress display
The CLI SHALL display progress during the restore download phase using Spectre.Console `Progress`. The display SHALL show: files restored from available chunks (determinate bar), and a summary on exit indicating how many files remain pending rehydration. The display SHALL be driven by `ProgressState` updated by restore notification handlers.

#### Scenario: Partial restore progress
- **WHEN** 500 files are available for immediate restore
- **THEN** the CLI SHALL show download progress and report remaining files pending rehydration on exit
