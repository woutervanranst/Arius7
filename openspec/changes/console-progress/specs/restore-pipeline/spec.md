## MODIFIED Requirements

### Requirement: Idempotent restore
Restore SHALL be fully idempotent. Re-running the same restore command SHALL: skip files already restored correctly (hash match), download newly rehydrated chunks, re-request rehydration for still-pending chunks, and report remaining files. Each run is a self-contained scan-and-act cycle with no persistent local state. The restore pipeline SHALL publish `RestoreStartedEvent` with the total file count before beginning downloads, `FileRestoredEvent` after each file is written to disk, `FileSkippedEvent` for each file skipped due to hash match, and `RehydrationStartedEvent` when rehydration is kicked off.

#### Scenario: Partial restore re-run
- **WHEN** a restore previously restored 500 of 1000 files and rehydration has completed for 300 more chunks
- **THEN** re-running SHALL skip the 500 completed files, restore the 300 newly available, and report 200 still pending

#### Scenario: Full restore complete
- **WHEN** all files have been restored across multiple runs
- **THEN** the system SHALL report all files restored and prompt to clean up `chunks-rehydrated/`

#### Scenario: Progress events emitted during restore
- **WHEN** a restore operation begins
- **THEN** the system SHALL publish `RestoreStartedEvent(TotalFiles)` before downloading, and `FileRestoredEvent` / `FileSkippedEvent` for each file processed
