## ADDED Requirements

### Requirement: AES-256-CBC encryption
All repository data (except key files) SHALL be encrypted using AES-256 in CBC mode with PKCS7 padding and 128-bit block size.

#### Scenario: Encrypt a pack file
- **WHEN** a pack file is ready for upload
- **THEN** it is encrypted with AES-256-CBC and the output begins with the OpenSSL salt prefix `Salted__` followed by an 8-byte random salt, followed by ciphertext

### Requirement: OpenSSL-compatible format
Encrypted data SHALL use the OpenSSL-compatible format: `"Salted__" (8 bytes) || salt (8 bytes) || ciphertext`. This format SHALL be decryptable by OpenSSL command-line tools with the correct passphrase.

#### Scenario: OpenSSL interoperability
- **WHEN** an encrypted blob is downloaded from Azure
- **THEN** it can be decrypted using `openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -md sha256` with the repository passphrase

### Requirement: PBKDF2 key derivation
Encryption keys SHALL be derived from the passphrase and salt using PBKDF2 with SHA-256, 10,000 iterations, producing a 32-byte key and 16-byte IV.

#### Scenario: Key derivation
- **WHEN** a passphrase and salt are provided
- **THEN** PBKDF2-SHA256 with 10,000 iterations derives 32 bytes for the AES key and 16 bytes for the IV

### Requirement: Multi-key support
The repository SHALL support multiple passwords. Each password has a corresponding key file in `keys/` containing the master key encrypted with that password's derived key.

#### Scenario: Add a new password
- **WHEN** user runs `key add` with the existing passphrase and a new passphrase
- **THEN** a new key file is created in `keys/` containing the master key encrypted with the new passphrase

#### Scenario: Remove a password
- **WHEN** user runs `key remove` specifying a key ID, and at least one other key remains
- **THEN** the key file is deleted from `keys/`

#### Scenario: Reject removing last key
- **WHEN** user runs `key remove` and only one key file exists
- **THEN** the system rejects the operation with an error

### Requirement: Stream-based encryption
Encryption and decryption SHALL operate on streams, supporting arbitrarily large data without loading entire content into memory.

#### Scenario: Large file encryption
- **WHEN** a 100 GB pack file is encrypted
- **THEN** encryption processes the data as a stream without requiring the full content in memory

### Requirement: Master key architecture
The repository SHALL use a master key for all data encryption. Individual passwords encrypt/decrypt this master key via key files. Changing a password SHALL NOT require re-encrypting repository data.

#### Scenario: Password change
- **WHEN** user changes their password with `key passwd`
- **THEN** a new key file is created with the master key encrypted under the new password, the old key file is removed, and no data packs are modified
