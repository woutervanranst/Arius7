# Replace gzip with zstd (ZstdSharp.Port) in Arius

## Context

Arius compresses every chunk with `System.IO.Compression.GZipStream`
(`CompressionLevel.SmallestSize`) before encrypting and uploading. We're switching new
writes to **zstd** for a better ratio and much faster restore.

Decisions made:
- **Library: `ZstdSharp.Port` only** — a faithful, upstream-maintained, pure-managed C# port
  of facebook's reference libzstd. No native binaries (runs anywhere .NET runs: glibc/musl
  Linux, macOS incl. Apple Silicon, Windows, single-file/AOT) — chosen over native ZstdNet to
  avoid per-RID native-binary and Alpine/musl deployment headaches. Same `CompressionStream`/
  `DecompressionStream` API; writes **standard RFC-8878 frames**.
- **Backwards compat: decompress-only for gzip.** Keep the BCL gzip *decompressor* so existing
  `+gzip` blobs stay readable. Everything written from now on — chunks, tar bundles,
  filetrees, chunk-index, snapshots — is zstd. No gzip is ever written again.

### Why this is safe for recovery (the original concern)

The fear was: "in 3 years `restore` fails with *corrupted stream* and the source is gone."
That is mitigated structurally:
- The **zstd format is an IETF standard (RFC 8878)** with a backward-compat guarantee — as
  durable as gzip. ZstdSharp writes standard frames, so even if the library vanished, the
  reference `zstd` CLI / any libzstd could decode an Arius chunk (you are **not locked in**).
- The only residual risk is an *encoder* bug writing a frame that won't decode. We retire it
  by **verifying the codec round-trip inline on every upload** (see step 5), so any such frame
  fails loudly *at archive time*, while the original is still on disk.
- We **enable the zstd content checksum** so decode-time corruption is always loud — keeping
  parity with gzip's always-on CRC32 (which we'd otherwise lose).

## Implementation

The codec is hard-coded today at `ChunkStorageService.cs:62` (compress) and `:169`
(decompress); gzip is also used in the filetree (stage 6c) and snapshot (stage 6d) paths.

**1. Add the dependency.** `ZstdSharp.Port` to `Arius.Core.csproj`.

**2. Compression abstraction.** Add `ICompressionService` (mirroring `IEncryptionService`)
with stream-wrapping methods matching the existing gzip wiring:
- `Stream CreateCompressionStream(Stream destination)` — returns a write stream; plaintext
  written in is compressed into `destination` (replaces the `new GZipStream(...)` write).
- `Stream CreateDecompressionStream(Stream source, CompressionType type)` — returns a read
  stream yielding plaintext (replaces the `new GZipStream(..., Decompress)`).

Provide two implementations selected by a `CompressionType` enum (`GZip`, `Zstd`):
- `ZstdCompressionService` — uses `ZstdSharp.CompressionStream`/`DecompressionStream`.
  On the compressor, set **`ZSTD_c_checksumFlag = 1`** and the chosen **level** (default ~19;
  beats gzip `SmallestSize` on ratio while decompressing far faster — make it configurable).
  Keep `nbWorkers = 0` (single-threaded) initially.
- `GZipCompressionService` — **decompress only** (used solely for old `+gzip` blobs). Its
  compress path is never wired up.

Inject `ICompressionService` into `ChunkStorageService` and the filetree/snapshot services.
Write path always uses `CompressionType.Zstd`.

**3. Tag the algorithm on each blob.** `BlobConstants.cs:35-67` already encodes the content
type (`application/aes256gcm+gzip`, `…+tar+gzip`). Add the `+zstd` variants and write those
for all new blobs. (Blob metadata in `BlobConstants.cs:7-29` is an alternative/secondary
signal.)

**4. Read path selects the decompressor from the tag.** On download, parse the stored
content-type: `+zstd` → `ZstdCompressionService`, `+gzip` → `GZipCompressionService`. This is
the one backwards-compat requirement. Pipeline stays: download → AES-GCM decrypt → decompress.

**5. Always verify the codec round-trip inline during upload** (bounded & streaming; *not*
gated on `--remove-local`). zstd decompression is cheap, so the cost is dominated by
compression + the network upload — effectively free.

Mechanism — a **tee** on the compressor's output, so we never re-read the source or buffer
the whole chunk:

```
plaintext → ZstdCompressionStream → TeeStream ─┬→ EncryptionStream → blob storage
                                               └→ Pipe → ZstdDecompressionStream → SHA-256 hasher → compare to H
```

The `TeeStream` forwards each compressed block to both (a) the encrypt→upload chain and (b) a
bounded `System.IO.Pipelines.Pipe`; a concurrent task reads the pipe through a
`DecompressionStream` into the **same content hasher** Arius uses (`IEncryptionService`'s
SHA-256, incl. the passphrase prefix) and compares the final digest to the chunk hash `H`
already computed in stage 2. The pipe's backpressure keeps memory flat (≈ one buffer, no
full-chunk buffering, no second pass over the source). On mismatch: **fail the chunk loudly**
— do not write its chunk-index entry or pointer, and do not delete the source.

We do **not** re-download to verify: new blobs land in the Azure **archive (offline) tier**, so
reading them back would require rehydration. The inline tee proves the codec round-trip at
write time. (Encryption isn't re-checked here — it's GCM-authenticated and verified on real
restore.) Applies to large-file and tar-bundle chunk uploads (stages 4a/4c).

## Files to change

- `src/Arius.Core/Shared/ChunkStorage/ChunkStorageService.cs` — replace gzip at `:62`/`:169`
  with `ICompressionService`; thread the resolved `CompressionType` on download; wire the
  step-5 tee + verification task into `UploadChunkAsync` and fail the chunk on hash mismatch.
- New `TeeStream` (duplicating write stream) + a small pipe-based round-trip verifier
  (e.g. `src/Arius.Core/Shared/Compression/`).
- New `src/Arius.Core/Shared/Compression/ICompressionService.cs`, `ZstdCompressionService.cs`,
  `GZipCompressionService.cs`, `CompressionType.cs`.
- `src/Arius.Core/Shared/Storage/BlobConstants.cs` — add `+zstd` content-type variants; helper
  to parse algorithm from content-type.
- Filetree (`FileTreeService` / stage 6c) and snapshot (`SnapshotService` / stage 6d) compress
  paths — route through `ICompressionService`.
- DI registration for the new services.
- `src/Arius.Core/Arius.Core.csproj` — add `ZstdSharp.Port`.

## Verification

- **Round-trip property tests:** random + real files across sizes (0 B, 1 B, >2 GiB), checksum
  on, assert byte-for-byte equality.
- **Standard-frame interop (recovery insurance):** decode an Arius-written zstd chunk with the
  reference `zstd` CLI; confirms you're not locked to ZstdSharp.
- **Backwards-compat:** an old `+gzip` blob still restores correctly via the gzip decompressor.
- **Corruption-rejection:** flip bytes in (a) the encrypted blob → expect GCM failure; (b) a
  plaintext zstd frame → expect a decode/checksum error, never silent wrong output.
- **Inline round-trip verification:** force a hash mismatch (corrupt frame / wrong digest);
  confirm the upload fails loudly and the chunk is not indexed and the source is not deleted.
  Also assert memory stays bounded on a large chunk (the tee/pipe never buffers the whole
  chunk) and that the verification adds negligible wall-time vs. compression + upload.
- **End-to-end:** `arius archive` a sample tree, `arius restore` to a fresh location, diff
  against source; repeat with `--remove-local`. Run the existing Arius suite + new tests, and
  sanity-check ratio/speed vs the old gzip path.
