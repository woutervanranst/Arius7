## ADDED Requirements

### Requirement: Parallel backup pipeline
The backup system SHALL process files through a multi-stage Channel-based producer-consumer pipeline with configurable concurrency per stage. The stages SHALL be: file processors (N workers: chunk + hash + dedup) → pack accumulator (1 consumer: collect blobs until pack threshold) → seal workers (M workers: TAR + gzip + AES-256-CBC) → uploaders (P workers: upload to Azure).

#### Scenario: Multiple files processed concurrently
- **WHEN** a backup is started with multiple files and `MaxFileProcessors > 1`
- **THEN** the system SHALL process up to `MaxFileProcessors` files concurrently, each chunking, hashing, and dedup-checking independently

#### Scenario: Pack sealing overlaps with file processing
- **WHEN** the pack accumulator reaches the pack size threshold
- **THEN** the sealed batch SHALL be handed to a seal worker while file processing and accumulation continue without blocking

#### Scenario: Upload overlaps with sealing
- **WHEN** a seal worker produces a `SealedPack`
- **THEN** the pack SHALL be handed to an uploader while sealing of the next batch continues without blocking

### Requirement: Atomic dedup under concurrency
The system SHALL use `ConcurrentDictionary<string, byte>.TryAdd()` as an atomic claim operation for blob dedup. Exactly one worker SHALL succeed in claiming a given blob hash; all other workers encountering the same hash SHALL skip packing it.

#### Scenario: Same blob produced by two files concurrently
- **WHEN** two file processor workers produce chunks with identical HMAC-SHA256 hashes
- **THEN** exactly one worker SHALL send the blob to the packing channel and the other SHALL skip it
- **AND** the dedup counter SHALL be incremented for the skipped occurrence

#### Scenario: Blob already in persistent index
- **WHEN** a file processor produces a chunk whose hash exists in the read-only `existingIndex`
- **THEN** the worker SHALL skip packing without attempting a `TryAdd` claim
- **AND** the dedup counter SHALL be incremented

### Requirement: Bounded memory via channel backpressure
All inter-stage channels SHALL be bounded. When a downstream stage is slower than upstream, the bounded channel SHALL block upstream producers via `WriteAsync` until capacity is available. This SHALL prevent unbounded memory growth.

#### Scenario: Uploaders slower than sealers
- **WHEN** the upload channel is full because all uploaders are busy
- **THEN** seal workers SHALL block on `WriteAsync` until an uploader completes and frees capacity
- **AND** this backpressure SHALL propagate upstream through the accumulator to file processors

### Requirement: Configurable parallelism
The system SHALL expose a `ParallelismOptions` record with per-stage concurrency limits. Default values SHALL be: `MaxFileProcessors = Environment.ProcessorCount`, `MaxSealWorkers = max(1, ProcessorCount / 2)`, `MaxUploaders = 4`, `MaxDownloaders = 4`, `MaxAssemblers = Environment.ProcessorCount`. A value of `0` SHALL mean "use the default".

#### Scenario: Custom parallelism via request
- **WHEN** a `BackupRequest` or `RestoreRequest` includes a non-null `ParallelismOptions`
- **THEN** the pipeline SHALL use the specified concurrency limits for each stage

#### Scenario: Default parallelism
- **WHEN** the `Parallelism` property is null on the request
- **THEN** the pipeline SHALL use `ParallelismOptions.Default`

### Requirement: Event delivery via channel
All pipeline workers SHALL write events to a shared `Channel<TEvent>`. The `Handle` method SHALL read from this channel and `yield return` each event to the consumer. Event ordering is NOT guaranteed — consumers are responsible for sorting if needed.

#### Scenario: Events from parallel workers
- **WHEN** multiple file processors complete files concurrently
- **THEN** `BackupFileProcessed` events SHALL be delivered to the consumer in an arbitrary order
- **AND** all events SHALL be delivered (no loss)

### Requirement: Collect-and-report error handling
Individual file processing failures SHALL NOT cancel the overall operation. The system SHALL catch per-file exceptions, emit an error event, and continue processing remaining files. The completion event SHALL include a failure count.

#### Scenario: One file fails during backup
- **WHEN** a file processor encounters an I/O error on one file
- **THEN** a `BackupFileError` event SHALL be emitted for that file
- **AND** processing of other files SHALL continue
- **AND** `BackupCompleted.Failed` SHALL be `1`

#### Scenario: All files fail
- **WHEN** every file fails during backup
- **THEN** a `BackupFileError` event SHALL be emitted for each file
- **AND** `BackupCompleted` SHALL have `Snapshot = null` and `Failed = totalFiles`

### Requirement: Chunk-level dedup statistics
The `BackupCompleted` event SHALL include chunk-level counters: `TotalChunks`, `NewChunks`, `DeduplicatedChunks`, `TotalBytes`, `NewBytes`, `DeduplicatedBytes`. These SHALL be tracked with `Interlocked` operations across all workers.

#### Scenario: Mixed new and deduplicated chunks
- **WHEN** a backup processes files containing both new and previously-seen chunks
- **THEN** `TotalChunks` SHALL equal `NewChunks + DeduplicatedChunks`
- **AND** `TotalBytes` SHALL equal `NewBytes + DeduplicatedBytes`
- **AND** `DeduplicatedChunks` SHALL include chunks found in `existingIndex` and chunks claimed by another worker in this run

### Requirement: Parallel index loading
`AzureRepository.LoadIndexAsync` SHALL download and parse all `index/` blobs concurrently using `Parallel.ForEachAsync` with a bounded degree of parallelism (default 8). The results SHALL be merged into a single `Dictionary<string, IndexEntry>`.

#### Scenario: Repository with multiple index blobs
- **WHEN** `LoadIndexAsync` is called on a repository with 10 index blobs
- **THEN** the system SHALL download all 10 blobs concurrently (up to the parallelism limit)
- **AND** the merged dictionary SHALL contain all entries from all blobs

### Requirement: Failed upload self-healing
When a pack upload fails, the index entries for that pack SHALL NOT be written to Azure. On a subsequent backup run, the affected blobs SHALL be detected as missing from `existingIndex` and SHALL be re-packed and re-uploaded.

#### Scenario: Pack upload fails then re-run succeeds
- **WHEN** a pack upload fails during backup run 1
- **AND** a second backup run is performed
- **THEN** the blobs from the failed pack SHALL be re-claimed, re-packed, and re-uploaded in run 2
