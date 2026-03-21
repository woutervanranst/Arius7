## ADDED Requirements

### Requirement: Encryption is optional
The system SHALL support an optional `--passphrase` parameter. When provided, all data stored in blob storage SHALL be encrypted. When omitted, all data SHALL be stored as plaintext (gzip-compressed where applicable).

#### Scenario: Archive without passphrase
- **WHEN** the user runs `arius archive` without `--passphrase`
- **THEN** all chunks, tree blobs, snapshot manifests, and chunk index shards SHALL be stored as plaintext (gzip-compressed where applicable, not encrypted)

#### Scenario: Archive with passphrase
- **WHEN** the user runs `arius archive` with `--passphrase`
- **THEN** all chunks, tree blobs, snapshot manifests, and chunk index shards SHALL be AES-256-CBC encrypted before upload

#### Scenario: Restore must use same passphrase as archive
- **WHEN** a repository was archived with a passphrase
- **THEN** restore and ls SHALL require the same passphrase to decrypt the data

#### Scenario: Restore plaintext repository without passphrase
- **WHEN** a repository was archived without a passphrase
- **THEN** restore and ls SHALL work without a passphrase

### Requirement: AES-256-CBC encryption compatible with openssl
When encryption is enabled, the system SHALL encrypt all data using AES-256-CBC with a format compatible with the openssl CLI tool.

#### Scenario: Encrypt a blob
- **WHEN** data is encrypted for storage (passphrase provided)
- **THEN** the output SHALL use the format: `Salted__<8-byte-salt><ciphertext>`, using PBKDF2 key derivation with SHA-256 and 10,000 iterations

#### Scenario: Decrypt with openssl CLI
- **WHEN** an encrypted blob is downloaded and decrypted using `openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:<passphrase>`
- **THEN** the original plaintext SHALL be recovered

### Requirement: All remote data encrypted when passphrase provided
When a passphrase is provided, the system SHALL encrypt every blob stored in blob storage: chunks, tree blobs, snapshot manifests, and chunk index shards.

#### Scenario: Tree blob encrypted
- **WHEN** a tree node is uploaded with a passphrase configured
- **THEN** its content SHALL be AES-256-CBC encrypted before upload

#### Scenario: Snapshot manifest encrypted
- **WHEN** a snapshot manifest is uploaded with a passphrase configured
- **THEN** its content SHALL be AES-256-CBC encrypted before upload

#### Scenario: Chunk index shard encrypted
- **WHEN** a chunk index shard is uploaded with a passphrase configured
- **THEN** its content SHALL be AES-256-CBC encrypted before upload

### Requirement: Backwards-compatible chunk encryption
The system SHALL use the same encryption format as the previous Arius version for chunks: `Salted__` prefix, PBKDF2 with SHA-256 and 10,000 iterations.

#### Scenario: Decrypt a chunk from previous Arius version
- **WHEN** a chunk encrypted by the previous Arius version is downloaded
- **THEN** the system SHALL decrypt it using the same PBKDF2 parameters and produce the original content

### Requirement: No structural leakage in blob names when encrypted
When a passphrase is provided, all blob names in storage SHALL be opaque hashes derived from passphrase-seeded SHA-256. An attacker with storage access but without the passphrase SHALL NOT be able to infer file names, directory structure, or relationships between blobs.

#### Scenario: Blob names reveal nothing when encrypted
- **WHEN** an attacker enumerates all blobs in a passphrase-protected container
- **THEN** they SHALL see only opaque hex hashes as blob names and encrypted content, with no information about file names, paths, or directory structure

#### Scenario: Same content different passphrase produces different hash
- **WHEN** the same file is archived with two different passphrases
- **THEN** the content hashes and blob names SHALL differ

### Requirement: Passphrase-seeded hashing when encrypted
When a passphrase is provided, all content hashes and tree hashes SHALL be computed as `SHA256(passphrase + data)`. When no passphrase is provided, hashes SHALL use plain `SHA256(data)`.

#### Scenario: Content hash seeded with passphrase
- **WHEN** a file is hashed with a passphrase configured
- **THEN** the hash SHALL be `SHA256(passphrase + file_bytes)`

#### Scenario: Content hash without passphrase
- **WHEN** a file is hashed without a passphrase configured
- **THEN** the hash SHALL be `SHA256(file_bytes)`

#### Scenario: Tree hash seeded with passphrase
- **WHEN** a tree node hash is computed with a passphrase configured
- **THEN** the hash SHALL be `SHA256(passphrase + serialized_tree_entries)`

#### Scenario: Tree hash without passphrase
- **WHEN** a tree node hash is computed without a passphrase configured
- **THEN** the hash SHALL be `SHA256(serialized_tree_entries)`

### Requirement: Pluggable encryption stream wrapper
The encryption layer SHALL be implemented as a pluggable stream wrapper. When a passphrase is present, streams are wrapped with encrypt/decrypt. When absent, streams pass through unmodified.

#### Scenario: Stream wrapper with passphrase
- **WHEN** a passphrase is configured and data is written to storage
- **THEN** the stream SHALL be wrapped with an encrypting stream before upload

#### Scenario: Stream wrapper without passphrase
- **WHEN** no passphrase is configured and data is written to storage
- **THEN** the stream SHALL pass through to storage without encryption
