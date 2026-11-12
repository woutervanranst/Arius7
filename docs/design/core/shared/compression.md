# Compression

> **Code:** `src/Arius.Core/Shared/Compression/*`  ·  **Decisions:** [ADR-0012](../../../decisions/adr-0012-zstd-as-new-compression-algorithm.md), [ADR-0024](../../../decisions/adr-0024-framework-zstandard-codec.md)  ·  **Terms:** [chunk](../../../glossary.md#chunk)

## Purpose

The compression boundary that all blob bodies pass through. `ICompressionService` wraps a stream with a codec layer the same way `IEncryptionService` does, so feature handlers and serializers stay codec-agnostic. New blobs are always written as zstd; the read path is self-describing and transparently decodes both new zstd and legacy gzip frames.

## How it works

`ICompressionService` exposes two stream-wrapping methods:

- `WrapForCompression(destination)` returns a write-only stream that compresses into `destination`; disposing it finalizes the frame.
- `WrapForDecompression(source)` returns a read-only stream that decompresses from `source`.

`ZstdCompressionService` is the only registered implementation (`ServiceCollectionExtensions` binds it as a singleton). It writes RFC 8878 zstd frames via the framework's `ZstandardStream` and owns a static `GZipCompressionService` instance to which it delegates legacy reads.

The provider migration preserves quality 15, checksums, level-derived windows, no long-distance matching, and single-compressor behavior. Decode windows are explicitly capped at 128 MiB. The mapping, native deployment implications, and compatibility/performance shipping gates are recorded in [ADR-0024](../../../decisions/adr-0024-framework-zstandard-codec.md).

The read path never consults blob metadata. `WrapForDecompression` returns an `AutoDetectDecompressionStream` that, on first read, peeks 4 bytes, then routes:

```mermaid
flowchart TD
    R["AutoDetectDecompressionStream<br/>peek 4 magic bytes"] --> E{read == 0?}
    E -- "yes (legacy empty blob)" --> P["PrefixedStream → empty content"]
    E -- no --> Z{zstd magic<br/>0xFD2FB528?}
    Z -- yes --> ZD["ZstandardStream<br/>128 MiB window cap"]
    Z -- no --> G{gzip magic<br/>0x1F 0x8B?}
    G -- yes --> GD["GZipCompressionService.WrapForDecompression"]
    G -- no --> X["throw InvalidDataException"]
```

The peeked bytes are replayed via a `PrefixedStream` so the chosen decoder sees a complete frame (the magic is "un-peeked"). `read == 0` maps to empty content because the legacy gzip era wrote empty payloads as 0-byte blobs. The wrapper checks cancellation before delegation, including when a decoder has buffered output.

**Upload policy:** [`ChunkStorageService`](./chunk-storage.md) streams directly through compression and encryption without decoding output or rehashing source bytes. Archive assumes source files stay static throughout the run and trusts the previously computed chunk hash. Encoder compatibility is verified by cross-codec and independent-recovery tests; checksums and truncation rejection remain on reads. [ADR-0024](../../../decisions/adr-0024-framework-zstandard-codec.md) records the deliberately reduced runtime verification.

## Key invariants

- **New blobs are zstd; old gzip blobs stay readable forever.** The read path must remain self-describing (magic-byte detection), never dependent on content-type or external metadata.
- **A frame written today is restorable by a default-configured reader.** Window size and long-distance matching stay at zstd's level-derived defaults, kept under the decompressor's 128 MiB `ZSTD_d_windowLogMax` cap — no large-window/LDM frames.
- **Checksums are enabled, and each stream uses one compressor.** The framework exposes `AppendChecksum`, but no worker-count option; its single-threaded context initialization is a runtime-upgrade review constraint.
- **Sources must remain static during archive.** Upload trusts the hash from the earlier hashing stage; source changes between hashing and upload are not detected.
- **0-byte source ⇒ empty content**, not a format error (legacy empty-payload compatibility).

## Why this shape

The format choice and its alternatives are recorded in [ADR-0012](../../../decisions/adr-0012-zstd-as-new-compression-algorithm.md); [ADR-0024](../../../decisions/adr-0024-framework-zstandard-codec.md) changes the provider and removes upload-time codec round-trips. The stream-wrapping `ICompressionService` boundary exists to keep codec mechanics out of the storage, filetree, snapshot, and chunk-index serializers — mirroring `IEncryptionService` so the two layers compose as `compress → encrypt` on write and `decrypt → decompress` on read.

> Note: ADR-0012's prose cites level 19; the code default is currently 15. The level is a tunable performance knob — treat the code constant as authoritative and any change as a measured performance decision (per the ADR).

## Open seams / future

- **Codec is a repository-wide singleton** (one per provider). Adding a third codec, or making the write codec configurable per repository, would mean extending the magic-byte detection table and the DI registration — the boundary already supports it.
- **No write path for gzip in production.** `GZipCompressionService.WrapForCompression` exists only to keep the legacy codec a complete, testable unit; if it is ever truly dead, it can be dropped without touching the read path.
