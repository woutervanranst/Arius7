## MODIFIED Requirements

### Requirement: Streaming hash computation
The system SHALL compute content hashes by streaming file data through the hash function without loading the entire file into memory. The hash function SHALL be SHA256(data) in plaintext mode or SHA256(passphrase + data) in encrypted mode (literal byte concatenation). Pointer file hashes SHALL NEVER be trusted as a cache -- every binary file SHALL be re-hashed on every archive run. During hashing, the system SHALL publish `FileHashingEvent` with the file's relative path and file size (in bytes) to enable per-file progress display. A `ProgressStream` wrapper SHALL be used for large file hashing to report `IProgress<long>` with cumulative source bytes read.

#### Scenario: Large file hashing
- **WHEN** a 10 GB binary file is hashed
- **THEN** the system SHALL compute the hash using streaming with bounded memory (stream buffer only, no full file load)

#### Scenario: Binary exists with stale pointer
- **WHEN** a binary file has a pointer file whose hash does not match the computed binary hash
- **THEN** the system SHALL use the computed binary hash (not the pointer hash) and mark the pointer as stale for overwriting

#### Scenario: Pointer-only file (thin archive)
- **WHEN** only a pointer file exists (no binary)
- **THEN** the system SHALL use the hash from the pointer file without re-hashing

#### Scenario: FileHashingEvent emitted with size
- **WHEN** a file begins hashing
- **THEN** the system SHALL publish `FileHashingEvent` with `RelativePath` and `FileSize` to enable progress bar denominator calculation
