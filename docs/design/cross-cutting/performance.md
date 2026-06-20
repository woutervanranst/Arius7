# Performance and tuning constants

> **Code:** `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs` · `RestoreCommand/RestoreCommandHandler.cs` · `Shared/ChunkIndex/ChunkIndexService.cs` · `Shared/Compression/ZstdCompressionService.cs` · `Shared/ChunkStorage/UploadVerifier.cs` · `Shared/FileTree/FileTreeBuilder.cs` · benchmarks under `src/Arius.Benchmarks`, `src/Arius.AzureBlob.Benchmarks`
> · **Decisions:** [ADR-0012 zstd codec](../../decisions/adr-0012-zstd-as-new-compression-algorithm.md) · [ADR-0015 chunk-index scalability](../../decisions/adr-0015-chunk-index-scalability.md)
> · **Terms:** [chunk](../../glossary.md#chunk) · [large chunk](../../glossary.md#large-chunk) · [tar chunk](../../glossary.md#tar-chunk) · [shard](../../glossary.md#shard) · [chunk index](../../glossary.md#chunk-index) · [chunk size](../../glossary.md#chunk-size)

## Purpose

Arius is a backup tool: correctness, durability, and bounded resource use outrank raw throughput, but a repository can hold terabytes of data and many thousands of files, so the pipelines must still parallelize and stream. This doc inventories the tuning constants — fan-out worker counts, channel/pipe bounds, the small-file tar threshold, the [shard](../../glossary.md#shard) split threshold, and the zstd level — explains *why* each is set where it is, and shows how the benchmark projects validate the end-to-end shape rather than picking the numbers.

## How it works

Every tuning knob is a `const` next to the stage that uses it — there is no central config object and no operator-facing knob (the only per-run options are `SmallFileThreshold`, `TarTargetSize`, and `UploadTier` on `ArchiveCommandOptions`). The constants fall into three groups: **fan-out** (how many concurrent operations a stage runs), **bounds** (channel/pipe capacities that supply backpressure), and **codec/routing** (the zstd level and the shard/tar size thresholds).

### Fan-out per stage

Concurrency is expressed as `Parallel.ForEachAsync(..., new ParallelOptions { MaxDegreeOfParallelism = <const> })` or as a bounded channel sized to the worker count. The counts are tuned per *what each stage is bound by*: CPU-bound stages stay near core count, blob-I/O-bound stages run wider, and stages that do tiny local work run widest.

| Stage | Const | Value | Bound by | Source |
|---|---|---:|---|---|
| Hash files | `HashWorkers` | 4 | CPU (hash + zstd compress) | `ArchiveCommandHandler.cs` |
| Upload [large chunks](../../glossary.md#large-chunk) | `UploadWorkers` | 4 | blob PUT + inline verify | `ArchiveCommandHandler.cs` |
| Upload sealed [tar chunks](../../glossary.md#tar-chunk) | `TarUploadWorkers` | 2 | large blob PUT (64 MB bundles) | `ArchiveCommandHandler.cs` |
| Record thin index entries | `ThinEntryWorkers` | 64 | local SQLite writes | `ArchiveCommandHandler.cs` |
| Write filetree entries | `FileTreeUpdateWorkers` | 16 | local staging I/O | `ArchiveCommandHandler.cs` |
| Flush [shards](../../glossary.md#shard) | `FlushWorkers` | 32 | shard blob read-merge-write | `ChunkIndexService.cs` |
| Cold-cache prefix load | `PrefixLoadWorkers` | 8 | subtree listing + shard download | `ChunkIndexService.cs` |
| Restore download | `DownloadWorkers` | 4 | blob GET + decrypt/decompress | `RestoreCommandHandler.cs` |
| Restore route files | `RouteWorkers` | 8 | index lookup | `RestoreFilePipeline.cs` |
| Filetree synchronize | `SynchronizeWorkers` | 32 | staging directory I/O | `FileTreeBuilder.cs` |
| Filetree sibling subtrees | `SiblingSubtreeWorkers` | 4 | recursion fan-out | `FileTreeBuilder.cs` |
| Bulk chunk delete (repair) | `DeleteWorkers` | 16 | blob DELETE | `ChunkStorageService.cs` |

The asymmetry is deliberate: `ThinEntryWorkers = 64` (cheap local index writes after a tar is uploaded) and `FlushWorkers = 32` (independent shard blobs, each a small read-merge-write) run far wider than `UploadWorkers = 4` / `TarUploadWorkers = 2`, where each unit moves a whole large blob and the bottleneck is the network, not concurrency.

### Bounds and backpressure

Long-running handlers are channel-connected stages with **bounded** channels so a slow downstream stage backpressures upstream instead of buffering the whole repository in memory (see [memory-boundedness](./memory-boundedness.md) for the full argument). The capacities:

| Bound | Const | Value | Role |
|---|---|---:|---|
| Archive file-pair channel | `ChannelCapacity` | 64 | feeds dedup/router; caps in-flight files |
| Archive sealed-tar channel | (= `TarUploadWorkers`) | 2 | one slot per tar uploader |
| Restore chunk channel | (= `DownloadWorkers`) | 4 | one slot per download worker |
| Filetree upload channel | `UploadChannelCapacity` | 16 | caps in-flight filetree blob writes |
| Inline verify pipe (pause) | `PauseWriterThreshold` | `1<<20` (~1 MiB) | caps compressed bytes buffered mid-verify |
| Inline verify pipe (resume) | `ResumeWriterThreshold` | `1<<19` | hysteresis so the writer is not toggled per byte |

The `RoundTripVerifier` pipe bound is the one that keeps a *single chunk* bounded regardless of its size: the verifier tees compressed bytes to a `System.IO.Pipelines.Pipe` whose `PauseWriterThreshold` parks the upload tee once ~1 MiB is buffered, so a multi-GB [large chunk](../../glossary.md#large-chunk) verifies in flat memory while it streams to blob storage. See [chunk-storage](../core/shared/chunk-storage.md) and [compression](../core/shared/compression.md) for the tee/verify shape; ADR-0012 records why every new zstd chunk is verified inline.

### The small-file tar threshold

```mermaid
flowchart LR
    F[File from scan] --> Q{size ≥ SmallFileThreshold<br/>(1 MB)?}
    Q -- yes --> L["largeChannel → 1 file = 1 large chunk"]
    Q -- no --> S["smallChannel → TarBuilder"]
    S --> T{tar bundle ≥ TarTargetSize<br/>(64 MB)?}
    T -- yes --> Seal[Seal tar → 1 tar chunk + N thin entries]
    T -- no --> Acc[Accumulate]
```

`SmallFileThreshold = 1 MB` (`ArchiveCommandOptions`) routes the file: a [large chunk](../../glossary.md#large-chunk) is one file = one blob; below the threshold files are packed by `TarBuilder` into [tar chunks](../../glossary.md#tar-chunk) sealed at `TarTargetSize = 64 MB`. The threshold exists because thousands of tiny blobs would multiply per-file blob round-trips and Azure transaction cost (the AGENTS "Scale And Durability" guidance: *avoid per-file remote round-trips*). Bundling turns N small uploads into one large-blob PUT plus N cheap local thin-entry records — which is exactly why `ThinEntryWorkers` (64) is set so much higher than the upload workers.

### Codec: zstd level

`ZstdCompressionService.DefaultCompressionLevel` is the single compression size/speed knob. **The code value is `15`** (`ZstdCompressionService.cs`), set by a deliberate "tune compression level" change after the codec was adopted. ADR-0012 records the original choice of level **19** (favouring ratio, matching the old gzip `SmallestSize` intent) and the rule that *level changes are performance decisions backed by measurement*. The lower current value trades a little ratio for speed and, notably, memory: the in-code comment notes level 19 needs ~85 MB per compressor, and archive runs `HashWorkers` compressors concurrently. zstd is also pinned single-threaded (`ZSTD_c_nbWorkers = 0`) on purpose — archive already parallelizes across chunks, and ZSTDMT is the least-verified path in the managed port (see ADR-0012 and [compression](../core/shared/compression.md)).

### Routing: shard split threshold

`MaxShardEntryCount = 1024` and `MinShardPrefixLength = 2` (`ChunkIndexService`) define how the [chunk index](../../glossary.md#chunk-index) shards. A [shard](../../glossary.md#shard) splits 16-way once its merged entry count exceeds 1024 at flush time. The number is load-bearing because **incremental flush rewrites the whole touched shard blob**, so steady-state write-amplification scales with shard *size*, not with the count of changed entries. ADR-0015 carries the write-amplification model: a daily archive touching ~200 roots writes ~2.2 MB at 1024 versus ~34 MB at 4096 (~15×). The threshold deliberately optimizes the common daily incremental over the once-per-machine cold rebuild. See [chunk-index](../core/shared/chunk-index.md) for the sharding mechanics.

## Key invariants

- **Channels and the verify pipe stay bounded.** Removing a capacity bound (or making a channel unbounded) breaks the bounded-memory guarantee at repository scale; the verify pipe bound specifically must keep one chunk flat regardless of its size. Any new pipeline stage mirrors this (AGENTS: *prefer streaming/batching/bounded pipelines over whole-repository in-memory materialization*).
- **Fan-out is sized to the stage's bottleneck, not copied.** Local-work stages (thin entries, flush) run wide; large-blob-I/O stages run narrow. A refactor that uniformly raises every worker count would oversubscribe the network path and the per-compressor memory budget without speeding the bottleneck.
- **`MaxShardEntryCount = 1024` and `MinShardPrefixLength = 2` are a coupled pair** verified by ADR-0015's confirmation checks; changing the threshold changes the steady-state write-amplification and shard count (the ~4096-shard design point fits one 5000-blob Azure list page).
- **The zstd level is the only codec size/speed knob and must keep `ZSTD_c_nbWorkers = 0`** and default window/LDM, so a frame written today is restorable by a default-configured reader (ADR-0012 confirmation).
- **`SmallFileThreshold` < `TarTargetSize`.** The per-file route decision must sit below the bundle seal size, or large files would land in tars and defeat the dedup-by-large-chunk path.

## Why this shape

- **Per-stage `const` over a config object** — the knobs live next to the code they govern and ride the numbered `// ── Stage N ──` handler structure, so a reader sees the bottleneck and its fan-out together. There is no operator tuning surface by design; ADR-0015 makes the same call for the shard layout (adapts with no operator tuning).
- **Bounded everything** — durability-first plus large-repository scale rules out whole-repository materialization. See [memory-boundedness](./memory-boundedness.md).
- **zstd level as a measured knob, not a constant of nature** — ADR-0012 fixes the policy (standard frames, inline verify, single-threaded) and treats the level as a tunable; the current `15` is a later measurement-driven adjustment from the ADR's `19`.
- **Shard threshold optimizes the common path** — daily incrementals over cold rebuilds; the rationale and the write-amplification arithmetic live once in [ADR-0015](../../decisions/adr-0015-chunk-index-scalability.md).

### How benchmarks inform the constants

The benchmark projects validate the *end-to-end shape and memory profile* the constants are meant to produce; they are not micro-tuners that emit the worker counts.

- **`src/Arius.Benchmarks`** (BenchmarkDotNet, `[MemoryDiagnoser]` + `[ThreadingDiagnoser]`) runs the real handlers against an Azurite backend:
  - `ArchiveStepBenchmarks` archives the deterministic `Representative` synthetic repository (`SyntheticRepositoryDefinitionFactory`) — exercising the full hash → dedup → large/tar route → flush pipeline so the worker/channel/threshold interplay is measured together.
  - `RepresentativeWorkflowBenchmarks` runs the canonical archive+restore workflow across supported backends.
  - The threading and memory diagnosers are how regressions in fan-out (thread oversubscription) or in the bounded-memory invariant (allocation growth with file count) surface.
- **`src/Arius.AzureBlob.Benchmarks`** isolates the blob primitive that the I/O-bound fan-out depends on — `AzureBlobContainerServiceBenchmarks` times `GetMetadataAsync` / `TryDownloadAsync` / download-if-exists against a *real* storage account (credentials via `ARIUS_AZURE_BENCHMARK_*` env vars). This is what grounds "blob round-trips are the bottleneck" — the premise behind keeping `UploadWorkers`/`DownloadWorkers` low and `ThinEntryWorkers` high, and behind the small-file tar threshold avoiding per-file round-trips.

The zstd-level and `MaxShardEntryCount` numbers themselves come from the measurements recorded in their ADRs (the gzip-vs-zstd archive comparison in ADR-0012; the write-amplification model in ADR-0015), not from these BenchmarkDotNet harnesses.

## Open seams / future

- **The constants are uncalibrated against production telemetry.** ADR-0015 states `1024` is grounded in a model (rewrite-whole-shard × ~200 touched roots), not long-horizon telemetry, so the value could shift with a different touched-root distribution at multi-million-chunk scale. The fan-out counts are similarly engineering estimates the benchmarks confirm rather than derive.
- **No adaptive tuning.** Worker counts do not scale with machine core count or measured link bandwidth; a fast multi-core host with a fat pipe leaves the narrow upload/download stages under-driven. An auto-scaling fan-out is the natural next change and would slot into the same per-stage `const` sites.
- **`ChannelCapacity = 64` carries a `TODO`** about pinning `SingleWriter`/`MultipleReader` semantics (`ArchiveCommandHandler.cs`) — tightening those channel options is a low-risk follow-up.
- **zstd level vs memory at higher `HashWorkers`.** Because per-compressor memory grows steeply with level (~85 MB at 19), any future raise of either `HashWorkers` or the level should be measured together against the bounded-memory invariant.
