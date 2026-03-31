# Encryption Spec

## Purpose

Defines the pluggable encryption abstraction, AES-256-GCM (default) and AES-256-CBC (legacy read-only) formats, auto-detection on read, streaming encryption, passphrase-seeded hashing, and backwards compatibility guarantees for Arius.

## Requirements

### Requirement: Pluggable encryption service
The system SHALL provide an `IEncryptionService` interface with two implementations: `PassphraseEncryptionService` (AES-256-GCM default, AES-256-CBC legacy read support) and `PlaintextPassthroughService` (no-op). The implementation SHALL be selected at startup based on the `--passphrase` CLI parameter. A repository SHALL be either fully encrypted or fully plaintext — no mixing.

#### Scenario: Passphrase provided
- **WHEN** the `--passphrase` parameter is provided
- **THEN** the system SHALL use `PassphraseEncryptionService` which writes AES-256-GCM and reads both GCM and CBC

#### Scenario: No passphrase
- **WHEN** the `--passphrase` parameter is omitted
- **THEN** the system SHALL use `PlaintextPassthroughService` (passthrough streams, plain SHA256 hashing)

### Requirement: AES-256-CBC encryption format
The system SHALL retain full support for reading AES-256-CBC encrypted blobs in openssl-compatible format: `Salted__` 8-byte prefix, random 8-byte salt, PBKDF2 key derivation with SHA-256 and 10,000 iterations. The output SHALL remain decryptable by: `openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:<passphrase>`. The system SHALL NOT write new blobs in CBC format — it is read-only for backwards compatibility.

#### Scenario: Decrypt existing CBC blob
- **WHEN** a blob with `Salted__` magic prefix is decrypted
- **THEN** the system SHALL use AES-256-CBC decryption with PBKDF2-SHA256 at 10,000 iterations

#### Scenario: CBC is read-only
- **WHEN** new data is encrypted with a passphrase
- **THEN** the system SHALL use AES-256-GCM (ArGCM1 format), not AES-256-CBC

#### Scenario: OpenSSL compatibility for legacy blobs
- **WHEN** an existing CBC-encrypted blob is downloaded
- **THEN** the openssl CLI command `openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:<passphrase>` SHALL successfully decrypt it

### Requirement: Auto-detection of encryption scheme on read
The system SHALL auto-detect the encryption scheme by inspecting the first bytes of the stream during decryption. The `WrapForDecryption` method SHALL peek at the magic bytes and select the appropriate decrypting stream without requiring caller changes.

#### Scenario: GCM blob detected
- **WHEN** `WrapForDecryption` is called on a stream whose first 6 bytes are `ArGCM1`
- **THEN** the system SHALL return an AES-256-GCM decrypting stream

#### Scenario: CBC blob detected
- **WHEN** `WrapForDecryption` is called on a stream whose first 8 bytes are `Salted__`
- **THEN** the system SHALL return an AES-256-CBC decrypting stream

#### Scenario: Unknown format rejected
- **WHEN** `WrapForDecryption` is called on a stream with unrecognized magic bytes
- **THEN** the system SHALL throw an `InvalidDataException`

#### Scenario: No caller changes required
- **WHEN** existing code calls `_encryption.WrapForDecryption(stream)`
- **THEN** the call SHALL work for both GCM and CBC blobs without any code changes at the call site

### Requirement: Mixed-scheme archives
An archive SHALL support blobs encrypted with different schemes. After this change, the `chunks/` prefix MAY contain both CBC-encrypted and GCM-encrypted blobs. The read path SHALL handle both transparently based on auto-detection.

#### Scenario: Archive with CBC and GCM chunks
- **WHEN** restoring from an archive that contains both CBC-encrypted chunks (from previous writes) and GCM-encrypted chunks (from new writes)
- **THEN** the system SHALL decrypt each chunk correctly based on its format

#### Scenario: Re-archiving does not re-encrypt existing chunks
- **WHEN** archiving files that already have chunks in the archive (dedup match)
- **THEN** the existing chunks SHALL remain in their original format (CBC or GCM) — no re-encryption

### Requirement: Streaming encryption
The system SHALL provide stream wrappers for encryption and decryption. The wrappers SHALL process data in chunks without buffering the entire content. Files of any size (including 10 GB+) SHALL be encrypted/decrypted with bounded memory usage.

#### Scenario: Large file encryption
- **WHEN** a 10 GB file is encrypted
- **THEN** the system SHALL stream the encryption with bounded memory (no full file buffering)

#### Scenario: Stream composition
- **WHEN** archiving a large file
- **THEN** the pipeline read → gzip → encrypt → upload SHALL work as composed streams

### Requirement: Passphrase-seeded hashing
The system SHALL compute content hashes as `SHA256(passphrase_bytes + data_bytes)` when a passphrase is provided (literal byte concatenation, not HMAC). Without a passphrase, hashes SHALL be plain `SHA256(data_bytes)`. This construction is locked for backwards compatibility with the previous Arius version.

#### Scenario: Encrypted hash computation
- **WHEN** computing the hash of file data with passphrase "mypass"
- **THEN** the hash SHALL be SHA256(UTF8("mypass") + file_bytes)

#### Scenario: Plaintext hash computation
- **WHEN** computing the hash of file data without a passphrase
- **THEN** the hash SHALL be SHA256(file_bytes)

#### Scenario: Same file different passphrase
- **WHEN** the same file is hashed with passphrase "a" and passphrase "b"
- **THEN** the resulting hashes SHALL be different

### Requirement: Passphrase-seeded blob names
When a passphrase is provided, all blob names (chunk hashes, tree hashes) SHALL be passphrase-seeded. This ensures blob names are opaque and do not leak file names or directory structure.

#### Scenario: Encrypted blob names are opaque
- **WHEN** archiving with a passphrase
- **THEN** blob names in `chunks/`, `filetrees/`, and `snapshots/` SHALL be passphrase-seeded hashes with no correlation to file names or paths

#### Scenario: Plaintext blob names are content hashes
- **WHEN** archiving without a passphrase
- **THEN** blob names SHALL be plain SHA256 content hashes

### Requirement: Backwards compatibility with previous Arius
The encryption format and hash construction SHALL be backwards compatible with chunks produced by the previous Arius version. Existing encrypted chunks in archive storage SHALL be decryptable by the new system. The hash construction `SHA256(passphrase + data)` SHALL NOT be changed.

#### Scenario: Decrypt previous Arius chunk
- **WHEN** an encrypted chunk from the previous Arius version is downloaded
- **THEN** the system SHALL decrypt it successfully using the same passphrase

#### Scenario: Hash compatibility
- **WHEN** computing a content hash with the same passphrase and data as the previous Arius version
- **THEN** the hash SHALL be identical

### Requirement: Worst-case recovery with standard tools
All encrypted blobs SHALL be recoverable using only standard tools. For CBC blobs: `openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:<passphrase> | gunzip`. For GCM blobs: `recover-chunk.py <chunk-file> <passphrase>` (the script decrypts and decompresses internally; for tar chunks the output is pipeable to `tar x`).

#### Scenario: Manual recovery of CBC-encrypted large file
- **WHEN** the Arius software is unavailable and a user has the passphrase and content-hash
- **THEN** downloading `chunks/<hash>` and running `openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:<passphrase> | gunzip` SHALL produce the original file

#### Scenario: Manual recovery of GCM-encrypted large file
- **WHEN** the Arius software is unavailable and a user has the passphrase and content-hash
- **THEN** downloading `chunks/<hash>` and running `recover-chunk.py <chunk-file> <passphrase>` SHALL produce the original file

### Requirement: Filetree blob body encryption
When a passphrase is provided, filetree blob bodies SHALL be gzip-compressed and encrypted (AES-256-CBC) before upload to Azure Blob Storage, using the same `IEncryptionService.WrapForEncryption` pipeline as chunks, snapshots, and chunk index shards. Without a passphrase, filetree blob bodies SHALL be gzip-compressed only (no encryption). The local disk cache SHALL store filetree blobs in plaintext (no compression or encryption).

#### Scenario: Filetree encrypted when passphrase provided
- **WHEN** archiving with `--passphrase` and a filetree blob is uploaded to Azure
- **THEN** the blob body SHALL be gzip-compressed and AES-256-CBC encrypted (not plaintext)

#### Scenario: Filetree compressed but not encrypted without passphrase
- **WHEN** archiving without `--passphrase` and a filetree blob is uploaded to Azure
- **THEN** the blob body SHALL be gzip-compressed but not encrypted

#### Scenario: Filetree roundtrip through encryption
- **WHEN** a filetree blob is uploaded with a passphrase and then downloaded and deserialized with the same passphrase
- **THEN** the deserialized tree entries SHALL be identical to the original

#### Scenario: Filetree disk cache remains plaintext
- **WHEN** a filetree blob is written to the local disk cache at `~/.arius/{account}-{container}/filetrees/`
- **THEN** the cached file SHALL be plaintext UTF-8 text (no compression or encryption)

### Requirement: Worst-case recovery of filetree blobs
Encrypted filetree blobs SHALL be recoverable using only standard tools. For CBC-encrypted filetrees: download blob, pipe through `openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:<passphrase>`, pipe through `gunzip`, producing the plaintext tree blob text. For GCM-encrypted filetrees: download blob, run `recover-chunk.py <blob-file> <passphrase>` (the script decrypts and decompresses internally), producing the plaintext tree blob text. For an unencrypted filetree: download blob, pipe through `gunzip`.

#### Scenario: Manual recovery of encrypted filetree
- **WHEN** the Arius software is unavailable and a user has the passphrase and tree hash
- **THEN** downloading `filetrees/<hash>` and running `openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:<passphrase> | gunzip` SHALL produce the plaintext tree blob text

#### Scenario: Manual recovery of unencrypted filetree
- **WHEN** the Arius software is unavailable and a user has the tree hash (no passphrase)
- **THEN** downloading `filetrees/<hash>` and running `gunzip` SHALL produce the plaintext tree blob text
