---
status: "accepted"
date: 2026-06-15
decision-makers: ["Wouter Van Ranst"]
consulted: ["Claude Code", "OpenCode"]
informed: ["Arius maintainers"]
confidence: "medium"
---

# Use Zstd as new compression algorithm

## Context and Problem Statement

Arius previously wrote repository blob bodies through `System.IO.Compression.GZipStream` with `CompressionLevel.SmallestSize`. For a backup tool, a codec change must improve storage or restore characteristics while preserving long-term recoverability after the original binary files are gone. The zstd evaluation in `2026-06-15-ztd/CONVO.md` and implementation plan in `2026-06-15-ztd/PLAN.md` found that zstd is a stable, standard format. Arius should still round-trip verify newly written zstd chunks before recording them, because finding a non-restorable chunk during a future restore would be too late.

The question for this ADR is whether Arius should replace gzip with zstd for newly written repository data, and what safety and performance constraints must govern that change.

## Decision Drivers

* Repository data must remain recoverable years later, even if the original binary files are gone.
* New compression must write standard, externally decodable frames rather than an Arius-specific format.
* Existing gzip blobs must remain readable.
* Archive must stay streaming and bounded-memory for large chunks and tar chunks.
* Compression ratio matters, but not at the expense of unacceptable archive duration.
* Codec selection should not be hard-coded throughout storage, filetree, snapshot, and chunk-index serializers.

## Considered Options

* Keep writing gzip with `CompressionLevel.SmallestSize`.
* Write zstd through `ZstdSharp.Port` behind `ICompressionService` and keep gzip as a read-only legacy codec.
* Use SharpCompress as the zstd provider.
* Use a native libzstd wrapper such as ZstdNet.

## Decision Outcome

Chosen option: "Write zstd through `ZstdSharp.Port` behind `ICompressionService` and keep gzip as a read-only legacy codec", because it writes standard RFC 8878 zstd frames, avoids native deployment complexity, centralizes compression behavior, preserves gzip restore compatibility, and verifies each new zstd chunk before it is recorded.

Before:

```text
source bytes -> gzip -> encryption -> repository blob
```

After:

```text
source bytes -> zstd -> encryption -> repository blob
                         |
                         +-> zstd decompress -> content hash verification before indexing
```

### Compression Service Boundary

Arius introduces `ICompressionService` in `Arius.Core.Shared.Compression` with stream-wrapping methods mirroring `IEncryptionService`:

```csharp
bool RequireRoundTripVerification { get; }
Stream WrapForCompression(Stream destination, bool leaveOpen = true);
Stream WrapForDecompression(Stream source, bool leaveOpen = false);
```

`ZstdCompressionService` is the production write codec. It also auto-detects zstd versus legacy gzip frame headers on reads, so restore does not depend on blob content-type metadata to choose the decompressor. `GZipCompressionService` remains available as the legacy gzip decompressor and does not require upload-time round-trip verification.

### Compression Settings

New zstd blobs use `ZstdCompressionService.DefaultCompressionLevel = 19`.

Level 19 favors ratio and matches the previous gzip `SmallestSize` intent rather than the common "fast zstd" default-level expectation. Future level changes should be treated as performance decisions and documented with new measurements.

Arius enables zstd frame checksums with `ZSTD_c_checksumFlag = 1`, so decode-time corruption fails loudly.

Arius sets `ZSTD_c_nbWorkers = 0`. Archive already parallelizes across chunks, and avoiding ZSTDMT keeps the write path simpler. Window size and long-distance matching are left at zstd defaults so Arius does not emit frames that require specially configured decoders.

### Round-Trip Verifier

Chunk uploads verify zstd inline before recording a chunk-index entry. The upload pipeline tees compressed bytes to both storage and a bounded verification pipe:

```text
source bytes -> zstd -> TeeStream -> encryption -> blob storage
                          |
                          +-> zstd decompress -> content hash -> compare with chunk hash
```

The verifier catches non-restorable output while the archive stream is still live. It fails archive before snapshot publication. It is streaming and bounded-memory; it does not re-read the source bytes and does not re-download the blob from Azure.

Chunk writes require this guard because zstd is a new Arius write path with a non-BCL codec dependency. Legacy gzip remains read-compatible only and does not need write-time verification.

The verifier confirms Arius can decode and hash the frame it just wrote. It is not an independent zstd conformance test; the release workflow's Python `zstandard` golden-file check provides separate conformance smoke coverage.

### Hardening and Audit Findings

The hardening audit in `2026-06-15-ztd/ZstdSharp Hardening Report - Audit vs Codebase.md` recorded safeguards that should stay true:

* Upload verification is bounded-memory and blocks chunk-index recording and snapshot publication on failure.
* Zstd frame checksums are enabled; truncated frames fail when restore or recovery reads to EOF.
* Compression disposal, verifier completion, encryption finalization, and byte counting happen in the required order.
* `ZSTD_c_nbWorkers = 0`; window size and long-distance matching are left at defaults.
* CI exercises codec round-trips and upload verification on x64 Linux, x64 Windows, and arm64 macOS. The release workflow also decodes a GCM plus zstd golden chunk with Python `zstandard`.
* `recover-chunk.py` handles gzip and zstd recovery without Arius and supports decrypt-only output for the `zstd` CLI.

### Benchmark Results

The table compares the completed gzip baseline with the completed verifier-on zstd run over the same mixed local dataset and fresh containers. Each run scanned 694 files, uploaded 671 new repository entries, deduped 23, uploaded 47 large chunks, and packed 624 small files into one tar chunk. Incomplete and verifier-disabled exploratory zstd runs are intentionally omitted.

| Run | Codec / verifier | Large stored MB | Tar stored MB | Large + tar stored MB | Upload elapsed | Total elapsed |
|---|---|---:|---:|---:|---:|---:|
| `gzip` | gzip | 2532.87 | 7.32 | 2540.19 | 19m51.7s | 19m55.9s |
| `zstd` | zstd, verifier on | 2447.86 | 5.94 | 2453.80 | 19m42.7s | 19m47.7s |

Against gzip, the completed verifier-on zstd run stored 86.39 MB less data and finished 8.3 seconds faster end-to-end: 85.01 MB saved on large chunks plus 1.38 MB saved on the tar chunk. These logs are not isolated CPU-only compression benchmarks, but they do show that the chosen zstd level and upload verifier are compatible with gzip-comparable end-to-end archive time on this representative dataset.

Representative large-file deltas from gzip to the completed verifier-on zstd run:

| Anonymous file type | gzip stored MB | zstd stored MB | zstd saved MB |
|---|---:|---:|---:|
| File A (`.mp4`) | 243.71 | 190.17 | 53.54 |
| File B (`.pptx`) | 297.43 | 290.05 | 7.38 |
| File C (`.pptx`) | 139.50 | 134.36 | 5.14 |
| File D (`.pdf`) | 18.17 | 14.29 | 3.88 |
| File E (`.pdf`) | 23.05 | 20.47 | 2.58 |
| File F (`.exe`) | 141.80 | 139.38 | 2.42 |
| File G (`.exe`) | 4.54 | 2.51 | 2.03 |
| File H (`.exe`) | 580.60 | 579.40 | 1.20 |

### Consequences and Tradeoffs

* Good, because new repository blobs use a standard zstd frame that can be decoded by other zstd implementations if Arius or ZstdSharp is unavailable later.
* Good, because zstd reduced stored chunk bytes by 86.39 MB on the representative archive while remaining gzip-comparable in the completed verifier-on run.
* Good, because `ICompressionService` removes compression knowledge from feature handlers and shared serializers, keeping codec mechanics in `Shared/Compression`.
* Good, because chunk upload verification catches non-restorable zstd chunk frames before a snapshot can reference them.
* Bad, because level 19 is not the usual fast zstd profile; on some already-compressed files it spends CPU for little storage gain.
* Bad, because `ZstdSharp.Port` is a managed port rather than native libzstd; performance and bug characteristics can differ from headline native zstd expectations.
* Bad, because legacy gzip read compatibility must remain tested alongside current zstd reads.

### Confirmation

This decision is being followed when all of the following are true:

* New chunk, tar, filetree, snapshot, and chunk-index blob bodies are written through `ICompressionService` as zstd frames.
* zstd writes use level 19 and enable the zstd frame checksum.
* zstd writes explicitly keep `ZSTD_c_nbWorkers = 0` and do not enable large-window or long-distance matching modes.
* Restore reads both new zstd frames and legacy gzip frames.
* zstd chunk uploads require inline round-trip verification before chunk-index entries are recorded.
* Archive does not publish a snapshot when round-trip verification fails.
* Compression remains streaming and bounded-memory for large chunks and tar chunks.
* Emergency chunk recovery remains possible without Arius through `recover-chunk.py` and standard zstd tooling.

## Pros and Cons of the Options

### Keep writing gzip with `CompressionLevel.SmallestSize`

This keeps the previous implementation unchanged.

* Good, because gzip is mature, ubiquitous, and already implemented.
* Good, because the BCL implementation avoids a new codec dependency.
* Bad, because the representative archive stored 86.39 MB more than zstd.
* Bad, because it leaves no abstraction boundary for future compression changes.

### Write zstd through `ZstdSharp.Port` behind `ICompressionService`

This is the chosen design.

* Good, because it writes standard RFC 8878 frames without native runtime deployment concerns.
* Good, because `ICompressionService` centralizes compression and legacy read compatibility.
* Good, because inline verification addresses the main recoverability risk of a new encoder.
* Bad, because level 19 can be slower than gzip on low-gain files.
* Bad, because the managed port can diverge from native zstd performance expectations.

### Use SharpCompress as the zstd provider

SharpCompress includes zstd support, but its zstd implementation is a vendored snapshot of ZstdSharp inside a broader archive library.

* Good, because it offers a stream-like zstd API.
* Bad, because Arius does not need SharpCompress archive container functionality.
* Bad, because a vendored codec snapshot can lag upstream ZstdSharp fixes.
* Bad, because SharpCompress zstd issue history includes archive-container bugs that are irrelevant to Arius but would still become dependency noise.

### Use a native libzstd wrapper such as ZstdNet

This would bind Arius more directly to the reference native implementation.

* Good, because native libzstd is the performance baseline most people mean when they say zstd is fast.
* Good, because it reduces managed-port translation risk.
* Bad, because it introduces native binary distribution and RID compatibility concerns across Windows, macOS, Linux, musl, single-file, and AOT scenarios.
* Bad, because Arius currently values simple deployment over extracting the last compression-throughput advantage.

## More Information

* Zstd context and risk evaluation: `openspec/changes/2026-06-15-ztd/CONVO.md`.
* Zstd implementation plan: `openspec/changes/2026-06-15-ztd/PLAN.md`.
* Zstd hardening audit: `openspec/changes/2026-06-15-ztd/ZstdSharp Hardening Report - Audit vs Codebase.md`.
* Benchmark log, gzip: `ariusci-testgzip/logs/2026-06-15_17-51-08_archive.txt`.
* Benchmark log, zstd verifier on: `ariusci-testzstd3/logs/2026-06-15_20-55-09_archive.txt`.
