## MODIFIED Requirements

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

### Requirement: Worst-case recovery with standard tools
All encrypted blobs SHALL be recoverable using only standard tools. For CBC blobs: `openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:<passphrase> | gunzip`. For GCM blobs: `recover-chunk.sh <chunk-file> <passphrase> | gunzip` (or piped to `tar x` for tar chunks).

#### Scenario: Manual recovery of CBC-encrypted large file
- **WHEN** the Arius software is unavailable and a user has the passphrase and content-hash
- **THEN** downloading `chunks/<hash>` and running `openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:<passphrase> | gunzip` SHALL produce the original file

#### Scenario: Manual recovery of GCM-encrypted large file
- **WHEN** the Arius software is unavailable and a user has the passphrase and content-hash
- **THEN** downloading `chunks/<hash>` and running `recover-chunk.sh <chunk-file> <passphrase>` SHALL produce the original file

## RENAMED Requirements

### Requirement: Streaming encryption
- **FROM:** Streaming encryption
- **TO:** Streaming encryption

*(Content unchanged — the requirement applies to both CBC and GCM schemes. GCM streaming specifics are in the `gcm-encryption` spec.)*

### Requirement: Passphrase-seeded hashing
*(Unchanged — hash construction `SHA256(passphrase + data)` is locked for backwards compatibility.)*

### Requirement: Passphrase-seeded blob names
*(Unchanged.)*

### Requirement: Backwards compatibility with previous Arius
*(Unchanged — CBC read support is retained. GCM is additive.)*

### Requirement: Filetree blob body encryption
*(Unchanged in behavior — filetrees use `IEncryptionService` which now writes GCM. No separate backwards compatibility needed for filetree blobs.)*

### Requirement: Worst-case recovery of filetree blobs
*(Updated to cover GCM — new filetree blobs use ArGCM1 format and are recoverable via `recover-chunk.sh`.)*
