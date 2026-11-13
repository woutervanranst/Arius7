---
status: "proposed"
date: 2026-11-12
confidence: "medium"
supersedes: "ADR-0012 (codec provider and upload round-trip policy); ADR-0017 (codec round-trip guard only)"
---

# Use framework Zstandard without upload-time re-verification

## Context and Problem Statement

.NET 11 includes a Zstandard (zstd) implementation. Arius can use it and remove the managed port (`ZstdSharp.Port`). This change affects the codec provider only. The RFC 8878 repository format does not change. No content, chunk, or filetree identity changes.

ADR-0012 also verified each upload. The upload decompressed the compressed bytes and hashed them again. This check protected against bugs in the managed port.

The question: which codec provider does Arius use, and does the upload verifier stay?

## Decision Outcome

**Provider.** Use `System.IO.Compression.ZstandardStream` behind `ICompressionService`. Keep these settings: quality 15, content checksum on, window size from the quality level, long-distance matching off. Limit the decoder window to 128 MiB. Keep the lazy gzip/zstd detection. Keep the rule that a zero-byte payload is empty content.

**Verification.** Remove the upload verifier: `IUploadVerifier`, `RoundTripVerifier`, `NoopVerifier`, `TeeStream`, the verification pipe, and the codec capability flag. An upload now copies the source bytes through compression and encryption. It trusts the chunk hash that Arius calculated earlier, including a hashcache reuse decision. Compression, encryption, and the storage write must all finish before Arius writes the commit metadata. The protocol of ADR-0017 does not change: body first, metadata sentinel, snapshot last.

**Static sources.** Arius assumes that source files do not change during an archive run. The verifier detected a file that changed between hashing and upload. This ADR removes that detection on purpose. If a file changes during an archive run, Arius can record its content under the wrong identity. The README and the CLI guide tell users to archive a stable copy or a filesystem snapshot. This decision changes the requirement of issue #220 to keep inline verification. The maintainer requested the change.

**Risk.** Arius no longer checks for encoder bugs on each upload. This removes one layer of protection. It does not prove that the framework code has no bugs. Tests give the coverage (see below). These protections do not change: frame checksums, truncation and window checks, and GCM authentication on reads.

## Consequences

- A corrupt frame or a truncated frame now causes `InvalidDataException`. Before, they caused `ZstdException` and `EndOfStreamException`. A window violation causes `IOException`.
- The framework encoder does not use zstd worker threads, and it has no setting for them. Each stream uses one compressor. Archive still runs in parallel across chunks. Check this again when you change the runtime.
- The codec needs the native compression component of the runtime. Microsoft .NET 11 runtimes include zstd. A runtime that a Linux distribution builds can need the system zstd package.
- A self-contained publish must keep the native assets for its RID. A single-file build must keep `IncludeNativeLibrariesForSelfExtract=true` and needs a writable folder for extraction.
- These targets were not tested: Browser/WASI, custom runtime builds, musl, AOT.
- The migration-only cross-codec tests, deployment workflow, and benchmark harness are removed. They remain in commits `0530dcad`, `c580bc04`, and `4be4269f`.

## Compatibility coverage

- `ZstdCompatibilityTests` reads a historical encrypted zstd golden chunk.
- `ZstdCompressionServiceTests` covers these cases: legacy gzip and empty payloads, checksum corruption, truncation (including missing checksum bytes), the window limit, cancellation, and stream ownership.
- `RecoveryScriptTests` (integration) archives encrypted large chunks and tar chunks through the real pipeline. It then recovers them with `recover-chunk.py`. The release workflow runs the same script on the golden files.
- The representative E2E workflow covers archive and restore.
- The tests run on Windows x64, Linux x64, and macOS arm64. A Python-based test shows a skip reason when Python or its libraries are not available.

## Performance evidence

The local measurements use macOS arm64 on a shared host. Each case uses a 32 MiB workload in a new process. The CI measurement is one short run on all three RIDs. The results show lower resource use. They do not show a general speed increase.

- **Codec, local:** The stored sizes are the same as with the previous codec. Compression throughput changes by +4% to +23%. Decode throughput changes by -13% to +45%, depending on the workload.
- **Codec, CI** ([run 43101837735](https://github.com/woutervanranst/Arius7/actions/runs/43101837735)): Encode throughput for mixed and random data increases by 28% to 57% on all RIDs. **On Windows x64, text data is slower (encode -26%, decode -42%).** Investigate this with representative data before release. Peak RSS decreases by 7 to 11 MiB on all RIDs.
- **Verifier removal** (real `ChunkStorageService`, in-memory storage, 320 MiB per case): CPU time decreases by 6.7% to 33.6%. Peak RSS decreases by 7 to 77 MiB. Median throughput changes by -2% to +16%. Repeated text is almost the same. Mixed and random data are faster. The timing noise on the shared host is large. The test has no network and no multi-worker timings.

This ADR stays proposed until the Windows text regression is resolved and the maintainer accepts the evidence.

## Alternatives

- **Keep the managed codec.** Deployment stays managed-only, but the framework does not service the codec. A native wrapper adds a dependency and a deployment contract and gives no benefit over the framework.
- **Keep the upload decoder.** It gives stronger protection against encoder bugs. It needs a second codec context, a tee, a bounded pipe, and a background task.
- **Keep only a source-copy hasher.** It still detects edits during an archive run. It adds CPU work and helper code for a condition that the static-source rule excludes.

## References

- [Issue 220](https://github.com/woutervanranst/Arius7/issues/220)
- [.NET 11 Zstandard API](https://github.com/dotnet/runtime/blob/v11.0.0/src/libraries/System.IO.Compression/ref/System.IO.Compression.cs)
- [Encoder initialization and parameters](https://github.com/dotnet/runtime/blob/v11.0.0/src/libraries/System.IO.Compression/src/System/IO/Compression/Zstandard/ZstandardEncoder.cs)
- [Native packaging](https://github.com/dotnet/runtime/blob/v11.0.0/src/native/libs/System.IO.Compression.Native/CMakeLists.txt)
- [ADR-0012: original zstd format decision](adr-0012-zstd-as-new-compression-algorithm.md)
