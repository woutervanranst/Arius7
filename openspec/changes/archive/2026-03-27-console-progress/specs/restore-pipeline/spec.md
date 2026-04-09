## REVISED Requirements (v2 — FileSize enrichment)

### Requirement: Idempotent restore with progress events
Restore SHALL be fully idempotent. Re-running the same restore command SHALL: skip files already restored correctly (hash match), download newly rehydrated chunks, re-request rehydration for still-pending chunks, and report remaining files. Each run is a self-contained scan-and-act cycle with no persistent local state. The restore pipeline SHALL publish notification events throughout:

- `RestoreStartedEvent(TotalFiles)` before beginning downloads
- `FileRestoredEvent(RelativePath, FileSize)` after each file is written to disk
- `FileSkippedEvent(RelativePath, FileSize)` for each file skipped due to hash match
- `RehydrationStartedEvent(ChunkCount, TotalBytes)` when rehydration is kicked off

`FileRestoredEvent` and `FileSkippedEvent` SHALL carry `long FileSize` (the file's uncompressed size in bytes) so the CLI can accumulate bytes-restored/skipped and show per-file sizes in the restore tail display.

#### Scenario: Partial restore re-run
- **WHEN** a restore previously restored 500 of 1000 files and rehydration has completed for 300 more chunks
- **THEN** re-running SHALL skip the 500 completed files, restore the 300 newly available, and report 200 still pending

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

No changes to `FileToRestore`, `FileTreeEntry`, or `IndexEntry` are needed.

#### Scenario: Skip site file size
- **WHEN** a file is skipped because its local hash matches the archive hash
- **THEN** `FileSkippedEvent` SHALL carry the size of the existing local file (obtained from the open `FileStream`)

#### Scenario: Large file restore site file size
- **WHEN** a large file is restored via `RestoreLargeFileAsync`
- **THEN** `FileRestoredEvent` SHALL carry `indexEntry.OriginalSize`

#### Scenario: Tar entry restore site file size
- **WHEN** a file is extracted from a tar bundle in `RestoreTarBundleAsync`
- **THEN** `FileRestoredEvent` SHALL carry `dataBuffer?.Length ?? 0` (0 for empty files)

### Requirement: ConfirmRehydration callback semantics for TCS coordination
The `ConfirmRehydration` callback on `RestoreOptions` (`Func<RestoreCostEstimate, CancellationToken, Task<RehydratePriority?>>?`) SHALL be invoked by the pipeline on the pipeline's worker thread when rehydration is needed. The callback blocks the pipeline until it returns. The CLI SHALL use this callback as a synchronization point via `TaskCompletionSource` pairs:

1. The CLI-provided callback receives the `RestoreCostEstimate` and sets a `TaskCompletionSource<RestoreCostEstimate>` result to signal to the CLI event loop that a question is pending.
2. The callback then awaits a `TaskCompletionSource<RehydratePriority?>` to receive the user's answer.
3. The CLI event loop detects the completed TCS, renders cost tables and selection prompt on a clean console, and sets the response TCS.

No changes to Core's pipeline logic or callback contract are needed. The TCS coordination is entirely within the CLI's callback implementation.

#### Scenario: Callback blocks pipeline until CLI responds
- **WHEN** the pipeline invokes `ConfirmRehydration` with a cost estimate
- **THEN** the pipeline thread SHALL block (await) until the CLI provides a `RehydratePriority?` via the response TCS

#### Scenario: CLI renders without pipeline interference
- **WHEN** the callback has signaled its TCS and is awaiting the response
- **THEN** no pipeline work proceeds, no progress events fire, and the CLI has exclusive access to the console

### Requirement: ConfirmCleanup callback semantics
The `ConfirmCleanup` callback on `RestoreOptions` (`Func<int, long, CancellationToken, Task<bool>>?`) SHALL follow the same TCS coordination pattern as `ConfirmRehydration`. The callback SHALL signal the CLI that a cleanup question is pending, and await the CLI's response. The CLI SHALL render the cleanup prompt on a clean console (after the download progress display has been auto-cleared).

#### Scenario: Cleanup prompt after download phase
- **WHEN** all downloads complete and rehydrated chunks exist
- **THEN** the pipeline invokes `ConfirmCleanup`, the CLI renders a confirmation prompt, and the pipeline awaits the response
