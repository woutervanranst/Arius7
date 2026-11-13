---
status: "proposed"
date: 2026-11-12
confidence: "medium"
supersedes: "ADR-0012 (codec provider and upload round-trip policy); ADR-0017 (codec round-trip guard only)"
---

# Replace ZstdSharp.Port with the .NET 11 Zstandard library

## Context and Problem Statement

Arius used `ZstdSharp.Port`, a third-party managed port, to read and write Zstandard (zstd) data. .NET 11 includes its own zstd implementation. The question: does Arius keep the third-party dependency or use the framework library?

The repository format does not change. Arius still writes RFC 8878 frames. No content, chunk, or filetree identity changes.

## Decision Outcome

Use `System.IO.Compression.ZstandardStream` behind `ICompressionService`. Remove the `ZstdSharp.Port` package. The framework receives the servicing updates, and Arius has one dependency less.

Keep the behavior of the old codec:

- Quality 15, content checksum on, window size from the quality level, long-distance matching off.
- A decoder window limit of 128 MiB.
- Lazy gzip/zstd detection on read. A zero-byte payload is empty content.

## Consequences

- **Errors.** A corrupt frame or a truncated frame now causes `InvalidDataException`. Before, they caused `ZstdException` and `EndOfStreamException`. A window violation causes `IOException`.
- **Threads.** The framework encoder does not use zstd worker threads, and it has no setting for them. Each stream uses one compressor. Archive still runs in parallel across chunks. Check this again when you change the runtime.
- **Native component.** The codec needs the native compression component of the runtime. Microsoft .NET 11 runtimes include zstd. A runtime that a Linux distribution builds can need the system zstd package. A self-contained publish must keep the native assets for its RID. A single-file build must keep `IncludeNativeLibrariesForSelfExtract=true` and needs a writable folder for extraction. These targets were not tested: Browser/WASI, custom runtime builds, musl, AOT.
- **Upload verification removed.** ADR-0012 verified each upload. The upload decompressed its own output and hashed it again, to protect against bugs in the third-party port. Arius now applies the same trust to the framework codec as to the BCL gzip codec. The upload does not verify its output. It copies the source bytes through compression and encryption and trusts the chunk hash that Arius calculated earlier. The protocol of ADR-0017 does not change: body first, metadata sentinel, snapshot last. Frame checksums, truncation and window checks, and GCM authentication on reads do not change.
- **Static sources.** The verifier also detected a file that changed between hashing and upload. Arius now assumes that source files do not change during an archive run. If a file changes, Arius can record its content under the wrong identity. The README and the CLI guide tell users to archive a stable copy or a filesystem snapshot. This changes the requirement of issue #220 to keep inline verification. The maintainer requested the change.
- **Removed migration items.** The cross-codec tests, deployment workflow, and benchmark harness were only for the migration. They remain in commits `0530dcad`, `c580bc04`, and `4be4269f`.

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
- **Verifier removal** (real `ChunkStorageService`, in-memory storage, 320 MiB per case): CPU time decreases by 6.7% to 33.6%. Peak RSS decreases by 7 to 77 MiB. Median throughput changes by -2% to +16%. The timing noise on the shared host is large. The test has no network and no multi-worker timings.

This ADR stays proposed until the Windows text regression is resolved and the maintainer accepts the evidence.

## Alternatives

- **Keep the third-party port.** Deployment stays managed-only, but the framework does not service the codec. A native wrapper adds a dependency and a deployment contract and gives no benefit over the framework.
- **Use the framework codec and keep the upload decoder.** This gives stronger protection against encoder bugs. It needs a second codec context, a tee, a bounded pipe, and a background task.
- **Keep only a source-copy hasher.** It still detects edits during an archive run. It adds CPU work and helper code for a condition that the static-source rule excludes.

## References

- [Issue 220](https://github.com/woutervanranst/Arius7/issues/220)
- [.NET 11 Zstandard API](https://github.com/dotnet/runtime/blob/v11.0.0/src/libraries/System.IO.Compression/ref/System.IO.Compression.cs)
- [Encoder initialization and parameters](https://github.com/dotnet/runtime/blob/v11.0.0/src/libraries/System.IO.Compression/src/System/IO/Compression/Zstandard/ZstandardEncoder.cs)
- [Native packaging](https://github.com/dotnet/runtime/blob/v11.0.0/src/native/libs/System.IO.Compression.Native/CMakeLists.txt)
- [ADR-0012: original zstd format decision](adr-0012-zstd-as-new-compression-algorithm.md)
