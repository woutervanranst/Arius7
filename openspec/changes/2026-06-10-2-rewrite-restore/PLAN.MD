# Streaming RestoreCommandHandler — implementation plan

## Context

`RestoreCommandHandler` (`src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs`)
currently materializes the **entire** restore set in memory before doing any work:

- `CollectFilesAsync` builds a `List<FileToRestore>` of every file in the snapshot (line 116/478).
- The conflict check builds a second `List<FileToRestore> toRestore` (line 127).
- Chunk resolution does `contentHashes.ToList()` + one giant `_index.LookupAsync` + a full
  `Dictionary<ChunkHash, List<FileToRestore>> filesByChunkHash` (lines 180–201) — flagged in-code
  with `// TODO this is a bottleneck --> make it streaming`.

For repositories with millions of file entries this is the dominant memory cost and makes the command
unresponsive (nothing happens until the whole tree is walked + every index entry is fetched).

**Goal:** rewrite the handler as a streaming, channel-based pipeline with bounded memory that scales
to millions of entries, **mirroring the structure/coding style of `ArchiveCommandHandler`** (channels
via `Channel.CreateBounded/Unbounded`, long-running `Task.Run` stages that complete their writer in a
`finally`, `Parallel.ForEachAsync` worker stages, `Interlocked` counters, `[phase]`/`[stage]`
structured logging, `ValueTask<RestoreResult>` return, outer try/catch returning a result with
`ErrorMessage`) and **drawing on `ListQueryHandler`** for the streaming walk + batched resolve.
Behavior must be preserved exactly (the CLI flow is unchanged).

## Decisions (confirmed with user)

1. **Rehydration barrier → two-pass.** The `ConfirmRehydration` callback needs an aggregate cost
   estimate *before* downloading, and on cancel must download nothing. So: **pass 1** streams the
   whole tree to classify chunks (bounded state only), presents the estimate, and confirms; **pass 2**
   re-walks (cheap — `IFileTreeService.ReadAsync` is cache-backed) and streams the downloads. This is
   how the current code already behaves logically (classify-all → confirm → download); we just make
   each phase streaming + bounded instead of materialized. Maps 1:1 onto the existing CLI phases in
   `RestoreVerb.cs` (first Live view → rehydration question → Phase-3 download Live view) — **no CLI
   restructuring**.

   *Why not single-pass materialize?* Verified type sizes (`ContentHash`/`ChunkHash` wrap a 64-char
   hex string ~154 B; `RelativePath` ~146 B; `FileToRestore` a record class) put a materialized
   `List<FileToRestore>` + `Dictionary<ChunkHash,List<FileToRestore>>` at **~400–500 B/file → ~0.5 GB
   at 1M, ~4–5 GB at 10M**. The two-pass phase-1 dict is keyed by *distinct chunks* with no path
   strings (~220 B/chunk), so it scales with chunks, not files. Phase 1 deliberately keeps only
   aggregates (no child paths) — that's exactly the memory the re-walk buys back. The re-walk's only
   real recompute is re-hashing pre-existing local files during routing, which only happens on the
   uncommon "file on disk + no `--overwrite` + content differs" path (nil for restore-into-empty-dir).

2. **Tar grouping → in-memory group + refcount flush.** Large files are 1:1 (content hash == chunk
   hash) and stream straight to a download worker — **never grouped**. Tar/small files are scattered
   across the walk (archive packs tars in unsorted `EnumerateFiles` + parallel-hash order; the tree
   reads back name-sorted per directory), so to download each tar once they must be buffered until the
   group is complete. Pass 1 tallies a **per-chunk refcount** (bounded by *distinct chunks*); pass 2
   buffers tar files in a `Dictionary<ChunkHash, List<FileToRestore>>` and flushes + downloads + drops
   each group the instant its accumulated count hits the refcount. Working set is bounded by tar
   metadata for currently-open groups only; large files and pass-1 are unaffected.

3. **Progress hooks → add for parity.** Add an `OnDownloadQueueReady` hook to `RestoreOptions`
   mirroring archive's `OnHashQueueReady`/`OnUploadQueueReady`, wired through `ProgressState`.

4. **Classify with one `ListAsync` + the index `StorageTierHint` — no per-chunk blob calls.** The
   current code does one `GetHydrationStatusAsync` HEAD per distinct chunk (two blob round-trips each) —
   brutal at scale. Replace it with **one streamed `_blobs.ListAsync(BlobPaths.ChunksRehydratedPrefix)`**
   up front: `BlobListItem` carries `Name` + `Tier`, so it yields the full set of rehydrated copies and
   their state (Tier != Archive → **rehydrated/ready**; Tier == Archive → **RehydrationPending**). This
   set is bounded by chunks under active rehydration (small) — keep it as a
   `Dictionary<RelativePath, bool>` keyed by `ChunkRehydratedPath(chunkHash)`. Then classify each chunk:
   in-rehydrated-ready → **Available** (download transparently uses the rehydrated copy via the existing
   `SelectReadableChunkBlobAsync`); in-rehydrated-pending → **RehydrationPending**; otherwise the index
   `StorageTierHint` (already fetched by resolve) decides — `Archive` → **NeedsRehydration**, else
   **Available**. Because Arius never changes a blob's tier in place (rehydration always creates the
   copy), hint + the one listing is **fully accurate**, restoring exact `available`/`rehydrated`/
   `needsRehydration`/`pending` counts. Keep a phase-2 **safety net**: if a download still throws the
   archived-blob exception (manual tier change, etc.), catch it and re-route the chunk to the
   rehydration set. Pin the precise exception (Azure `RequestFailed`/`BlobArchived`) at implementation
   time and catch narrowly; `StartRehydrationAsync` is already try/caught for the 409-already-pending case.

5. **Thread-safety / ownership.** Collections owned by a single ×1 stage stay plain `Dictionary`/`List`
   (correct + faster); only ×N-worker-written state is concurrent. The phase-1 classification dict and
   the phase-2 tar-grouping dict are each owned by a **single ×1 consumer/grouper** (like
   `ListQueryHandler`'s single Resolve consumer) → plain. The only concurrent state: `Interlocked`
   counters and the phase-2 **re-route rehydration set** written by ×N download workers →
   `ConcurrentDictionary`/`ConcurrentBag` (mirroring archive's `inFlightHashes`/`pendingPointers`).

## Files to modify

- `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs` — full rewrite to the pipeline below.
- `src/Arius.Core/Features/RestoreCommand/Models.cs` — add `ResolvedFile`, `ChunkToRestore`,
  `ChunkClassification` pipeline records (reuse existing `FileToRestore`).
- `src/Arius.Core/Features/RestoreCommand/RestoreCommand.cs` — add `OnDownloadQueueReady` to `RestoreOptions`.
- `src/Arius.Cli/ProgressState.cs` — add a `DownloadQueueDepth` getter (mirror `UploadQueueDepth`, lines 408–415).
- `src/Arius.Cli/Commands/Restore/RestoreVerb.cs` — wire `OnDownloadQueueReady = g => progressState.DownloadQueueDepth = g`
  in the options block (~line 119), and surface the depth in `BuildDisplay` (mirror `ArchiveVerb.cs:262`).
- Tests — see Verification.

`RestoreLargeFileAsync` and `RestoreTarBundleAsync` (current lines 557 / 600) are **reused essentially
as-is** — they already download a single chunk, extract, set timestamps, write pointers, and publish
the right events.

## Pipeline design

A shared streaming sub-pipeline produces `ResolvedFile`s; pass 1 and pass 2 each consume that stream
differently. Both passes use the same three `Task.Run` stages (Walk → Disposition → Resolve) writing
to bounded channels, completing each writer in a `finally`/`catch` (faults propagated via
`Writer.Complete(ex)`, like `ListQueryHandler.cs:118-138`), under a linked `stageCts`
(`ListQueryHandler.cs:109`). Add a tuning-knob constants block like `ListQueryHandler`/`ArchiveCommandHandler`:
`ChannelCapacity`, `ResolveBatchSize`, `DispositionWorkers`, `DownloadWorkers`.

**Stage 1 — Walk (×1).** Replace `CollectFilesAsync`/`WalkTreeAsync` (lines 478–546) with an
explicit-stack DFS modeled on `ListQueryHandler.WalkTreeAsync` (`:187`), writing `FileToRestore` into a
bounded `walkChannel`. Honor `TargetPath` via the existing `IsPathRelevant`/`StartsWith` filter logic
(lines 517, 533). Keep emitting `TreeTraversalProgressEvent` periodically (current 10-file/100ms cadence).

**Stage 2 — Route (×N).** (Renamed from "Disposition" — see naming note below.) `Parallel.ForEachAsync`
over `walkChannel`. Per file: `fs.FileExists`; when it exists and `!Overwrite`, hash the local file
(`_encryption.ComputeHashAsync`) and decide skip-identical / keep-differs; otherwise overwrite/new.
Logic lifted verbatim from lines 130–169. Files needing restore are written to `routeChannel`.
**Per-file events (`FileRoutedEvent`/`FileSkippedEvent`) are emitted only when the pass requests it**
(flag) — pass 1 emits, pass 2 suppresses to avoid double emission. `Interlocked` skip counter (pass 1).

> **Naming:** rename the "disposition" concept to **Route/Routing** throughout, including the public
> surface (dev-phase, breaking OK): `RestoreDisposition` enum → `RestoreRoute`, `FileDispositionEvent`
> → `FileRoutedEvent`; update `RestoreProgressHandlers` + CLI references. (Leave public names alone only
> if the user later prefers internal-only rename.)

**Stage 3 — Resolve (×1, batched).** Exactly like `ListQueryHandler.ResolveAsync`/`ResolveBatchAsync`
(`:306`,`:343`): buffer ≤ `ResolveBatchSize` files, one `_index.LookupAsync(batch)` per batch, preserve
order. Emit `ResolvedFile(FileToRestore File, ShardEntry IndexEntry)` to `resolvedChannel`. On a missing
index entry, throw the existing "missing from chunk index" `InvalidOperationException` (line 206), with
the same up-to-5 sample (accumulate a small bounded sample list, not all unresolved).

### Pass 1 — Classify (Handle consumes `resolvedChannel`)

Like `ListQueryHandler.Handle`'s consume loop. Build a single bounded structure:

Before consuming, do the single `ListAsync(ChunksRehydratedPrefix)` (decision 4) into a
`rehydratedState` map. Then build a single bounded structure:

```
Dictionary<ChunkHash, ChunkClassification>   // keyed by distinct chunk (single-consumer → plain Dictionary)
ChunkClassification(bool IsLargeChunk, ChunkHydrationStatus Status, long CompressedSize, long OriginalSize, int RefCount)
```

For each `ResolvedFile`: on first sight of a chunk, classify via `rehydratedState` + `StorageTierHint`
(decision 4) — **no per-chunk blob call**. Record `IsLargeChunk`/sizes once; always `RefCount++`. For
tar chunks, *sum* `CompressedSize`/`OriginalSize` across the chunk's files (per-file proportional
shares, current lines 230–238); for large, the single entry. Accumulate the aggregate sums the current
code computes (lines 209–241, 277–304): large/tar counts, totals, and the download-vs-rehydration byte
split by status (`Available` → download bytes; `NeedsRehydration` + `RehydrationPending` → rehydration
bytes).

After pass 1 drains: publish `SnapshotResolvedEvent`, `TreeTraversalCompleteEvent`, `RestoreStartedEvent`
(file count is now known — the full walk completed), `ChunkResolutionCompleteEvent`,
`RehydrationStatusEvent`. Build the `RestoreCostEstimate` via `RestoreCostCalculator` (line 307) and
invoke `opts.ConfirmRehydration` when there are archive-tier chunks (line 318). On cancel (`null`),
return `FilesRestored = 0, FilesSkipped = skipped, ChunksPendingRehydration = needs+pending` (lines 326–333) —
**no pass 2**. Memory: O(distinct chunks). If zero files, short-circuit to the cleanup step.

### Pass 2 — Download (only available chunks)

Re-run Walk → Route (events suppressed) → Resolve, then a grouper + download workers, mirroring
archive's router → upload-workers shape:

- **Grouper (×1, Task.Run consuming `resolvedChannel`; single-consumer → plain `Dictionary`):** look up
  the chunk in the pass-1 dict.
  - `Status != Available` → skip (rehydration/cleanup handled below).
  - `IsLargeChunk` → write a `ChunkToRestore` (single file) to `chunkChannel` immediately (no buffering).
  - Tar → append the file to its `Dictionary<ChunkHash, List<FileToRestore>>` entry; when the entry's
    count reaches the pass-1 `RefCount`, emit a `ChunkToRestore` for the full group to `chunkChannel`
    and remove it from the dict (refcount flush). Complete `chunkChannel` in `finally`.
- **Download workers (×N, `Parallel.ForEachAsync` over `chunkChannel`, `DownloadWorkers=4`):** for each
  `ChunkToRestore`, publish `ChunkDownloadStartedEvent` **before** invoking the progress factory
  (ordering invariant: `RestoreProgressHandlers` populates `TarBundleMetadata` that
  `CreateTarBundleDownloadProgress` reads — `RestoreVerb.cs:154`), then call the reused
  `RestoreLargeFileAsync` (large) or `RestoreTarBundleAsync` (tar, grouped by content hash as today,
  lines 386–391). `Interlocked` restored counter. **Re-route on mis-hint (decision 4):** if the
  download throws the archived-blob exception (Azure `RequestFailed`/`BlobArchived` — verify exact
  type), catch it and add the chunk to a `ConcurrentDictionary<ChunkHash,…>`/`ConcurrentBag`
  rehydration set (×N-written → concurrent) instead of faulting the worker.

```
ChunkToRestore(ChunkHash ChunkHash, bool IsLargeChunk, IReadOnlyList<FileToRestore> Files, long CompressedSize, long OriginalSize)
```

Register `opts.OnDownloadQueueReady?.Invoke(() => chunkChannel.Reader.Count)` in pass-2 setup.

After pass 2: kick off rehydration for the `NeedsRehydration` chunks **plus any chunks re-routed by
mis-hint download failures** (Step 8, lines 397–425) and run the rehydrated-blob cleanup when nothing is
pending (Step 9, lines 431–445) — both reused from current code, sourced from the pass-1 classification
dict + the concurrent re-route set instead of `filesByChunkHash`.

### Channels (handler doc-comment table, archive-style)

| Channel | Writer | Reader | Capacity | Notes |
|---|---|---|---|---|
| `walkChannel` | Walk (1) | Route (2) | bounded (N) | Backpressure caps walk-ahead. SingleWriter. |
| `routeChannel` | Route (2,×N) | Resolve (3) | bounded (N) | Files needing restore (post conflict-check). |
| `resolvedChannel` | Resolve (3) | pass1 classify / pass2 grouper | bounded (N) | `ResolvedFile`; order preserved. SingleWriter. |
| `chunkChannel` (pass 2) | Grouper | Download workers (×N) | bounded (DownloadWorkers) | `ChunkToRestore`; download backpressures grouping. |

## Verification

- **Unit/handler tests** (`src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`):
  all existing tests must pass unchanged (behavior preserved) — large files, tar duplicates, zero-byte
  tar entries, invalid-hash failure, `TargetPath` filtering, `NoPointers`, missing-container. Run
  `dotnet test` for `Arius.Core.Tests`.
  - Add a **scattered-tar** test: several distinct small files that land in one tar bundle but live at
    distant tree paths (e.g. `a/…`, `z/…`); assert all are restored correctly. Optionally assert each
    chunk is downloaded exactly once by counting `DownloadAsync` calls on `FakeInMemoryBlobContainerService`
    (== distinct available chunks) — validates the refcount flush avoids re-downloads.
  - Add a **cancel-rehydration** test (returns `FilesRestored = 0`, downloads nothing) using a fake
    that reports an archive-tier chunk — confirms the two-pass barrier.
- **CLI tests** (`src/Arius.Cli.Tests/Commands/Restore/RestoreCommandTests.cs`): parsing tests unchanged
  (no new CLI options/args). Verify `CliHarness` still builds.
- **End-to-end manual check:** archive a small tree then restore it via the CLI (`/run` or
  `dotnet run -- archive … && dotnet run -- restore …`) into an empty dir; confirm files + pointers +
  timestamps and the progress UI (now showing download queue depth) render correctly.
- Build: `dotnet build` the solution; ensure no analyzer/nullable warnings introduced.
