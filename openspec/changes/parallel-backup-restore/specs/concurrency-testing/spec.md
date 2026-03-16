## ADDED Requirements

### Requirement: Deterministic race tests
The test suite SHALL include TUnit tests that use `System.Threading.Barrier` to force multiple threads to hit critical dedup and channel operations simultaneously, asserting exact outcomes.

#### Scenario: ConcurrentDictionary TryAdd dedup gate
- **WHEN** 10 threads simultaneously call `TryAdd` on the same blob hash
- **THEN** exactly 1 thread SHALL succeed (return true)
- **AND** exactly 9 threads SHALL fail (return false)

#### Scenario: Duplicate blob across parallel file processors
- **WHEN** two files with identical content are processed by separate workers concurrently
- **THEN** the blob SHALL appear in exactly one pack
- **AND** the index SHALL contain exactly one entry for that blob hash

### Requirement: Stress tests with data integrity verification
The test suite SHALL include TUnit tests that back up 1000+ files with ~30% content overlap at high parallelism, then restore and verify every byte matches the original.

#### Scenario: Large-scale parallel backup and restore roundtrip
- **WHEN** 1000 files (with ~30% shared content) are backed up with `MaxFileProcessors = 8`
- **AND** the backup is restored with `MaxDownloaders = 4, MaxAssemblers = 8`
- **THEN** every restored file SHALL be byte-identical to the original
- **AND** `BackupCompleted.DeduplicatedChunks` SHALL be greater than 0
- **AND** `BackupCompleted.Failed` SHALL be 0

#### Scenario: Error collection under contention
- **WHEN** a backup includes files that become unreadable mid-operation (e.g., permission denied)
- **THEN** `BackupFileError` events SHALL be emitted for the failed files
- **AND** all other files SHALL be processed successfully
- **AND** `BackupCompleted.Failed` SHALL equal the number of unreadable files

### Requirement: Systematic interleaving exploration with Coyote
The test suite SHALL include a separate `Arius.Coyote.Tests` project targeting `net8.0` that uses Microsoft Coyote to systematically explore thread interleavings in the dedup gate, channel pipeline, and completion signaling.

#### Scenario: Coyote finds no bugs in dedup gate
- **WHEN** Coyote explores 1000 interleavings of 4 workers claiming the same blob hash
- **THEN** the `Specification.Assert` SHALL confirm exactly 1 blob reaches the packing channel in every interleaving

#### Scenario: Coyote finds no deadlocks in bounded channels
- **WHEN** Coyote explores 500 interleavings of a producer-consumer pipeline with bounded channels
- **THEN** no deadlock SHALL be detected within the scheduling step limit

### Requirement: Index uniqueness verification
After any parallel backup, the test suite SHALL verify that the merged index contains no duplicate blob hash entries (each blob maps to exactly one pack).

#### Scenario: Index integrity after parallel backup
- **WHEN** a parallel backup completes successfully
- **THEN** `repo.LoadIndexAsync()` SHALL return a dictionary where all blob hash keys are unique
- **AND** restoring any file using the index SHALL produce the correct output
