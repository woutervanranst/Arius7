## ADDED Requirements

### Requirement: ArGCM1 binary format

The system SHALL use the following binary format for AES-256-GCM encrypted blobs, identified by the 6-byte ASCII magic `ArGCM1`:

**Header (38 bytes)**:
| Offset | Size | Field |
|--------|------|-------|
| 0 | 6 | Magic: `ArGCM1` (ASCII) |
| 6 | 16 | Salt (random, for PBKDF2) |
| 22 | 4 | PBKDF2 iteration count (little-endian uint32) |
| 26 | 12 | Base nonce `nonce₀` (random) |

**Block (repeated)**:
| Offset | Size | Field |
|--------|------|-------|
| 0 | 4 | Plaintext length (little-endian uint32, max 65536) |
| 4 | length + 16 | Ciphertext + GCM authentication tag |

**Sentinel (final block)**:
| Offset | Size | Field |
|--------|------|-------|
| 0 | 4 | Length: `0x00000000` |
| 4 | 16 | GCM authentication tag (authenticates end-of-stream) |

#### Scenario: Header is 38 bytes with correct magic
- **WHEN** data is encrypted with AES-256-GCM
- **THEN** the first 6 bytes of the output SHALL be the ASCII string `ArGCM1`
- **AND** the total header size SHALL be exactly 38 bytes

#### Scenario: Salt is 16 bytes and random
- **WHEN** the same data is encrypted twice with the same passphrase
- **THEN** bytes 6–21 (the salt) SHALL differ between the two outputs

#### Scenario: Iteration count is stored in header
- **WHEN** data is encrypted with PBKDF2 iteration count 100,000
- **THEN** bytes 22–25 SHALL contain the little-endian uint32 value `0x000186A0`

#### Scenario: Base nonce is 12 bytes and random
- **WHEN** the same data is encrypted twice with the same passphrase
- **THEN** bytes 26–37 (the base nonce) SHALL differ between the two outputs

### Requirement: Chunked AEAD block encryption

The system SHALL split the compressed data into blocks of at most 65,536 bytes (64 KiB) and encrypt each block independently using AES-256-GCM. Each block SHALL be prefixed with a 4-byte little-endian uint32 indicating the plaintext length, followed by the ciphertext and 16-byte GCM authentication tag. A sentinel block with length 0 SHALL terminate the stream.

#### Scenario: Block size limit
- **WHEN** encrypting data larger than 64 KiB
- **THEN** each block's plaintext length field SHALL be at most 65,536

#### Scenario: Last data block may be smaller
- **WHEN** the remaining compressed data is less than 64 KiB
- **THEN** the final data block's plaintext length SHALL equal the remaining byte count

#### Scenario: Sentinel terminates stream
- **WHEN** all data blocks have been written
- **THEN** a sentinel block with length 0 and a 16-byte GCM tag SHALL be appended

#### Scenario: Authentication tag per block
- **WHEN** a single byte in a block's ciphertext is modified
- **THEN** decryption of that block SHALL fail with an authentication error

#### Scenario: Sentinel authentication
- **WHEN** the sentinel block is removed from the stream
- **THEN** decryption SHALL fail (stream truncation detected)

### Requirement: Nonce derivation from block index

The nonce for block `i` SHALL be derived as `nonce₀ XOR little_endian_bytes(i, 12)`, where `i` is the zero-based block index and the XOR operates over 12 bytes. The sentinel block uses the next sequential index after the last data block.

#### Scenario: First block uses base nonce
- **WHEN** encrypting the first data block (index 0)
- **THEN** the nonce SHALL equal `nonce₀ XOR 0` (i.e., `nonce₀` itself)

#### Scenario: Subsequent blocks use incremented nonces
- **WHEN** encrypting block at index 5
- **THEN** the nonce SHALL equal `nonce₀ XOR [5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]`

#### Scenario: Block reordering detected
- **WHEN** two encrypted blocks are swapped in the stream
- **THEN** decryption SHALL fail with an authentication error (wrong nonce for position)

### Requirement: PBKDF2-SHA256 key derivation for GCM

The system SHALL derive a 32-byte AES-256 key from the passphrase using PBKDF2-SHA256 with the salt and iteration count stored in the header. The initial iteration count SHALL be 100,000. Only the key is derived — the nonce is stored directly in the header.

#### Scenario: Key derivation uses header parameters
- **WHEN** decrypting an ArGCM1 blob
- **THEN** the system SHALL read the salt (16 bytes) and iteration count (uint32) from the header and pass them to PBKDF2-SHA256 to derive a 32-byte key

#### Scenario: Different iteration counts produce different keys
- **WHEN** the same passphrase and salt are used with iteration counts 100,000 and 200,000
- **THEN** the derived keys SHALL be different

#### Scenario: Default iteration count is 100,000
- **WHEN** encrypting new data
- **THEN** the PBKDF2 iteration count written to the header SHALL be 100,000

### Requirement: Streaming encryption with bounded memory

The AES-256-GCM encrypting and decrypting streams SHALL process data block-by-block without buffering the entire payload. Peak memory usage SHALL be bounded to approximately 2× the block size (128 KiB) regardless of total data size.

#### Scenario: Large file encryption memory bound
- **WHEN** encrypting a 4 GB stream
- **THEN** the encryption process SHALL NOT allocate more than 256 KiB of buffer memory for the encryption layer (excluding GZip and I/O buffers)

#### Scenario: Encrypting stream is write-only
- **WHEN** a caller writes data to the GCM encrypting stream
- **THEN** the stream SHALL buffer up to 64 KiB, encrypt the block, write the length + ciphertext + tag to the inner stream, and reset the buffer

#### Scenario: Decrypting stream is read-only
- **WHEN** a caller reads from the GCM decrypting stream
- **THEN** the stream SHALL read the next block (length + ciphertext + tag) from the inner stream, decrypt it, and serve bytes from the decrypted buffer

### Requirement: GCM content types

The system SHALL use `application/aes256gcm+gzip` as the content type for GCM-encrypted large chunks, filetree blobs, snapshot blobs, and chunk-index shards. The system SHALL use `application/aes256gcm+tar+gzip` for GCM-encrypted tar chunks.

#### Scenario: Large chunk content type
- **WHEN** a large chunk is uploaded with GCM encryption
- **THEN** the blob content type SHALL be `application/aes256gcm+gzip`

#### Scenario: Tar chunk content type
- **WHEN** a tar chunk is uploaded with GCM encryption
- **THEN** the blob content type SHALL be `application/aes256gcm+tar+gzip`

#### Scenario: Non-chunk blob content types
- **WHEN** a filetree, snapshot, or chunk-index shard is uploaded with GCM encryption
- **THEN** the blob content type SHALL be `application/aes256gcm+gzip`

### Requirement: Pure bash recovery script

A bash script (`recover-chunk.sh`) SHALL be provided that can decrypt any AES-256-GCM encrypted chunk given the chunk file and passphrase. The script SHALL depend only on `openssl` (3.x), `dd`, `xxd`, and `gunzip`. The script SHALL verify the OpenSSL version at startup and fail with a clear error if below 3.0.

#### Scenario: Recover a GCM-encrypted large chunk
- **WHEN** `recover-chunk.sh <chunk-file> <passphrase>` is run against a GCM-encrypted large chunk
- **THEN** the script SHALL output the original uncompressed file content to stdout

#### Scenario: Recover a GCM-encrypted tar chunk
- **WHEN** `recover-chunk.sh <chunk-file> <passphrase>` is run against a GCM-encrypted tar chunk
- **THEN** the script SHALL output the uncompressed tar stream to stdout (pipeable to `tar x`)

#### Scenario: OpenSSL version check
- **WHEN** `recover-chunk.sh` is run with OpenSSL < 3.0
- **THEN** the script SHALL exit with a non-zero code and print an error message indicating OpenSSL 3.x is required

#### Scenario: Wrong passphrase
- **WHEN** `recover-chunk.sh` is run with an incorrect passphrase
- **THEN** the script SHALL fail with a non-zero exit code (GCM authentication failure on the first block)

### Requirement: Recovery script tested in CI/CD

The bash recovery script SHALL be tested in the GitHub Actions release pipeline (`release.yml`). The test SHALL create a GCM-encrypted chunk using the Arius binary, then run `recover-chunk.sh` to decrypt it, and verify the output matches the original file by comparing SHA256 hashes.

#### Scenario: Recovery script roundtrip in CI
- **WHEN** the release pipeline runs
- **THEN** a job SHALL encrypt a known file, recover it with `recover-chunk.sh`, and assert the SHA256 of the recovered output matches the SHA256 of the original file

#### Scenario: Recovery script test runs on ubuntu-latest
- **WHEN** the recovery script test job runs
- **THEN** it SHALL execute on `ubuntu-latest` which provides OpenSSL 3.x
