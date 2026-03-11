# chunking

## Purpose
Defines content-defined chunking using gear hash CDC, chunk size bounds, deduplication via content hashing, and the configurable chunker interface.

## Requirements

### Requirement: Content-defined chunking interface
The system SHALL define an `IChunker` interface that accepts a `Stream` and produces an `IAsyncEnumerable<Chunk>` where each `Chunk` contains the chunk data and its length.

#### Scenario: Interface contract
- **WHEN** a file stream is passed to `IChunker.ChunkAsync`
- **THEN** it yields a sequence of `Chunk` values covering the entire stream content with no gaps or overlaps

### Requirement: Gear hash CDC implementation
The default `IChunker` implementation SHALL use Gear hash content-defined chunking with a 256-entry lookup table of `uint64` values.

#### Scenario: Gear hash boundary detection
- **WHEN** the rolling gear hash `(hash << 1) + GearTable[byte]` masked with the average-size mask equals zero
- **THEN** a chunk boundary is emitted at that position

### Requirement: Chunk size bounds
The chunker SHALL enforce minimum, average, and maximum chunk sizes. No chunk SHALL be smaller than the minimum (except for end-of-stream). No chunk SHALL exceed the maximum. The mask SHALL be derived from the average size (nearest power of two minus one).

#### Scenario: Default chunk sizes
- **WHEN** a chunker is created with default parameters
- **THEN** minimum is 256 KB, average is 1 MB, maximum is 4 MB

#### Scenario: Small file handling
- **WHEN** a file smaller than the minimum chunk size is chunked
- **THEN** the entire file is yielded as a single chunk

#### Scenario: Maximum size enforcement
- **WHEN** no gear hash boundary is found within the maximum chunk size
- **THEN** a boundary is forced at the maximum chunk size

### Requirement: Deterministic gear table
The gear hash lookup table SHALL be generated from a seed value stored in the repository config. The same seed SHALL always produce the same table.

#### Scenario: Cross-machine reproducibility
- **WHEN** two machines use the same repository (same seed in config)
- **THEN** they produce identical chunk boundaries for identical input data

### Requirement: Deduplication via content hashing
Each chunk SHALL be SHA-256 hashed. If a chunk's hash already exists in the repository index, the chunk SHALL NOT be re-uploaded.

#### Scenario: Duplicate chunk skipped
- **WHEN** a chunk is produced whose SHA-256 hash matches an existing blob in the index
- **THEN** the chunk is not added to a pack file, and the existing blob reference is reused in the tree node

#### Scenario: Unique chunk stored
- **WHEN** a chunk is produced whose SHA-256 hash does not exist in the index
- **THEN** the chunk is added to the current pack file and a new index entry is created

### Requirement: Configurable chunk sizes at repo init
Chunk size parameters (min, avg, max) SHALL be configurable at repository initialization and stored in the `config` blob.

#### Scenario: Custom chunk sizes
- **WHEN** user runs `init` with `--chunk-min 128k --chunk-avg 512k --chunk-max 2m`
- **THEN** the config stores these values and all subsequent backups use them
