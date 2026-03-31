## 1. Rename Existing Classes

- [ ] 1.1 Rename `EncryptingStream` inner class to `AesCbcEncryptingStream` in `PassphraseEncryptionService.cs`
- [ ] 1.2 Rename `DecryptingStream` inner class to `AesCbcDecryptingStream` in `PassphraseEncryptionService.cs`
- [ ] 1.3 Update all existing CBC encryption tests to reference the renamed classes (ensure tests still pass)

## 2. AES-256-GCM Stream Implementation

- [ ] 2.1 Implement `AesGcmEncryptingStream` (write-only stream): writes 38-byte ArGCM1 header, buffers up to 64 KiB blocks, encrypts each with `System.Security.Cryptography.AesGcm`, writes length + ciphertext + tag, writes sentinel on dispose
- [ ] 2.2 Implement `AesGcmDecryptingStream` (read-only stream): reads and parses ArGCM1 header, derives key via PBKDF2 (100k iterations, 16-byte salt), reads blocks (length + ciphertext + tag), decrypts with counter-derived nonces, signals EOF on sentinel
- [ ] 2.3 Implement nonce derivation helper: `nonce_i = nonce₀ XOR little_endian_bytes(i, 12)`

## 3. Auto-Detection and Service Wiring

- [ ] 3.1 Update `PassphraseEncryptionService.WrapForEncryption` to create `AesGcmEncryptingStream` (GCM default for writes)
- [ ] 3.2 Update `PassphraseEncryptionService.WrapForDecryption` to peek magic bytes (`ArGCM1` vs `Salted__`) and return the appropriate decrypting stream
- [ ] 3.3 Verify `IEncryptionService` interface is unchanged — no signature modifications

## 4. Content Types

- [ ] 4.1 Add GCM content type constants to `BlobConstants.ContentTypes`: `LargeGcmEncrypted = "application/aes256gcm+gzip"`, `TarGcmEncrypted = "application/aes256gcm+tar+gzip"`, and corresponding entries for filetree, snapshot, and chunk-index
- [ ] 4.2 Rename existing CBC constants to make scheme explicit (e.g., `LargeEncrypted` → `LargeCbcEncrypted`)
- [ ] 4.3 Update content type selection in `ArchivePipelineHandler` to use GCM content types for encrypted writes
- [ ] 4.4 Update content type selection in `TreeService`, `SnapshotService`, and `ChunkIndexService` to use GCM content types

## 5. Unit Tests (Arius.Core.Tests)

- [ ] 5.1 GCM encrypt/decrypt roundtrip test (small payload)
- [ ] 5.2 GCM encrypt/decrypt roundtrip test (4 MB payload — multi-block)
- [ ] 5.3 Verify ArGCM1 magic prefix in encrypted output
- [ ] 5.4 Verify header structure: salt (16 bytes), iteration count (100,000 LE uint32), nonce (12 bytes)
- [ ] 5.5 Verify bounded memory: encrypt 32 MB stream without exceeding ~256 KiB encryption buffer
- [ ] 5.6 Tamper detection: modify one byte in a block's ciphertext, assert decryption fails
- [ ] 5.7 Truncation detection: remove sentinel block, assert decryption fails
- [ ] 5.8 Auto-detection test: `WrapForDecryption` correctly handles both ArGCM1 and Salted__ streams
- [ ] 5.9 Auto-detection test: `WrapForDecryption` throws `InvalidDataException` on unknown magic
- [ ] 5.10 Create GCM golden file test data (encrypt known content with known passphrase, commit as test fixture)

## 6. Integration Tests (Arius.Integration.Tests)

- [ ] 6.1 End-to-end archive + restore roundtrip with GCM encryption (large file)
- [ ] 6.2 End-to-end archive + restore roundtrip with GCM encryption (tar bundle)
- [ ] 6.3 Mixed-archive test: archive some files with CBC build, archive more files with GCM build, restore all — verify all files restored correctly

## 7. Recovery Script

- [ ] 7.1 Create `recover-chunk.sh` bash script: parse ArGCM1 header, derive key via `openssl kdf PBKDF2`, decrypt blocks with `openssl enc -d -aes-256-gcm`, pipe through `gunzip`
- [ ] 7.2 Add OpenSSL 3.x version check at script startup
- [ ] 7.3 Test script locally against a GCM-encrypted chunk (large file)
- [ ] 7.4 Test script locally against a GCM-encrypted chunk (tar bundle)

## 8. CI/CD Pipeline

- [ ] 8.1 Add a recovery script test job to `release.yml`: encrypt a known file with the built Arius binary, run `recover-chunk.sh`, compare SHA256 of recovered output against original
- [ ] 8.2 Ensure the job runs on `ubuntu-latest` (OpenSSL 3.x available)
