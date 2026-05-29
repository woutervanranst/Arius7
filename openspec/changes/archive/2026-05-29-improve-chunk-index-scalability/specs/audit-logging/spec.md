## MODIFIED Requirements

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

#### Scenario: Ls logs chunk-index lookup failure
- **WHEN** `ls` fails because chunk-index lookup detects corruption or interrupted repair state
- **THEN** the log file SHALL contain a detail log identifying the chunk-index failure and repair instruction
- **AND** it SHALL preserve the existing console behavior for user-facing output

### Requirement: Operation start and end markers
Each archive, restore, ls, and explicit chunk-index repair invocation SHALL use top-level operation lifecycle logs for start, done, and failure. Explicit chunk-index repair SHALL use `[repair]` lifecycle/detail logs and `[phase]` phase-entry markers for major repair stages such as marker setup, chunk scan, local shard rebuild, remote shard upload, stale shard deletion, and marker cleanup.

#### Scenario: Chunk-index repair lifecycle logged
- **WHEN** the explicit chunk-index repair command runs
- **THEN** the log file SHALL include `[repair]` start and done or failure lifecycle messages
- **AND** it SHALL include `[phase]` markers for major repair stages
- **AND** repair detail logs SHALL include useful payload such as listed chunk count, rebuilt shard count, uploaded shard count, and stale shard deletion count
