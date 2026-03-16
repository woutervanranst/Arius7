## Why

Backup and restore are entirely sequential — every file, chunk, pack seal, upload, and download happens one at a time. On large datasets this means the CPU idles while waiting for network I/O and vice versa. Restore also keeps all downloaded packs in memory forever, causing OOM on large restores. Parallelizing the pipelines will yield 2–5× throughput improvements while fixing the memory problem.

## What Changes

- **Parallel backup pipeline** — 4-stage Channel-based producer-consumer: file processors (N workers chunk + hash + dedup) → pack accumulator (1 consumer) → seal workers (M workers TAR+gzip+AES) → uploaders (P workers). Atomic dedup via `ConcurrentDictionary.TryAdd`.
- **Parallel restore pipeline** — 3-phase: plan (collect needed packs) → fetch packs to temp directory on disk with N parallel downloaders (decrypt + extract blobs to `{tempDir}/{hash}.bin`) → assemble output files with M parallel writers → cleanup temp dir. Eliminates unbounded in-memory pack cache.
- **Parallel index loading** — Download and parse all `index/` blobs concurrently in `AzureRepository.LoadIndexAsync`.
- **Configurable concurrency** — New `ParallelismOptions` record with per-stage limits. Defaults: `ProcessorCount` for CPU-bound, `4` for network-bound. Exposed via `BackupRequest`/`RestoreRequest`.
- **Chunk-level dedup statistics** — New counters: total/new/deduplicated chunks and bytes. Richer `BackupCompleted` event.
- **Error events** — **BREAKING**: New `BackupFileError` and `RestoreFileError` events. Modified `BackupCompleted` and `RestoreCompleted` with `Failed` count. Collect-and-report: individual file failures don't cancel the operation.
- **Humanizer integration** — Add `Humanizer.Core` to CLI for human-readable byte sizes, durations, quantities, and relative timestamps. Replace manual `FormatBytes` helpers.
- **Concurrency test suite** — 3-layer strategy: deterministic race tests (barrier-based, TUnit), stress tests (high-volume parallel backup+restore+verify), systematic interleaving exploration (Microsoft Coyote in a separate test project targeting net8.0).

## Capabilities

### New Capabilities
- `parallel-pipeline`: Defines the parallel backup and restore pipeline architecture — stage definitions, channel topology, backpressure, concurrency configuration, and memory budget constraints.
- `concurrency-testing`: Testing strategy for concurrent correctness — dedup atomicity, channel deadlock detection, pipeline completion, and data integrity under contention.

### Modified Capabilities
- `cli`: New error events (`BackupFileError`, `RestoreFileError`) to display, Humanizer-based formatting, dedup statistics in completion output, parallelism CLI options.
- `restore`: Disk-based temp staging replaces in-memory pack cache, parallel pack fetching, parallel file assembly, temp dir cleanup.

## Impact

- **Arius.Core**: `BackupHandler`, `RestoreHandler` — full rewrites (same `IStreamRequestHandler` interface). `AzureRepository.LoadIndexAsync` — parallel implementation. New `ParallelismOptions` model. New event types in contracts.
- **Arius.Cli**: All command files updated for new events + Humanizer formatting. New `--parallelism` CLI option. New dependency: `Humanizer.Core`.
- **Arius.Api / Arius.Web**: Must handle new event types (`BackupFileError`, `RestoreFileError`). No other changes.
- **Tests**: New `Concurrency/` test directory in `Arius.Core.Tests`. New `Arius.Coyote.Tests` project (net8.0). Existing workflow tests updated for new event shapes.
- **Dependencies**: `Humanizer.Core` (Cli only), `Microsoft.Coyote` + `Microsoft.Coyote.Test` (Coyote test project only).
- **Breaking**: Consumers of `BackupEvent`/`RestoreEvent` streams must handle new event subtypes. `BackupCompleted`/`RestoreCompleted` have additional fields.
