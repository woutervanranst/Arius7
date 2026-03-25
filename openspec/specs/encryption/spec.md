# Encryption Spec

## Purpose

Defines the pluggable encryption abstraction, AES-256-CBC format, streaming encryption, passphrase-seeded hashing, and backwards compatibility guarantees for Arius.

## Requirements

### Requirement: Pluggable encryption service
The system SHALL provide an `IEncryptionService` interface with two implementations: `PassphraseEncryptionService` (AES-256-CBC) and `PlaintextPassthroughService` (no-op). The implementation SHALL be selected at startup based on the `--passphrase` CLI parameter. A repository SHALL be either fully encrypted or fully plaintext — no mixing.

#### Scenario: Passphrase provided
- **WHEN** the `--passphrase` parameter is provided
- **THEN** the system SHALL use `PassphraseEncryptionService` for all encryption, decryption, and hashing operations

#### Scenario: No passphrase
- **WHEN** the `--passphrase` parameter is omitted
- **THEN** the system SHALL use `PlaintextPassthroughService` (passthrough streams, plain SHA256 hashing)

### Requirement: AES-256-CBC encryption format
The system SHALL encrypt using AES-256-CBC with openssl-compatible format: `Salted__` 8-byte prefix, random 8-byte salt, PBKDF2 key derivation with SHA-256 and 10,000 iterations. The output SHALL be decryptable by: `openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:<passphrase>`.

#### Scenario: Encrypt and decrypt roundtrip
- **WHEN** data is encrypted with a passphrase and then decrypted with the same passphrase
- **THEN** the output SHALL be byte-identical to the original

#### Scenario: OpenSSL compatibility
- **WHEN** the system encrypts data with passphrase "test123"
- **THEN** the openssl CLI command `openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:test123` SHALL successfully decrypt the output

#### Scenario: Salted prefix
- **WHEN** data is encrypted
- **THEN** the first 8 bytes of the encrypted output SHALL be the ASCII string `Salted__`

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
All encrypted blobs SHALL be recoverable using only openssl, gzip, and tar. For a large file: download blob → `openssl enc -d ...` → `gunzip` → original file. For a tar bundle: download blob → `openssl enc -d ...` → `gunzip` → `tar x` → find file by content-hash name.

#### Scenario: Manual recovery of large file
- **WHEN** the Arius software is unavailable and a user has the passphrase and content-hash
- **THEN** downloading `chunks/<hash>` and running `openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:<passphrase> | gunzip` SHALL produce the original file

#### Scenario: Manual recovery of tar-bundled file
- **WHEN** a user needs to recover a small file from a tar bundle
- **THEN** downloading the tar chunk, piping through `openssl enc -d ... | gunzip | tar x`, and finding the file named by its content-hash SHALL produce the original file

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
Encrypted filetree blobs SHALL be recoverable using only standard tools. For an encrypted filetree: download blob, pipe through `openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:<passphrase>`, pipe through `gunzip`, producing the plaintext tree blob text. For an unencrypted filetree: download blob, pipe through `gunzip`.

#### Scenario: Manual recovery of encrypted filetree
- **WHEN** the Arius software is unavailable and a user has the passphrase and tree hash
- **THEN** downloading `filetrees/<hash>` and running `openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:<passphrase> | gunzip` SHALL produce the plaintext tree blob text

#### Scenario: Manual recovery of unencrypted filetree
- **WHEN** the Arius software is unavailable and a user has the tree hash (no passphrase)
- **THEN** downloading `filetrees/<hash>` and running `gunzip` SHALL produce the plaintext tree blob text
