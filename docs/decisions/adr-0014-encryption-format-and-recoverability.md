---
status: "accepted"
date: 2026-06-17
decision-makers: ["Wouter Van Ranst"]
consulted: ["Claude Code"]
informed: ["Arius maintainers"]
confidence: "high"
---

# AES-256-GCM (ArGCM1) encryption format with long-term recoverability

## Context and Problem Statement

Arius encrypts every repository body — chunks, tar chunks, filetrees, snapshots, chunk-index shards — before upload. The legacy scheme was AES-256-CBC in the openssl-compatible `Salted__` format. CBC provides confidentiality but no integrity: a flipped ciphertext bit decrypts to corrupted plaintext without detection, and a truncated blob simply yields fewer bytes. For an archive whose blobs sit untouched on the Azure archive tier for years, undetected corruption is the worst possible failure — it surfaces only during a restore that is already too late.

Switching to an authenticated cipher (AES-256-GCM) fixes integrity, but GCM is not a streaming cipher: `System.Security.Cryptography.AesGcm` computes one tag over the whole message and operates on buffers, not streams, so multi-gigabyte chunks cannot be encrypted in one call. Any chosen design must also stay decryptable decades from now even if the Arius binary no longer runs — the blobs, not the application, are the system of record.

The question for this ADR is what on-disk encryption format, key-derivation function, and recovery story Arius should adopt so that new blobs gain authenticated integrity and streaming bounded-memory encryption while remaining recoverable with standard tooling long after Arius itself is gone.

## Decision Drivers

* Repository blobs must remain decryptable years later with standard, non-Arius tooling, even if the Arius binary and its NuGet dependencies are unavailable.
* Encryption must be authenticated: tampering and truncation must fail loudly, not silently corrupt restored data.
* Encryption must stream with bounded memory for multi-gigabyte chunks; full-payload buffering is not acceptable.
* Key derivation runs per chunk during archive, so its cost must stay in the low-millisecond range.
* Existing AES-256-CBC blobs must remain readable — no forced migration.
* Cryptography should use only `System.Security.Cryptography` (BCL); zero external crypto NuGet dependencies.
* The `IEncryptionService` interface and its callers must not change.

## Considered Options

* Keep AES-256-CBC in the `Salted__` format.
* AES-256-GCM as a chunked AEAD (`ArGCM1`) with PBKDF2-SHA256 key derivation, behind unchanged `IEncryptionService`, with magic-byte auto-detection of legacy CBC.
* AES-256-GCM as above but with Argon2id key derivation instead of PBKDF2.
* A streaming-AEAD library (e.g. NSec/libsodium `secretstream`, or XChaCha20-Poly1305).

## Decision Outcome

Chosen option: "AES-256-GCM as a chunked AEAD (`ArGCM1`) with PBKDF2-SHA256 key derivation", because it adds authenticated integrity and truncation detection, streams in bounded memory by splitting payloads into independently sealed 64 KiB blocks, keeps per-chunk key derivation cheap, uses only BCL primitives, and — most importantly — is a documented byte format recoverable by the dependency-light pure-Python `recover-chunk.py` and by any standard AES-GCM implementation. Legacy CBC stays readable through magic-byte auto-detection, so no blob is orphaned.

Confidence: high. AES-256-GCM and PBKDF2-SHA256 are standard, well-analyzed primitives; the chunked-AEAD construction mirrors `age`, Tink, and TLS 1.3 record encryption; and the format is independently exercised by `recover-chunk.py` in CI. The recoverability guarantee — not the choice of GCM, which is uncontroversial — is the load-bearing decision here.

Before — AES-256-CBC, openssl `Salted__` format, no integrity:

```text
"Salted__"(8) | Salt(8) | AES-256-CBC ciphertext (PKCS7)
key,iv = PBKDF2-SHA256(passphrase, salt, 100_000, 48 bytes)
```

After — AES-256-GCM, `ArGCM1` chunked AEAD, authenticated + truncation-detecting:

```text
HEADER (38 bytes): "ArGCM1"(6) | Salt(16) | Iterations(4 LE) | Nonce₀(12)
BLOCK (repeated):  Length(4 LE, ≤ 65536) | Ciphertext+Tag(Length+16)
SENTINEL (final):  Length(4)=0 | Tag(16)
key   = PBKDF2-SHA256(passphrase, salt, iterations, 32 bytes)
nonceᵢ = Nonce₀ XOR little_endian_bytes(i, 12)
```

### Format and Construction

`PassphraseEncryptionService` writes `ArGCM1` and reads both `ArGCM1` and legacy `Salted__`. The inner `AesGcmEncryptingStream` buffers plaintext into `GcmBlockSize = 64 * 1024` blocks; each block is sealed independently with `AesGcm.Encrypt` under a counter-derived nonce, then framed as a 4-byte little-endian length followed by ciphertext and the 16-byte tag. `DeriveNonce` computes `nonceᵢ = nonce₀ XOR little_endian_bytes(i, 12)`, which makes nonce reuse impossible within a blob, binds each block to its position (reordering breaks authentication), and gives the sentinel its own unique nonce. On dispose, `WriteSentinel` emits a zero-length block plus a tag over the empty payload — `AesGcmDecryptingStream` only reports EOF after authenticating that sentinel, so a truncated blob throws instead of returning short. A tampered block or sentinel throws `AuthenticationTagMismatchException`.

The 64 KiB block size is fixed in `ArGCM1` (not encoded in the header); the ~20 bytes of per-block overhead (4-byte length + 16-byte tag) is ~0.03%. A block-size or scheme change requires a version bump (`ArGCM2`).

### Key Derivation: PBKDF2, not Argon2

Key derivation runs per chunk, not per session. `DeriveGcmKey` uses `Rfc2898DeriveBytes.Pbkdf2` with SHA-256 at `GcmPbkdf2Iter = 100_000` iterations (~5–20 ms per derivation). Argon2id was rejected: at OWASP-recommended parameters each derivation costs ~0.5–1 s, which is unacceptable when an archive derives a key for every chunk. The iteration count is written into the header so future writes can raise it without breaking existing blobs; the reader rejects an out-of-range count (`0` or `> GcmMaxPbkdf2Iter = 10_000_000`) to refuse crafted blobs. The salt is 16 bytes (up from 8 in CBC); the nonce is stored in the header rather than derived. The legacy CBC reader derives via PBKDF2-SHA256 at `CbcPbkdf2Iter = 100_000` into a 48-byte key+IV.

### Recoverability is the Point

GCM itself is solid; the decision that matters is that the blob is the system of record and must outlive Arius. Three mechanisms enforce this:

* **`recover-chunk.py`** (repo root) decrypts any Arius blob with only Python 3.7+ and the `cryptography` package. It auto-detects `ArGCM1` vs `Salted__` from the leading magic bytes and auto-detects zstd vs gzip on the decrypted stream, then writes recovered content (or, with `--no-decompress`, the still-compressed bytes for the `zstd`/`gzip` CLI). It deliberately carries no native or Arius dependency.
* **Magic-byte auto-detection** in `WrapForDecryption` peeks the prefix through a `PeekStream` and dispatches to the GCM or CBC reader, so legacy `Salted__` blobs stay readable forever without a migration and without relying on blob content-type metadata.
* **No proprietary primitives.** The format is plain AES-256-GCM + PBKDF2-SHA256 + a documented framing, decodable by any conforming AES-GCM implementation. `openssl enc` is explicitly *not* a fallback — OpenSSL 3.x refuses AEAD ciphers via `enc` — which is exactly why a small documented script, not a shell one-liner, is the canonical recovery tool.

Compression (zstd, gzip legacy) is a separate concern owned by ADR-0012; encryption wraps the already-compressed body. New content types are `application/aes256gcm+zstd` and `application/aes256gcm+tar+zstd` (`BlobConstants`), with the CBC + gzip variants retained for legacy reads.

### Consequences and Tradeoffs

* Good, because new blobs are authenticated: bit-flips and truncation throw `AuthenticationTagMismatchException`/`InvalidDataException` instead of silently producing corrupt restores.
* Good, because the chunked AEAD streams in bounded memory regardless of chunk size, using only BCL `AesGcm`.
* Good, because the `ArGCM1` byte format is fully documented and recoverable by `recover-chunk.py` and any standard AES-GCM tool, independent of the Arius binary.
* Good, because magic-byte auto-detection keeps every legacy AES-256-CBC blob readable with no migration and no caller or interface change.
* Good, because the header-stored iteration count allows future KDF strengthening without breaking existing blobs.
* Bad, because `ArGCM1` is an Arius-specific framing with no off-the-shelf decoder — long-term recovery depends on the format staying documented and `recover-chunk.py` staying maintained and CI-tested.
* Bad, because PBKDF2 at 100,000 iterations is a deliberately weaker password hash than Argon2id; the per-chunk cost constraint trades brute-force resistance for archive throughput.
* Bad, because recovery now requires Python plus the `cryptography` package rather than ubiquitous coreutils — `openssl enc` cannot decrypt AEAD output.
* Bad, because `AesGcm` requires AES-NI on some platforms and throws `PlatformNotSupportedException` where unavailable.
* Bad, because two cipher read paths (GCM and CBC) must stay tested together indefinitely.

### Confirmation

This decision is confirmed when all of the following hold:

* New writes go through `AesGcmEncryptingStream` producing the `ArGCM1` header, per-block length+tag framing, and the zero-length sentinel; verified by `AesGcmEncryptionTests.GcmEncrypt_OutputStartsWithArGcm1Magic` and `GcmEncrypt_HeaderStructure_IsCorrect`.
* Round-trip correctness for small and large payloads is verified by `AesGcmEncryptionTests.GcmEncryptDecrypt_SmallPayload_Roundtrip` and `GcmEncryptDecrypt_LargePayload_Roundtrip`.
* Tamper and truncation detection are verified by `GcmDecrypt_TamperedCiphertext_ThrowsAuthenticationException`, `GcmDecrypt_TamperedSentinelTag_ThrowsAuthenticationException`, and `GcmDecrypt_TruncatedStream_ThrowsOnMissingSentinel`.
* Bounded-memory streaming is verified by `GcmEncrypt_LargeStream_DoesNotBufferEntireContent`.
* Legacy compatibility and dispatch are verified by `WrapForDecryption_AutoDetects_GcmAndCbc`, `WrapForDecryption_CbcGoldenFile_DecryptsCorrectly`, and `WrapForDecryption_UnknownMagic_ThrowsInvalidDataException`.
* External recoverability is verified end-to-end by `Arius.Integration.Tests.Pipeline.RecoveryScriptTests` (`Archive_EncryptedLargeFile_RecoverScript_ByteIdentical`, `Archive_EncryptedTarBundle_RecoverScript_FilesCorrect`, and the CBC variants), and by the `release.yml` workflow steps that run `recover-chunk.py` against committed GCM, GCM+zstd, and CBC golden files and compare the plaintext.

## Pros and Cons of the Options

### Keep AES-256-CBC in the `Salted__` format

* Good, because it is already implemented, openssl-compatible, and natively streaming via `CryptoStream`.
* Good, because it needs no new code and no new recovery tooling.
* Bad, because CBC provides no integrity — tampering and truncation go undetected until a restore fails.
* Bad, because it leaves the archive without authenticated protection against silent bit-rot.

### AES-256-GCM chunked AEAD (`ArGCM1`) with PBKDF2-SHA256

This is the chosen design.

* Good, because it adds authenticated integrity and explicit truncation detection via the sentinel.
* Good, because independent 64 KiB blocks stream in bounded memory with BCL `AesGcm` only.
* Good, because the documented format plus `recover-chunk.py` guarantees recovery without Arius.
* Good, because magic-byte auto-detection preserves all legacy CBC blobs with no interface change.
* Bad, because the framing is Arius-specific and recovery leans on the script and format staying documented.
* Bad, because PBKDF2 is a weaker KDF than Argon2id, accepted for per-chunk speed.

### AES-256-GCM with Argon2id key derivation

* Good, because Argon2id is the modern memory-hard password-hashing recommendation, far stronger against brute force.
* Bad, because at recommended parameters each derivation costs ~0.5–1 s, multiplied across every chunk in an archive — prohibitive for the per-chunk derivation model.
* Bad, because it would require an external NuGet dependency, breaking the BCL-only constraint and complicating the pure-Python recovery path.

### Streaming-AEAD library (libsodium `secretstream` / XChaCha20-Poly1305)

* Good, because purpose-built streaming AEAD removes the need for a hand-rolled chunked format.
* Good, because XChaCha20's 24-byte nonce removes nonce-collision concerns entirely.
* Bad, because it introduces a native/external dependency, breaking the BCL-only and dependency-light-recovery constraints.
* Bad, because recovery would then depend on that library being installable and ABI-compatible years later, weakening the long-term recoverability guarantee.

## More Information

* Encryption format design and rationale (frozen): `docs/history/openspec-archive/2026-03-31-aes256gcm-encryption/design.md`.
* Compression codec decision (separate concern): ADR-0012, `docs/decisions/adr-0012-zstd-as-new-compression-algorithm.md`.
* Implementation: `src/Arius.Core/Shared/Encryption/PassphraseEncryptionService.cs`.
* Recovery tool: `recover-chunk.py` (repo root); CI verification in `.github/workflows/release.yml`.
