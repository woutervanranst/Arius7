## Context

Arius encrypts data using AES-256-CBC with PBKDF2-SHA256 (10,000 iterations) in an openssl-compatible format (`Salted__` + 8-byte salt + ciphertext). The current `IEncryptionService` interface provides `WrapForEncryption`/`WrapForDecryption` stream wrappers, consumed identically by chunks, filetrees, snapshots, and chunk-index shards. CBC is a streaming cipher — `CryptoStream` handles arbitrarily large data with bounded memory. All cryptography uses `System.Security.Cryptography` (BCL) with zero external dependencies.

The write path always uses one encryption scheme; the read path currently assumes the same scheme. Content types on blobs (e.g., `application/aes256cbc+gzip`) identify the format but are not currently used for decrypt dispatch — the code just calls `_encryption.WrapForDecryption(stream)`.

## Goals / Non-Goals

**Goals:**
- Introduce AES-256-GCM as the default encryption for new writes (chunks, filetrees, snapshots, chunk-index)
- Maintain full read-path compatibility with existing AES-256-CBC chunks
- Stream-encrypt arbitrarily large data with bounded memory (no full-file buffering)
- Keep zero external NuGet dependencies — use only `System.Security.Cryptography`
- Provide a pure Python recovery script tested in CI/CD
- Rename classes to make the encryption scheme explicit

**Non-Goals:**
- Migration of existing CBC blobs to GCM (out of scope, separate change)
- Changing the hash construction (`SHA256(passphrase + data)`) — locked for backwards compat
- Changing the `IEncryptionService` interface signature
- Supporting key rotation or multiple passphrases
- Backwards compatibility for non-chunk folders (filetrees, snapshots, chunk-index) — these switch to GCM-only

## Decisions

### Decision 1: Chunked AEAD for streaming

AES-GCM is not a streaming cipher — it computes an authentication tag over the entire message. `System.Security.Cryptography.AesGcm` operates on buffers, not streams. For multi-gigabyte chunks, we cannot buffer the entire compressed payload.

**Approach**: Split the compressed data into fixed-size blocks (64 KiB), encrypt each block independently with a counter-derived nonce, and append a sentinel block to prevent truncation. This is the same approach used by `age`, Tink, and TLS 1.3 record encryption.

**Alternatives considered**:
- *Buffer entire payload*: Not feasible for large files (multi-GB)
- *Use CBC for large, GCM for small*: Defeats the purpose of the migration
- *Use a streaming AEAD library (e.g., NSec secretstream)*: Would add an external dependency; the chunked approach is straightforward with BCL types

**Block size rationale**: 64 KiB balances overhead (20 bytes/block = 0.03%) against memory usage. Matches `age` and Tink conventions.

### Decision 2: Binary format `ArGCM1`

Custom format since there is no standard for this combination. The magic `ArGCM1` (6 bytes) enables auto-detection and versioning.

```
HEADER (38 bytes)
═════════════════
Offset  Size   Field
──────  ────   ─────
  0       6    Magic: "ArGCM1" (ASCII)
  6      16    Salt (random, for PBKDF2)
 22       4    PBKDF2 iterations (LE uint32)
 26      12    Nonce₀ (random, base nonce for block counter)
──────  ────
 38     total

BLOCK (repeated)
════════════════
Offset  Size              Field
──────  ────              ─────
  0       4               Plaintext length (LE uint32, max 65536)
  4       length + 16     Ciphertext + GCM tag

SENTINEL (final block)
══════════════════════
  0       4               Length: 0x00000000
  4      16               GCM tag (authenticates end-of-stream)
```

**Nonce derivation**: `nonce_i = nonce₀ XOR little_endian_bytes(i, 12)` where `i` is the zero-based block index. This prevents nonce reuse (monotonic counter), block reordering (positionally bound), and truncation (sentinel under unique nonce).

**Alternatives considered**:
- *Encode block size in header*: Not needed — fixed at 64 KiB in v1, version bump if changed
- *Use associated data per block*: The block index is already encoded in the nonce; AAD would be redundant
- *Variable-length nonce (XChaCha20)*: Would require external dependency; 12-byte GCM nonce with random base + counter is safe

### Decision 3: PBKDF2-SHA256 with 100,000 iterations

Argon2id was considered but rejected because key derivation runs per-chunk (not per-session). With OWASP-recommended Argon2id params, each derivation takes ~0.5–1s, which is excessive when archiving many chunks.

PBKDF2-SHA256 at 100,000 iterations takes ~5–20ms per derivation — acceptable per-chunk cost.

The iteration count is stored in the header, so it can be increased in future writes without breaking existing chunks.

**Salt size**: 16 bytes (up from 8 in CBC). Matches modern recommendations.

**Key derivation output**: 32 bytes for AES-256 key. Unlike the CBC format, the nonce is stored directly in the header (random, not derived).

### Decision 4: Auto-detection via magic bytes

`WrapForDecryption` peeks at the first bytes of the stream to select the codec:
- Bytes `0..6` == `"ArGCM1"` → GCM decrypting stream
- Bytes `0..8` == `"Salted__"` → CBC decrypting stream (legacy)
- Otherwise → error

This keeps the `IEncryptionService` interface unchanged. No caller modifications needed. The write path always uses GCM.

**Alternatives considered**:
- *Two services, dispatch by content type*: Requires changes to all callers (RestorePipelineHandler, TreeBlobSerializer, SnapshotSerializer, ShardSerializer). Invasive.
- *Content-type based dispatch in the service*: Service doesn't have access to blob metadata, only the stream.

### Decision 5: Class renaming

| Current | New | Role |
|---|---|---|
| `PassphraseEncryptionService` | `PassphraseEncryptionService` | Orchestrator: writes GCM, reads both (auto-detect) |
| *(inner class)* `EncryptingStream` | `AesCbcEncryptingStream` | Legacy CBC write stream (retained for test/reference) |
| *(inner class)* `DecryptingStream` | `AesCbcDecryptingStream` | Legacy CBC read stream |
| *(new)* | `AesGcmEncryptingStream` | New GCM write stream |
| *(new)* | `AesGcmDecryptingStream` | New GCM read stream |
| `PlaintextPassthroughService` | `PlaintextPassthroughService` | Unchanged |

`PassphraseEncryptionService` remains the single entry point. Internally it delegates to CBC or GCM stream classes. The `WrapForEncryption` method creates `AesGcmEncryptingStream`; `WrapForDecryption` peeks magic bytes and creates the appropriate decrypting stream.

### Decision 6: Content types

| Content Type | Usage |
|---|---|
| `application/aes256gcm+gzip` | Large chunks (GCM) |
| `application/aes256gcm+tar+gzip` | Tar chunks (GCM) |
| `application/aes256cbc+gzip` | Large chunks (CBC, legacy) |
| `application/aes256cbc+tar+gzip` | Tar chunks (CBC, legacy) |

Non-chunk blobs (filetrees, snapshots, chunk-index) reuse `application/aes256gcm+gzip` — same as today's pattern where they share `application/aes256cbc+gzip`.

The `ContentTypes` class gains `LargeGcmEncrypted`, `TarGcmEncrypted`, and corresponding entries for non-chunk types. The write-path selection changes from `_encryption.IsEncrypted ? ContentTypes.LargeEncrypted : ...` to a scheme-aware selection. The `IEncryptionService` interface is not changed — content type selection uses an `IsEncrypted` check combined with a compile-time constant for the GCM content type identifiers.

### Decision 7: Pure Python recovery script

The recovery script (`recover-chunk.py`) uses Python 3 with the `cryptography` package (`pip install cryptography`). All other operations use the stdlib: `hashlib`, `struct`, `gzip`, `sys`, `argparse`.

**Structure**:
1. Import `AESGCM` from `cryptography.hazmat.primitives.ciphers.aead`; exit with a clear error if unavailable
2. Parse 38-byte header
3. Derive key via `hashlib.pbkdf2_hmac("sha256", passphrase_bytes, salt, iterations, dklen=32)`
4. Loop: read 4-byte block length, read ciphertext+tag, compute nonce (XOR base with counter), decrypt via `AESGCM.decrypt`
5. Decompress the concatenated plaintext blocks with `gzip.decompress` and write to file or stdout

**Why Python over bash**: `openssl enc` subcommand in OpenSSL 3.x explicitly refuses AEAD ciphers. Pure bash with only coreutils cannot perform GCM authentication. Python with the `cryptography` package is the minimal viable option that is both portable and correct.

**Dependency check**: The script imports `cryptography` inside a try/except at startup and exits non-zero with a clear install instruction if unavailable.

### Decision 8: CI/CD testing of recovery script

The recovery script is tested in the `release.yml` GitHub Actions workflow. After building the platform binaries, a new job:

1. Installs Python 3 and the `cryptography` package (`pip install cryptography`)
2. Runs `python3 recover-chunk.py <golden-chunk-file> <passphrase> <output-file>` against a pre-committed golden file
3. Compares the recovered output against the known plaintext (SHA256 equality check)

This runs on `ubuntu-latest` which ships Python 3. The test validates the full format contract between the C# implementation and the Python script.

## Risks / Trade-offs

**[Risk: Python cryptography package availability]** → The recovery script requires Python 3 and the `cryptography` package. Mitigation: Python 3 ships with most modern Linux distributions; `pip install cryptography` is a one-liner. The script checks the import at startup and exits non-zero with a clear install instruction if unavailable. As a fallback, the C# binary itself can always decrypt.

**[Risk: AES-GCM hardware requirement]** → `System.Security.Cryptography.AesGcm` requires AES-NI hardware support on some platforms. Mitigation: All x64 CPUs since ~2010 and Apple Silicon support AES-NI. The class throws `PlatformNotSupportedException` if unavailable — fail fast with a clear message.

**[Risk: Block size locked to format version]** → The 64 KiB block size is implicit in `ArGCM1` (not encoded in the header). Changing it requires a version bump (`ArGCM2`). Mitigation: 64 KiB is a well-established choice; no foreseeable reason to change.

**[Trade-off: Per-chunk key derivation cost]** → Each chunk runs PBKDF2 at 100,000 iterations (~5–20ms). For an archive with hundreds of chunks, this adds seconds. Acceptable given I/O dominates.

**[Trade-off: ~20 bytes overhead per 64 KiB block]** → 4-byte length + 16-byte tag per block = 0.03% overhead. Negligible.
