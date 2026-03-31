## Why

AES-256-CBC is a legacy cipher mode without built-in authentication — ciphertext can be tampered with silently. AES-256-GCM provides authenticated encryption (AEAD), ensuring both confidentiality and integrity. The current PBKDF2 iteration count of 10,000 is also weak by modern standards. This change introduces AES-256-GCM as the default encryption scheme for new chunks while preserving full backwards compatibility with existing CBC-encrypted data.

## What Changes

- **New AES-256-GCM encryption scheme** as the default for all new chunk writes, using a custom binary format (`ArGCM1` header + chunked AEAD blocks for streaming support).
- **PBKDF2 iterations increased to 100,000** for the new scheme (old CBC chunks retain 10,000).
- **Salt size increased to 16 bytes** (up from 8 in the CBC format).
- **Zero new dependencies**: Uses built-in `System.Security.Cryptography.AesGcm` for AEAD and existing `Rfc2898DeriveBytes` for PBKDF2.
- **New content types**: `application/aes256gcm+gzip` and `application/aes256gcm+tar+gzip`.
- **Auto-detection on read**: `WrapForDecryption` peeks at magic bytes (`ArGCM1` vs `Salted__`) to select the correct decryption path — zero caller changes needed.
- **Rename existing classes** to clarify encryption scheme: `PassphraseEncryptionService` becomes scheme-explicit (e.g., `AesCbcEncryptionService` for legacy, `AesGcmEncryptionService` for new).
- **Pure bash recovery script** (`recover-chunk.sh`) for emergency decryption of GCM-encrypted chunks using only `openssl` (3.x), `xxd`, `dd`, and `gunzip`. Tested in the CI/CD release pipeline.
- **BREAKING**: Non-chunk folders (filetrees, snapshots, chunk-index) will switch to GCM-only — no backwards compatibility for those. Migration of existing non-chunk blobs is out of scope for this change.
- **Mixed archives**: After this change, an archive can contain both CBC and GCM chunks. The read path handles both transparently.

## Capabilities

### New Capabilities
- `gcm-encryption`: Defines the AES-256-GCM binary format (header structure, chunked AEAD blocks, nonce derivation), streaming encryption/decryption, and the recovery script.

### Modified Capabilities
- `encryption`: The pluggable encryption service now supports two passphrase-based schemes (CBC legacy + GCM default). Auto-detection on the read path. New content types. Class renaming. PBKDF2 iteration count changes.

## Impact

- **Code**: `IEncryptionService` interface unchanged. `PassphraseEncryptionService` split/renamed. New `AesGcmEncryptionService` class. `BlobConstants.ContentTypes` gains GCM entries. DI registration updated to use GCM by default.
- **Dependencies**: None added. All cryptography uses `System.Security.Cryptography` (BCL).
- **Storage**: New chunks get `application/aes256gcm+gzip` content type. Existing chunks untouched.
- **Tests**: Golden file tests for CBC remain. New golden file tests for GCM. Mixed-archive roundtrip tests (CBC + GCM chunks in same archive). Bash recovery script tested in CI/CD release pipeline.
- **CLI**: No user-facing changes — `--passphrase` works the same way.
