## MODIFIED Requirements

### Requirement: File enumeration
The system SHALL recursively enumerate all files in the local root directory through the Arius.Core filesystem domain boundary, producing FilePair units for archiving using a single-pass streaming approach. Files with the `.pointer.arius` suffix SHALL always be treated as pointer files. All other files SHALL be treated as binary files. If a file cannot be read (e.g., system-protected), the system SHALL log a warning and continue with the remaining files. Enumeration SHALL yield FilePair objects immediately as files are discovered without materializing the full file list into memory. When encountering a binary file, the system SHALL check for its pointer counterpart through relative path pointer derivation. When encountering a pointer file, the system SHALL check for its binary counterpart through relative path pointer derivation -- if the binary exists, skip (already emitted with the binary); if not, yield as pointer-only. No dictionaries or state tracking SHALL be used for pairing.

During enumeration, the system SHALL publish a `FileScannedEvent(string RelativePath, long FileSize)` for each file discovered. The `RelativePath` and `FileSize` SHALL be taken from the `FilePair` at the enumeration site. After enumeration completes, the system SHALL publish a `ScanCompleteEvent(long TotalFiles, long TotalBytes)` with the final counts.

#### Scenario: Binary file with matching pointer
- **WHEN** a binary file `photos/vacation.jpg` exists alongside `photos/vacation.jpg.pointer.arius`
- **THEN** the system SHALL produce a FilePair with both binary and pointer present, discovered through relative pointer path derivation

#### Scenario: Binary file without pointer
- **WHEN** a binary file `documents/report.pdf` exists with no corresponding `.pointer.arius` file
- **THEN** the system SHALL produce a FilePair with binary present and pointer absent

#### Scenario: Pointer file without binary (thin archive)
- **WHEN** a pointer file `music/song.mp3.pointer.arius` exists with no corresponding binary
- **THEN** the system SHALL produce a FilePair with pointer present and binary absent, using the hash from the pointer file

#### Scenario: Pointer file with binary already emitted
- **WHEN** a pointer file `photos/vacation.jpg.pointer.arius` is encountered and `photos/vacation.jpg` exists
- **THEN** the system SHALL skip the pointer file (it was already emitted as part of the binary's FilePair)

#### Scenario: Inaccessible file
- **WHEN** a file cannot be read due to permissions or system protection
- **THEN** the system SHALL log a warning with the file path and reason, skip the file, and continue enumeration

#### Scenario: Pointer file with invalid content
- **WHEN** a `.pointer.arius` file contains content that is not a valid hex hash
- **THEN** the system SHALL log a warning and treat the file as having no valid pointer

#### Scenario: No materialization of file list
- **WHEN** enumerating a directory with 1 million files
- **THEN** the pipeline SHALL begin processing the first FilePair before enumeration completes, with no `.ToList()` or equivalent materialization

#### Scenario: Per-file scanning event published
- **WHEN** a FilePair is discovered during enumeration
- **THEN** the system SHALL publish `FileScannedEvent` with the file's `RelativePath` and `FileSize` before writing the FilePair to the channel

#### Scenario: Scan complete event published
- **WHEN** all files have been enumerated and the channel is about to be completed
- **THEN** the system SHALL publish `ScanCompleteEvent` with the total file count and total bytes

## ADDED Requirements

### Requirement: Archive path collision validation
The archive pipeline SHALL validate discovered relative paths for ordinal case-insensitive collisions before publishing a snapshot.

#### Scenario: Case-insensitive collision discovered
- **WHEN** enumeration discovers `photos/pic.jpg` and `Photos/pic.jpg` in the same archive input
- **THEN** the archive SHALL fail before snapshot publication and report the colliding paths

#### Scenario: No case-insensitive collision
- **WHEN** enumeration discovers `photos/pic.jpg` and `photos/pic2.jpg`
- **THEN** the archive SHALL proceed past path collision validation
