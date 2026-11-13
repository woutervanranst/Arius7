---
status: "proposed"
date: 2026-11-12
confidence: "medium"
supersedes: "ADR-0012 (codec provider and upload round-trip policy); ADR-0017 (codec round-trip guard only)"
---

# Use framework Zstandard without upload-time re-verification

## Context and Problem Statement

.NET 11 ships a framework-serviced zstd implementation, so Arius can drop its managed port (`ZstdSharp.Port`). This changes the codec provider only, not the RFC 8878 repository format or any content, chunk, or filetree identity. ADR-0012 also verified every upload by decompressing and re-hashing the compressed bytes, to guard against bugs in that managed port.

The question: which codec provider do we use, and does the upload-time round-trip verifier stay?

## Decision Outcome

**Provider.** Use `System.IO.Compression.ZstandardStream` behind the existing `ICompressionService`. Preserve quality 15, the content checksum, the level-derived window, and no long-distance matching. Cap the decoder window at 128 MiB. Keep lazy gzip/zstd detection and the zero-byte legacy payload rule.

**Verification.** Remove the per-upload verifier (`IUploadVerifier`, `RoundTripVerifier`, `NoopVerifier`, `TeeStream`, the verification pipe and the codec capability flag). Upload now copies source bytes straight through compression and encryption, and trusts the chunk hash computed earlier, including a hashcache reuse decision. The compression frame, encryption trailer and storage write must all finalize before the commit metadata is written, so ADR-0017's body-first, metadata-sentinel, snapshot-last protocol is unchanged.

**Static sources.** Source files are assumed unchanged throughout an archive run. The verifier incidentally detected a file changing between hashing and upload; that detection is deliberately dropped. A file edited mid-archive may be recorded under the wrong identity, so the README and CLI guide tell users to archive a stable copy or filesystem snapshot. This revises issue #220's requirement to keep inline verification, at the maintainer's request.

**Tradeoff.** Encoder bugs are no longer checked on every upload; this is defence in depth traded for less CPU and less machinery, not proof that framework code is bug-free. Coverage moves to tests (below). Frame checksums, truncation and window rejection, and GCM authentication on reads are unchanged.

## Consequences

- Corrupt and truncated frames now throw `InvalidDataException` (previously `ZstdException` / `EndOfStreamException`); a window violation throws `IOException`.
- The framework encoder never enables zstd worker threads and exposes no setting for it, so each stream uses one compressor and archive still parallelizes across chunks. Recheck this when changing runtimes.
- The codec needs the runtime's native compression component. Official Microsoft .NET 11 runtimes bundle zstd; distribution-built runtimes may need system zstd. Self-contained publishing must keep the RID-specific native assets, and single-file builds keep `IncludeNativeLibrariesForSelfExtract=true` with a writable extraction location. Not exercised: Browser/WASI, custom runtime builds, musl, AOT.
- The migration-only cross-codec tests, deployment workflow and benchmark harness are removed; they live in the migration commits (`0530dcad`, `c580bc04`, `4be4269f`).

## Compatibility coverage

- `ZstdCompatibilityTests` reads a historical encrypted zstd golden chunk.
- `ZstdCompressionServiceTests` covers legacy gzip and empty payloads, checksum corruption, truncation (including missing checksum bytes), the window cap, cancellation and stream ownership.
- `RecoveryScriptTests` (integration) archives encrypted large and tar chunks through the real pipeline and recovers them with `recover-chunk.py`. The release workflow runs the same script against the golden files.
- The representative E2E workflow covers archive and restore. All of this runs on Windows x64, Linux x64 and macOS arm64; Python-based tests visibly skip when Python or its libraries are missing.

## Performance evidence

Local macOS arm64 measurements on a shared host (32 MiB workloads, fresh process per case), plus one short CI run on all three RIDs. These show lower resource use, not a universal speed-up.

- **Codec, local:** stored sizes are identical to the previous codec. Compression throughput is +4% to +23%; decode throughput varies by workload (-13% to +45%).
- **Codec, CI** ([run 43101837735](https://github.com/woutervanranst/Arius7/actions/runs/43101837735)): mixed and random data improve 28-57% on encode on every RID. **Windows x64 text regressed (encode -26%, decode -42%)** and needs representative follow-up before release. Peak RSS drops 7-11 MiB everywhere.
- **Removing the verifier** (real `ChunkStorageService`, in-memory storage, 320 MiB per case): CPU time -6.7% to -33.6%, peak RSS -7 to -77 MiB, median throughput -2% to +16% (repeated text flat, mixed and random faster). Timing noise on the shared host is substantial, and there are no network or multi-worker timings.

This ADR stays proposed until the Windows text regression is resolved and the evidence is accepted.

## Alternatives

- **Keep the managed codec:** managed-only deployment, but codec servicing stays outside the framework. A native wrapper adds a dependency and deployment contract for no gain over the framework.
- **Keep the per-upload decoder:** stronger encoder-bug defence, at the cost of a second codec context, a tee, a bounded pipe and a background task.
- **Keep only a source-copy hasher:** still detects mid-archive edits, but adds CPU work and helper code for a condition the static-source contract excludes.

## References

- [Issue 220](https://github.com/woutervanranst/Arius7/issues/220)
- [.NET 11 Zstandard API](https://github.com/dotnet/runtime/blob/v11.0.0/src/libraries/System.IO.Compression/ref/System.IO.Compression.cs)
- [Encoder initialization and parameters](https://github.com/dotnet/runtime/blob/v11.0.0/src/libraries/System.IO.Compression/src/System/IO/Compression/Zstandard/ZstandardEncoder.cs)
- [Native packaging](https://github.com/dotnet/runtime/blob/v11.0.0/src/native/libs/System.IO.Compression.Native/CMakeLists.txt)
- [ADR-0012: original zstd format decision](adr-0012-zstd-as-new-compression-algorithm.md)
