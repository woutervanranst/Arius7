---
status: "proposed"
date: 2026-11-11
confidence: "medium"
supersedes: "ADR-0012 (codec provider only)"
---

# Use framework Zstandard without changing the repository format

## Context and Problem Statement

.NET 11 provides a framework-serviced zstd implementation. Arius can remove its production dependency on ZstdSharp.Port, but a backup tool must prove compatibility before shipping a different encoder. This changes the codec provider, not the RFC 8878 repository format or any content, chunk, or filetree identity.

## Decision Outcome

Use `System.IO.Compression.ZstandardStream` behind the existing `ICompressionService`. Preserve quality 15, append the content checksum, retain the level-derived compression window with long-distance matching disabled, and explicitly limit decoding to a 128 MiB window. Keep lazy gzip/zstd detection and zero-byte legacy payloads. Keep inline chunk-upload verification even though the encoder is now framework-serviced.

The .NET 11 encoder creates a fresh native context and never enables `ZSTD_c_nbWorkers`. Its public API has no worker-count setting. This preserves one compressor per stream without adding private interop; archive still parallelizes across chunks. Recheck this assumption when changing runtimes.

Corrupt and truncated frames now raise `InvalidDataException`, replacing `ZstdException` and `EndOfStreamException` respectively. A decode-window violation raises `IOException`. Cancellation is checked by the lazy wrapper even when the framework decoder could return buffered output without consulting the token.

ZstdSharp.Port 0.8.8 remains **only in tests and benchmarks** as an independent previous-codec compatibility oracle. It is not a production dependency.

### Compatibility and shipping gates

`ZstdCompatibilityTests` exercises both writer/reader directions for chunks, canonical filetrees, shards, and snapshots, with and without encryption. An existing encrypted zstd golden chunk anchors historical compatibility. `ZstdCompressionServiceTests` covers legacy gzip/empty payloads, checksum corruption, truncation including missing checksum bytes, the decode-window cap, cancellation, and sync/async stream ownership. `ZstdRecoveryTests` decodes newly written encrypted frames with `recover-chunk.py` and Python `zstandard`.

The `Compression compatibility` workflow runs on Windows x64, Linux x64, and macOS arm64. It also archives and lists empty, small, and large binary files using the published self-contained single-file CLI, then independently recovers the large and tar chunks, in plaintext and encrypted repositories. A separate Linux job runs the framework-dependent CLI inside the actual production Docker image, exercising that image's native runtime. These live probes use disposable containers and delete them afterward; missing credentials fail the gate rather than report success. Release builds and image publication depend on this workflow.

**Do not ship until all platform/deployment gates pass and their performance artifacts have been reviewed.** This ADR remains proposed until that evidence is accepted.

### Initial performance evidence

Local macOS arm64, .NET SDK 11.0.100/runtime 11.0.0, Release build, quality 15 with checksums, 2026-11-11. Each workload is 32 MiB, written in 80 KiB pieces; one warmup precedes three timed compressions and three timed decompressions. Each codec/workload runs in a fresh process. Peak RSS includes managed and native memory, the runtime, input, and compressed-output buffers; it is not incremental compressor allocation.

| Workload | Codec | Stored bytes | Compress MiB/s | Decode MiB/s | Peak RSS MiB |
|---|---|---:|---:|---:|---:|
| Repeated text | Previous | 3,155 | 16.7 | 2,633 | 158.6 |
| Repeated text | Framework | 3,155 | 32.9 | 4,378 | 165.7 |
| Half text / half seeded random | Previous | 16,781,910 | 119.6 | 2,942 | 190.3 |
| Half text / half seeded random | Framework | 16,781,910 | 168.0 | 6,302 | 182.0 |
| Seeded random | Previous | 33,555,213 | 12.7 | 1,031 | 206.0 |
| Seeded random | Framework | 33,555,213 | 86.1 | 6,859 | 195.8 |

These are synthetic codec measurements on a shared host, not a statistically controlled end-to-end archive benchmark. The observed ratio is unchanged and throughput improves in this sample, but memory is not uniformly lower. Do not extrapolate these timings to real repositories or infer a universal performance improvement. Review the other platforms' `compression-<rid>` JSON artifacts before shipping; representative real-repository archive performance remains a separate release decision.

Reproduce each measurement from the repository root, using a fresh Python process per invocation:

```bash
dotnet build src/Arius.Benchmarks/Arius.Benchmarks.csproj -c Release
python3 .github/scripts/measure-compression.py previous mixed
python3 .github/scripts/measure-compression.py framework mixed
```

Also run `text` and `random`. The driver uses per-child OS peak RSS on Unix and `PeakWorkingSet64` on Windows, so native allocations are included.

### Native deployment implications

Unlike the pure-managed port, the framework codec requires the runtime's native compression component. Official Microsoft .NET 11 runtime packs vendor zstd in that component; no separate system `libzstd` installation is required for those packs. Distribution-built .NET runtimes may instead link system zstd and must supply it.

Self-contained publication must retain the RID-specific native assets. Single-file publication keeps `IncludeNativeLibrariesForSelfExtract=true` and needs a writable native-extraction location. The existing .NET 11 container base supplies the framework-dependent native assets. Browser/WASI, custom runtime builds, musl, and AOT are not certified by the three supported release-RID gates.

## Alternatives

Keep ZstdSharp.Port in production until compatibility/performance evidence is accepted; this retains managed-only deployment but leaves codec servicing outside the framework. A separate native wrapper would add another dependency and deployment contract without improving the framework boundary.

## References

- [Issue 220](https://github.com/woutervanranst/Arius7/issues/220)
- [.NET 11 Zstandard API](https://github.com/dotnet/runtime/blob/v11.0.0/src/libraries/System.IO.Compression/ref/System.IO.Compression.cs)
- [Encoder initialization and parameters](https://github.com/dotnet/runtime/blob/v11.0.0/src/libraries/System.IO.Compression/src/System/IO/Compression/Zstandard/ZstandardEncoder.cs)
- [Native packaging](https://github.com/dotnet/runtime/blob/v11.0.0/src/native/libs/System.IO.Compression.Native/CMakeLists.txt)
- [Original zstd format decision](adr-0012-zstd-as-new-compression-algorithm.md)
