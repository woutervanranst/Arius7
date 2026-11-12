---
status: "proposed"
date: 2026-11-12
confidence: "medium"
supersedes: "ADR-0012 (codec provider and upload round-trip policy); ADR-0017 (codec round-trip guard only)"
---

# Use framework Zstandard without changing the repository format

## Context and Problem Statement

.NET 11 provides a framework-serviced zstd implementation. Arius can remove its previous managed-codec dependency, but a backup tool must prove compatibility before shipping a different encoder. This changes the codec provider, not the RFC 8878 repository format or any content, chunk, or filetree identity.

## Decision Outcome

Use `System.IO.Compression.ZstandardStream` behind the existing `ICompressionService`. Preserve quality 15, append the content checksum, retain the level-derived compression window with long-distance matching disabled, and explicitly limit decoding to a 128 MiB window. Keep lazy gzip/zstd detection and zero-byte legacy payloads.

Remove per-upload codec round-trip verification and its incidental detection of source changes between hashing and upload. Source files are assumed **static throughout an archive run**; upload trusts the previously computed chunk hash, including a hashcache reuse decision. Copy source bytes directly into compression without another hash pass. The compression frame, encryption trailer, and storage write must all finalize successfully before commit metadata. ADR-0017's body-first, metadata-sentinel, snapshot-last protocol is unchanged.

This intentionally revises issue #220's original requirement to retain inline codec verification, following the maintainer's request after reviewing the framework migration. The maintainer explicitly accepted static sources and removal of the verifier's source-change-detection side effect.

The .NET 11 encoder creates a fresh native context and never enables `ZSTD_c_nbWorkers`. Its public API has no worker-count setting. This preserves one compressor per stream without adding private interop; archive still parallelizes across chunks. Recheck this assumption when changing runtimes.

Corrupt and truncated frames now raise `InvalidDataException`, replacing `ZstdException` and `EndOfStreamException` respectively. A decode-window violation raises `IOException`. Cancellation is checked by the lazy wrapper even when the framework decoder could return buffered output without consulting the token.

The previous codec dependency, temporary cross-codec adapter, and compression benchmark harness are removed after migration validation. Their source remains available in the migration commits. Golden-file and independent recovery tests provide ongoing coverage without keeping the old library in any project.

### Why remove the upload decoder

The original zstd plan and commit `a7e7f4e4` added the tee to defend against an encoder bug in the managed port. Commit `2f0fac01` made the trust distinction explicit: BCL gzip skipped verification; `a891c8ac` added the no-op verifier, `956b96aa` extracted the infrastructure, and `ba024b8e` hardened its pipe disposal. The secondary branch **decompressed and hashed pre-encryption bytes**; it never decrypted output or checked bytes downloaded from storage.

Apply the existing BCL-codec trust policy to framework zstd. Remove `IUploadVerifier`, `RoundTripVerifier`, `NoopVerifier`, `TeeStream`, the bounded verification pipe/background task, and the codec capability flag. Do not retain an upload-time hasher solely to preserve the original verifier's source-change-detection side effect.

This is a deliberate defense-in-depth tradeoff, not proof that Microsoft code cannot have bugs: encoder defects are no longer checked on each upload. Golden-file, independent Python recovery, and archive/restore tests cover the encoder instead. Frame checksums, truncation/window rejection, and GCM authentication remain unchanged. They do not detect a source changing after initial hashing: changing a file during archive violates the static-source precondition and may record content under the wrong identity.

### Compatibility coverage

Temporary cross-codec tests exercised both writer/reader directions for chunks, canonical filetrees, shards, and snapshots, with and without encryption during migration. The retained `ZstdCompatibilityTests` reads an existing encrypted zstd golden chunk to anchor historical compatibility. `ZstdCompressionServiceTests` covers legacy gzip/empty payloads, checksum corruption, truncation including missing checksum bytes, the decode-window cap, cancellation, and sync/async stream ownership. `ZstdRecoveryTests` decodes newly written encrypted frames with `recover-chunk.py` and Python `zstandard`.

These tests remain in the normal suites on Windows x64, Linux x64, and macOS arm64. Integration recovery tests archive encrypted large and tar chunks, then independently recover their contents; the representative E2E workflow covers archive/restore behavior. Python-based tests visibly skip when their interpreter or libraries are unavailable. The existing release workflow installs those dependencies and verifies Python recovery against historical golden files; its configuration is unchanged.

The migration-only deployment/benchmark workflow and its two Python drivers were removed after collecting the evidence below: their codec/recovery coverage largely duplicated the test suites, and shared-runner measurements were not performance thresholds. They are not ongoing release dependencies. The published executable/container probes provided packaging evidence during migration; in-process tests do not replace that distinct check.

This ADR remains proposed until compatibility and performance evidence is accepted. In particular, the Windows text regressions below remain a release decision.

### Performance evidence

Local macOS arm64, .NET SDK 11.0.100/runtime 11.0.0, Release build, quality 15 with checksums, 2026-11-12. Each workload is 32 MiB, written in 80 KiB pieces; one warmup precedes three timed compressions and three timed decompressions. Each codec/workload runs in a fresh process. CPU time is process-wide user + kernel time over the timed region, including background/native work. Peak RSS includes managed and native memory, the runtime, input, and compressed-output buffers; it is not incremental compressor allocation.

| Workload | Codec | Stored bytes | Compress MiB/s | Encode CPU ms / 96 MiB | Decode MiB/s | Decode CPU ms / 96 MiB | Peak RSS MiB |
|---|---|---:|---:|---:|---:|---:|---:|
| Repeated text | Previous | 3,155 | 72.7 | 1,333 | 6,626 | 14.4 | 173.0 |
| Repeated text | Framework | 3,155 | 78.3 | 1,226 | 9,582 | 10.0 | 163.9 |
| Half text / half seeded random | Previous | 16,781,910 | 325.7 | 296 | 8,789 | 10.9 | 187.7 |
| Half text / half seeded random | Framework | 16,781,910 | 338.7 | 279 | 11,325 | 8.5 | 179.9 |
| Seeded random | Previous | 33,555,213 | 187.4 | 541 | 10,226 | 9.4 | 206.0 |
| Seeded random | Framework | 33,555,213 | 231.3 | 414 | 8,934 | 10.7 | 195.7 |

These follow-up measurements replace the initial 2026-11-11 local sample, which was substantially slower and noisier. Both were taken on a shared host; neither is a controlled end-to-end archive benchmark. Decode timings are particularly short. Stored sizes match, but do not infer a universal speed or memory improvement.

#### Cross-platform codec evidence

[Compatibility run 43101837735](https://github.com/woutervanranst/Arius7/actions/runs/43101837735) passed all three release RIDs and the production Linux-container deployment at commit `0530dcad`. Its `compression-<rid>` artifacts contain fresh-process codec measurements. Relative to the previous codec:

| RID | Workload | Encode throughput change | Decode throughput change | Peak RSS change |
|---|---|---:|---:|---:|
| Linux x64 | Text | -5.1% | +65.1% | -7.3 MiB |
| Linux x64 | Mixed | +56.6% | +52.6% | -7.5 MiB |
| Linux x64 | Random | +56.0% | +5.2% | -10.1 MiB |
| Windows x64 | Text | -26.1% | -42.4% | -7.9 MiB |
| Windows x64 | Mixed | +45.6% | +51.0% | -9.1 MiB |
| Windows x64 | Random | +57.4% | -8.2% | -11.2 MiB |
| macOS arm64 | Text | +26.2% | +21.3% | -7.7 MiB |
| macOS arm64 | Mixed | +31.1% | +21.0% | -8.2 MiB |
| macOS arm64 | Random | +28.4% | +16.8% | -10.1 MiB |

Stored sizes matched in every case. These short single-sample CI runs did not yet collect CPU time. In particular, the Windows text regressions need representative follow-up before shipping; compatibility success is not performance acceptance.

#### Upload verification overhead

Same local host and data, using the real `ChunkStorageService` with cached file-backed input and an in-memory storage double. Compare the framework codec with its old decoder/tee verifier against the final direct-copy pipeline (no decoder or upload-time hash), using an identical harness. Commit `c580bc04` contains the harness with the original verifier pipeline, unchanged from `0530dcad`, for reproducing the baseline. Each fresh process does one warmup and ten timed 32 MiB uploads; the table shows medians of three processes per case, interleaved before/after with reversed order in the middle repetition. Average CPU utilization uses **100% = one core**, not percent of the whole machine.

| Workload | Encryption | Upload MiB/s, before / after | CPU ms / 320 MiB, before / after | CPU %, before / after | Peak RSS MiB, before / after |
|---|---|---:|---:|---:|---:|
| Text | Plaintext | 75.5 / 74.0 | 4,703 / 4,387 | 111 / 102 | 170.2 / 160.9 |
| Text | GCM | 71.9 / 71.9 | 4,873 / 4,548 | 110 / 102 | 171.3 / 162.8 |
| Mixed | Plaintext | 273.5 / 318.1 | 1,768 / 1,174 | 150 / 117 | 538.1 / 494.0 |
| Mixed | GCM | 246.9 / 265.1 | 1,913 / 1,388 | 148 / 115 | 309.4 / 302.1 |
| Random | Plaintext | 180.5 / 183.8 | 2,415 / 1,913 | 136 / 109 | 614.5 / 537.3 |
| Random | GCM | 149.3 / 169.8 | 2,748 / 2,026 | 130 / 108 | 624.8 / 610.1 |

Stored sizes were unchanged. Removing the verifier and upload-time rehash reduced measured CPU time by **6.7-33.6%**, and median process peak RSS by **7.3-77.2 MiB**. Median throughput changes ranged from **-2.0% to +16.3%**: repeated text was essentially flat/slightly slower, while mixed and random samples improved. This supports lower resource consumption and less infrastructure, not a universal archive-speed improvement.

The storage double retains/copies complete bodies, and RSS includes runtime/GC effects, body buffers, and all iterations. These peaks are not the production streaming memory budget or per-decoder allocations. Shared-host timing noise is substantial: mixed GCM baseline throughput ranged from 158 to 273 MiB/s across the three processes. File input is warm in the OS cache, and there are no Azure/network or multi-chunk-worker timings here. Real-repository throughput and the platform regressions remain release decisions.

The temporary codec/upload benchmark harness and its Python memory collector have been removed. Commit `c580bc04` preserves the verifier-on baseline and commit `4be4269f` preserves the direct-copy pipeline with the same harness. Restore those historical revisions if these measurements need repeating; no compression-specific benchmark commands or legacy codec dependency remain in the current tree.

### Native deployment implications

Unlike the pure-managed port, the framework codec requires the runtime's native compression component. Official Microsoft .NET 11 runtime packs vendor zstd in that component; no separate system `libzstd` installation is required for those packs. Distribution-built .NET runtimes may instead link system zstd and must supply it.

Self-contained publication must retain the RID-specific native assets. Single-file publication keeps `IncludeNativeLibrariesForSelfExtract=true` and needs a writable native-extraction location. The existing .NET 11 container base supplies the framework-dependent native assets. The migration probes exercised Windows x64, Linux x64, macOS arm64, and the production Linux container, not Browser/WASI, custom runtime builds, musl, or AOT.

## Alternatives

Keep the previous managed codec in production until compatibility/performance evidence is accepted; this retains managed-only deployment but leaves codec servicing outside the framework. A separate native wrapper would add another dependency and deployment contract without improving the framework boundary.

Keep the per-upload decoder even with the framework codec: stronger encoder-bug defense, but an extra codec context, tee, bounded pipe, and background task. Keep only a source-copy hasher: still detects files changing after initial hashing, but adds CPU work and API/helper machinery solely for a condition excluded by the maintainer-approved static-source contract. The chosen direct-copy pipeline removes both.

## References

- [Issue 220](https://github.com/woutervanranst/Arius7/issues/220)
- [.NET 11 Zstandard API](https://github.com/dotnet/runtime/blob/v11.0.0/src/libraries/System.IO.Compression/ref/System.IO.Compression.cs)
- [Encoder initialization and parameters](https://github.com/dotnet/runtime/blob/v11.0.0/src/libraries/System.IO.Compression/src/System/IO/Compression/Zstandard/ZstandardEncoder.cs)
- [Native packaging](https://github.com/dotnet/runtime/blob/v11.0.0/src/native/libs/System.IO.Compression.Native/CMakeLists.txt)
- [Original zstd format decision](adr-0012-zstd-as-new-compression-algorithm.md)
