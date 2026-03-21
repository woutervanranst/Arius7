## ADDED Requirements

### Requirement: Channel-based archive pipeline
The system SHALL use a multi-stage pipeline with bounded Channels for archive operations: enumerate → hash → decide → upload → finalize.

#### Scenario: Pipeline stages connected by channels
- **WHEN** an archive operation runs
- **THEN** each stage SHALL produce items into a Channel consumed by the next stage, with bounded capacity for backpressure

#### Scenario: Parallel hashing
- **WHEN** files are queued for hashing
- **THEN** N worker tasks (N = processor count) SHALL consume from the hash channel concurrently

#### Scenario: Parallel uploading
- **WHEN** chunks are queued for upload
- **THEN** M worker tasks SHALL upload chunks concurrently

### Requirement: Channel-based restore pipeline
The system SHALL use a multi-stage pipeline with bounded Channels for restore operations: resolve → rehydrate → download → decrypt/extract → write.

#### Scenario: Pipeline stages for restore
- **WHEN** a restore operation runs
- **THEN** each stage SHALL produce items into a Channel consumed by the next stage

#### Scenario: Parallel downloading
- **WHEN** rehydrated chunks are available for download
- **THEN** N worker tasks SHALL download chunks concurrently

### Requirement: Chunk upload deduplication
The system SHALL prevent concurrent upload of the same chunk by multiple pipeline threads.

#### Scenario: Two files with same hash processed concurrently
- **WHEN** two workers attempt to upload a chunk with the same content hash simultaneously
- **THEN** only one worker SHALL perform the upload and the other SHALL await the result of the first

### Requirement: Rehydration request deduplication
The system SHALL prevent concurrent rehydration requests for the same chunk.

#### Scenario: Two files needing same tar bundle rehydrated
- **WHEN** two files in the same tar bundle are queued for restore
- **THEN** only one rehydration request SHALL be submitted and both SHALL wait for that single request to complete

### Requirement: Tar buffer serialization
The system SHALL serialize access to the tar buffer, ensuring files are added sequentially while uploads proceed in parallel.

#### Scenario: Sequential tar buffer writes
- **WHEN** multiple small files are processed concurrently
- **THEN** they SHALL be added to the tar buffer one at a time (via lock or single-writer pattern), and sealed tar uploads SHALL proceed in parallel with continued tar filling

### Requirement: Batch rehydration polling
The system SHALL poll rehydration status in batches rather than individually.

#### Scenario: Batch status check
- **WHEN** 1,000 chunks are pending rehydration
- **THEN** the system SHALL check their status in batches (e.g., list blobs in `chunks-rehydrated/`) rather than issuing 1,000 individual HEAD requests

### Requirement: Streaming enumeration
The system SHALL use `IAsyncEnumerable` for filesystem enumeration, blob listing, and tree traversal to avoid loading entire collections into memory.

#### Scenario: Large directory enumeration
- **WHEN** a directory contains millions of files
- **THEN** the system SHALL enumerate files as a stream, not load all file info into memory at once

### Requirement: Bounded channel capacity
Pipeline channels SHALL have bounded capacity to prevent unbounded memory growth when producers outpace consumers.

#### Scenario: Backpressure when upload is slow
- **WHEN** the hash stage produces results faster than the upload stage can consume them
- **THEN** the hash stage SHALL block (await) until channel capacity is available, preventing unbounded memory growth

### Requirement: Streaming progress events
The system SHALL produce streaming progress events (`IAsyncEnumerable<ProgressEvent>`) that the CLI can display in real time.

#### Scenario: Progress event for file hashed
- **WHEN** a file is hashed during archive
- **THEN** a progress event SHALL be emitted with the file path, hash, and status (new/unchanged/updated)

#### Scenario: Progress event for upload completed
- **WHEN** a chunk upload completes
- **THEN** a progress event SHALL be emitted with the hash, size, and type (chunk/tar/tree)

#### Scenario: Progress event for warning
- **WHEN** a file or directory is skipped due to an error
- **THEN** a warning progress event SHALL be emitted with the path and reason
