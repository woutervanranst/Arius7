# Filesystem Domain Types Spec

## Purpose

Defines Arius.Core's internal filesystem domain model for validated relative paths, rooted local filesystem access, archive-time local file state, and path-boundary behavior.

## Requirements

### Requirement: Slash-normalized relative paths
Arius.Core SHALL use an internal `RelativePath` value object to represent canonical slash-normalized relative paths for archive domain paths and other logical paths that are relative and use `/` separators.

#### Scenario: Valid relative path
- **WHEN** Arius.Core parses `photos/2024/pic.jpg` as a relative path
- **THEN** the parsed path SHALL render as `photos/2024/pic.jpg`

#### Scenario: Root relative path
- **WHEN** Arius.Core needs to represent the root of a relative path space
- **THEN** it SHALL use `RelativePath.Root`

#### Scenario: Invalid rooted path
- **WHEN** Arius.Core parses `/photos/pic.jpg` as a relative path
- **THEN** parsing SHALL fail before the value is used by archive, list, restore, filetree, blob, or cache logic

#### Scenario: Invalid dot segment
- **WHEN** Arius.Core parses `photos/../pic.jpg` as a relative path
- **THEN** parsing SHALL fail before the value is used by archive, list, restore, filetree, blob, or cache logic

### Requirement: Segment composition developer experience
`RelativePath` SHALL support appending a single validated path segment with the `/` operator using either a path segment value or a string segment.

#### Scenario: Compose path from string segments
- **WHEN** Core code evaluates `RelativePath.Root / "photos" / "pic.jpg"`
- **THEN** the resulting relative path SHALL render as `photos/pic.jpg`

#### Scenario: Reject multi-segment string append
- **WHEN** Core code evaluates `RelativePath.Root / "photos/pic.jpg"`
- **THEN** the append operation SHALL fail because the right side is not one path segment

#### Scenario: Reject unsafe string append
- **WHEN** Core code evaluates `RelativePath.Root / ".."`
- **THEN** the append operation SHALL fail because the right side is not a canonical path segment

### Requirement: Segment-aware relative path operations
`RelativePath` SHALL provide segment-aware name, parent, segment enumeration, and prefix operations so Core code does not perform path traversal by raw string prefix checks.

#### Scenario: Prefix matches complete segment
- **WHEN** Arius.Core checks whether `photos/2024/pic.jpg` starts with prefix `photos`
- **THEN** the check SHALL return true

#### Scenario: Prefix does not match partial segment
- **WHEN** Arius.Core checks whether `photoshop/pic.jpg` starts with prefix `photos`
- **THEN** the check SHALL return false

### Requirement: Pointer path derivation
Arius.Core SHALL centralize pointer-file path behavior so `.pointer.arius` suffix handling is not repeated in feature handlers or shared services.

#### Scenario: Derive pointer path
- **WHEN** Arius.Core derives the pointer path for `photos/pic.jpg`
- **THEN** the result SHALL be `photos/pic.jpg.pointer.arius`

#### Scenario: Derive binary path from pointer path
- **WHEN** Arius.Core derives the binary path for `photos/pic.jpg.pointer.arius`
- **THEN** the result SHALL be `photos/pic.jpg`

#### Scenario: Reject binary derivation from non-pointer path
- **WHEN** Arius.Core derives a binary path from `photos/pic.jpg`
- **THEN** the operation SHALL fail because the path is not a pointer-file path

### Requirement: Archive-time file model
Arius.Core SHALL model local archive-time file state with internal `BinaryFile`, `PointerFile`, and `FilePair` types. These types SHALL carry relative domain paths and file metadata, and SHALL NOT carry host full-path strings.

#### Scenario: Binary-only file pair
- **WHEN** a binary file exists with no corresponding pointer file
- **THEN** enumeration SHALL produce a file pair whose path is the binary relative path, whose binary value is present, and whose pointer value is absent

#### Scenario: Pointer-only file pair
- **WHEN** a pointer file exists with no corresponding binary file
- **THEN** enumeration SHALL produce a file pair whose path is the binary relative path, whose pointer value is present, and whose binary value is absent

#### Scenario: Binary and pointer file pair
- **WHEN** a binary file exists alongside its corresponding pointer file
- **THEN** enumeration SHALL produce one file pair whose path is the binary relative path and whose binary and pointer values are both present

### Requirement: Relative filesystem boundary
Arius.Core SHALL centralize local filesystem access for archive, list, restore, and cache operations behind a concrete internal `RelativeFileSystem` rooted at a local directory.

#### Scenario: Enumerate local files by relative path
- **WHEN** Core code enumerates a rooted local directory through `RelativeFileSystem.EnumerateFiles()`
- **THEN** every returned file entry SHALL include a validated `RelativePath` produced by stripping the configured root

#### Scenario: Open local file by relative path
- **WHEN** Core code opens a file through `RelativeFileSystem` using a `RelativePath`
- **THEN** the filesystem boundary SHALL resolve the host path under the configured root and perform the `System.IO` call internally

#### Scenario: Prevent root escape
- **WHEN** a relative path would resolve outside the configured local root
- **THEN** the filesystem boundary SHALL reject the operation before touching the filesystem

### Requirement: System IO quarantine
Archive, list, restore, filetree, blob-name, and cache-path feature code SHALL NOT call local filesystem `File.*`, `Directory.*`, or `Path.*` APIs directly when handling Arius path-domain work. Such calls SHALL be limited to the filesystem boundary or clearly non-domain infrastructure that cannot be represented by `RelativePath`.

#### Scenario: Feature code reads pointer file
- **WHEN** archive or list logic needs to read pointer-file text
- **THEN** it SHALL call the filesystem boundary with a `RelativePath` instead of calling `File.ReadAllText` directly

#### Scenario: Restore writes file
- **WHEN** restore logic materializes a file under the restore root
- **THEN** it SHALL call the filesystem boundary with a `RelativePath` instead of constructing a full path string in the feature handler

### Requirement: Cross-OS case collision rejection
Arius.Core SHALL preserve ordinal relative path identity internally, but SHALL reject a repository state with two relative paths that collide under ordinal case-insensitive comparison before publishing a snapshot.

#### Scenario: Linux-only casing conflict
- **WHEN** an archive input contains both `photos/pic.jpg` and `Photos/pic.jpg`
- **THEN** the archive SHALL fail before snapshot publication with an error that identifies the colliding paths

#### Scenario: Distinct non-colliding paths
- **WHEN** an archive input contains `photos/pic.jpg` and `photos/pic2.jpg`
- **THEN** the archive SHALL proceed past case-collision validation

### Requirement: Boundary string contracts
Public Arius.Core command, query, result, and event contracts SHALL remain string-based for path values unless a separate change explicitly modifies those contracts.

#### Scenario: Parse command path at boundary
- **WHEN** a public command option supplies a root, prefix, or target path string
- **THEN** the handler SHALL parse it into filesystem domain types near the start of the operation

#### Scenario: Return public listing path
- **WHEN** list query returns a repository entry
- **THEN** the public result SHALL expose the path as the existing string contract produced from the internal relative path

### Requirement: Restore-time relative path model
Restore-time file candidates SHALL be modeled separately from archive-time `BinaryFile`, `PointerFile`, and `FilePair` objects, while still using `RelativePath` for repository location and pointer path derivation.

#### Scenario: Restore candidate from filetree entry
- **WHEN** restore traversal finds a filetree file entry
- **THEN** it SHALL create a restore candidate with a relative path, content hash, timestamps, and restore metadata rather than an archive-time file pair

#### Scenario: Restore pointer file creation
- **WHEN** restore writes a pointer file for a restored binary file
- **THEN** it SHALL derive the pointer-file relative path from the restore candidate's relative path using the centralized pointer path behavior
