## ADDED Requirements

### Requirement: AES-256-CBC encryption compatible with openssl
The system SHALL encrypt all data using AES-256-CBC with a format compatible with the openssl CLI tool.

#### Scenario: Encrypt a blob
- **WHEN** data is encrypted for storage
- **THEN** the output SHALL use the format: `Salted__<8-byte-salt><ciphertext>`, using PBKDF2 key derivation with SHA-256 and 10,000 iterations

#### Scenario: Decrypt with openssl CLI
- **WHEN** an encrypted blob is downloaded and decrypted using `openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:<passphrase>`
- **THEN** the original plaintext SHALL be recovered

### Requirement: All remote data encrypted
The system SHALL encrypt every blob stored in blob storage: chunks, tree blobs, snapshot manifests, and chunk index shards. No plaintext data SHALL exist in blob storage.

#### Scenario: Tree blob encrypted
- **WHEN** a tree node is uploaded
- **THEN** its content SHALL be AES-256-CBC encrypted before upload

#### Scenario: Snapshot manifest encrypted
- **WHEN** a snapshot manifest is uploaded
- **THEN** its content SHALL be AES-256-CBC encrypted before upload

#### Scenario: Chunk index shard encrypted
- **WHEN** a chunk index shard is uploaded
- **THEN** its content SHALL be AES-256-CBC encrypted before upload

### Requirement: Backwards-compatible chunk encryption
The system SHALL use the same encryption format as the previous Arius version for chunks: `Salted__` prefix, PBKDF2 with SHA-256 and 10,000 iterations.

#### Scenario: Decrypt a chunk from previous Arius version
- **WHEN** a chunk encrypted by the previous Arius version is downloaded
- **THEN** the system SHALL decrypt it using the same PBKDF2 parameters and produce the original content

### Requirement: No structural leakage in blob names
All blob names in storage SHALL be opaque hashes derived from passphrase-seeded SHA-256. An attacker with storage access but without the passphrase SHALL NOT be able to infer file names, directory structure, or relationships between blobs.

#### Scenario: Blob names reveal nothing
- **WHEN** an attacker enumerates all blobs in the container
- **THEN** they SHALL see only opaque hex hashes as blob names and encrypted content, with no information about file names, paths, or directory structure

#### Scenario: Same content different passphrase produces different hash
- **WHEN** the same file is archived with two different passphrases
- **THEN** the content hashes and blob names SHALL differ

### Requirement: Passphrase-seeded hashing
All content hashes and tree hashes SHALL be computed as `SHA256(passphrase + data)` to prevent hash collision attacks and ensure blob names are passphrase-dependent.

#### Scenario: Content hash seeded with passphrase
- **WHEN** a file is hashed for content addressing
- **THEN** the hash SHALL be `SHA256(passphrase + file_bytes)`

#### Scenario: Tree hash seeded with passphrase
- **WHEN** a tree node hash is computed
- **THEN** the hash SHALL be `SHA256(passphrase + serialized_tree_entries)`
