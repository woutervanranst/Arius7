# Simplify RestoreCommandHandler

## Context

`RestoreCommandHandler` (843 lines) is hard to follow. The restore itself is sound — a two-pass,
streaming, memory-bounded pipeline — but the *mechanics* are cumbersome: a `StartResolvePipeline`
method wires three `Channel.CreateBounded` stages (Walk → Route → Resolve), each wrapped in its own
`Task.Run` with complete-on-`finally`, glued together by a linked `CancellationTokenSource` and a
`ResolvePipeline` record struct, and this whole apparatus is stood up **twice** (once per pass). The
walk is also depth-first (explicit `Stack`), unlike the sibling `ListQueryHandler` which walks the
same filetree breadth-first as a clean `IAsyncEnumerable`.

Goal: make it read top-to-bottom, mirror `ListQueryHandler`'s walk idiom, and drop the scaffolding —
**without** changing behavior, events, counts, or the streaming/memory-bounded guarantees.

Two design decisions were confirmed with the user:
1. **Symmetry, not shared code** — Restore keeps its own BFS walk written in the same idiom as
   `ListQueryHandler.WalkAsync`; we do **not** extract a shared walker (ListQuery's walk is a
   remote+local *merge* that emits directory nodes, so it can't cleanly serve Restore).
2. **Keep parallel route hashing** — preserved via one small reusable streaming helper instead of the
   inline channel/worker scaffolding.
3. **ArchiveCommandHandler structure** — keep the numbered `// ── Stage N ──` banner comments, mirror the
   same numbering in the class `<summary>`, and prefer one long linear `Handle` that **inlines** the
   stage orchestration over extracting many small private methods.

## What does NOT change (behavior to preserve — pinned by tests)

- Two-pass order: **classify → publish cost estimate → `ConfirmRehydration` → download**. Cancel
  returns without downloading (`Handle_CancelRehydration_RestoresNothing`).
- Tar bundles downloaded **exactly once** via the refcount-flush grouper, regardless of walk order
  (`Handle_ScatteredTarEntries_DownloadsChunkOnce_AndRestoresAll`).
- Deadlock-free fault handling in pass 2: a download-worker fault must unblock a grouper parked on the
  bounded chunk channel (`Handle_DownloadFailure_FailsGracefullyWithoutDeadlock`, 30s timeout).
- Route's four outcomes + events (`RestoreRouteTests`): New / SkipIdentical / Overwrite /
  KeepLocalDiffers, with `FileRoutedEvent` + `FileSkippedEvent`.
- Missing-index-hash error message containing the bad hash, no files written
  (`Handle_InvalidSnapshotContentHash_FailsRestore`).
- All `mediator.Publish(...)` calls, counts (`FilesRestored`/`FilesSkipped`/`ChunksPendingRehydration`),
  timestamps, pointer files, `NoPointers`, `TargetPath` filtering, duplicate-content restore.

## Target structure

Replace the three-channel pipeline + `ResolvePipeline` with a composed `IAsyncEnumerable`. Both passes
consume one private method:

```csharp
// Walk → Route → Resolve, composed. emitEvents=true only for pass 1 (avoids double-publish).
private IAsyncEnumerable<ResolvedFile> StreamResolvedFilesAsync(
    RelativeFileSystem fs, FileTreeHash rootHash, RestoreOptions opts,
    bool emitEvents, StrongBox<long>? skipped, CancellationToken ct)
    => ResolveAsync(
           WalkAsync(rootHash, opts.TargetPath, emitProgress: emitEvents, ct)
               .WhereParallelAsync(RouteWorkers,
                   (file, c) => ShouldRestoreAsync(file, fs, opts, emitEvents, skipped, c), ct),
           ct);
```

### `WalkAsync` — BFS, mirrors `ListQueryHandler.WalkAsync`
- Signature changes from `Task WalkAsync(ChannelWriter<FileToRestore> writer, …)` to
  `async IAsyncEnumerable<FileToRestore> WalkAsync(FileTreeHash rootHash, RelativePath? targetPrefix,
  bool emitProgress, [EnumeratorCancellation] CancellationToken ct)`.
- Swap the `Stack` for a `Queue` (BFS). Per directory: `yield return` matching `FileEntry`s first, then
  `Enqueue` child `DirectoryEntry`s — same rhythm as ListQuery. This deletes the `childDirectories`
  temp list and the "push in reverse for pre-order" loop/comment.
- Keep `IsPathRelevant` (subtree pruning), the per-file `targetPrefix.StartsWith` filter, and the
  throttled `TreeTraversalProgressEvent` (only when `emitProgress`).

### `ShouldRestoreAsync` — the per-file Route decision (was `RouteAsync`)
- `ValueTask<bool> ShouldRestoreAsync(FileToRestore file, RelativeFileSystem fs, RestoreOptions opts,
  bool emitEvents, StrongBox<long>? skipped, CancellationToken ct)` — returns `true` to restore.
- Body is the existing route logic verbatim: file-exists → hash unless `--overwrite` → SkipIdentical
  (return false) / KeepLocalDiffers (return false) / Overwrite (return true); not-exists → New (return
  true). Events emitted only when `emitEvents`. `skipped` stays a `StrongBox<long>` with
  `Interlocked.Increment` (the predicate runs on multiple route workers).

### `ResolveAsync` — batching transform (replaces hand-rolled `FlushAsync` closure)
- `async IAsyncEnumerable<ResolvedFile> ResolveAsync(IAsyncEnumerable<FileToRestore> files,
  CancellationToken ct)`: buffer up to `ResolveBatchSize`, then one `index.LookupAsync(distinct hashes)`,
  `yield return new ResolvedFile(file, entry)` per file; flush remainder at end. Same missing-hash
  `InvalidOperationException` (with up-to-5 sample) thrown after the batch's resolved files are yielded
  — identical to today, so the invalid-snapshot test stays green (pass 1 throws before pass 2 runs).

### `Handle` — one long linear method, stages inlined (ArchiveCommandHandler style)
Per the user's preference, mirror `ArchiveCommandHandler.Handle`: a single long, top-to-bottom method
that **inlines** the stage orchestration under numbered `// ── Stage N: <name> ──` banners (kept, not
trimmed), each with its `_logger.LogInformation("[phase] …")` line, and the numbers match the
`<summary>`'s numbered Stages list 1:1. So `ClassifyAsync` and `DownloadAsync` are **removed as separate
methods** and inlined into `Handle` (just as Archive inlines its Enumerate/Hash/… stages as inline
`Task.Run` blocks). The whole body stays inside the existing `try/catch` that returns a failure
`RestoreResult`. Renumber the current inconsistent banners (`Step 1`, `Pass 1`, `Step 6`, `Pass 2`,
`Step 8`, `Step 9`) into a clean consecutive sequence:

- **Stage 1: Resolve snapshot** — `snapshotSvc.ResolveAsync`; bail with the no-snapshot/not-found result.
- **Stage 2: Classify (walk #1)** — `await foreach (var resolved in StreamResolvedFilesAsync(…,
  emitEvents: true, skipped, ct))` accumulating the `ChunkClassification` map; then the counts/byte sums
  loop and the Snapshot/TreeTraversal/RestoreStarted/ChunkResolution/RehydrationStatus events. (No more
  `StartResolvePipeline`/`using (pipeline.Cts)`/`finally { CancelAsync; await Stages; }` — the composed
  `IAsyncEnumerable` needs none of it.)
- **Stage 3: Cost estimate + confirm** — `RestoreCostCalculator.Compute`, then `ConfirmRehydration`;
  cancel returns the "restored nothing" result.
- **Stage 4: Download (walk #2)** — inline `chunkChannel` (bounded `DownloadWorkers`), the grouper
  `Task.Run` (large 1:1; tar buffered + flushed at refcount), the `Parallel.ForEachAsync` workers, and
  the deadlock-free `finally`. Scope the linked CTS locally: `using var cts =
  CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)`; the grouper consumes
  `StreamResolvedFilesAsync(…, emitEvents: false, skipped: null, cts.Token)`. `finally { await
  cts.CancelAsync(); await Task.WhenAll(grouper, downloads)…; }` keeps teardown deadlock-free (cancelling
  `cts.Token` unblocks a grouper parked on `chunkChannel.WriteAsync` and tears down the in-flight
  Walk/Route/Resolve iterators). Logic identical to today's `DownloadAsync`.
- **Stage 5: Rehydrate** — request rehydration for `NeedsRehydration` + re-routed chunks (skip already
  pending); publish `RehydrationStartedEvent`.
- **Stage 6: Cleanup** — when nothing pending, `PlanRehydratedCleanupAsync` + `ConfirmCleanup` + execute.

Counters become `Handle` locals (`StrongBox<long> skipped`, `long filesRestored`,
`ConcurrentDictionary rerouteToRehydration`), exactly as Archive uses `filesScanned`/`filesUploaded`.
Keep the load-bearing *why* comments (StartCopyFromUri 409 note, stale-classification reroute,
publish-start-before-tar-progress-factory, cancel-to-unblock-grouper). `ClassifyChunk` stays a small
helper used in Stage 2.

## New shared helper

`src/Arius.Core/Shared/AsyncEnumerableExtensions.cs` — `WhereParallelAsync<T>` (sibling to the existing
`ChannelReaderExtensions.ReadAllBatchesAsync`): a bounded N-way parallel async filter that yields kept
items as they complete. It encapsulates `Channel.CreateBounded(dop)` + `Parallel.ForEachAsync` +
complete-on-fault + an internal linked CTS cancelled in `finally` (so an abandoned/early-broken consumer
can't deadlock a producer parked on `WriteAsync`). This is the single home for the concurrency plumbing
that was previously smeared across `StartResolvePipeline`. Add a focused unit test mirroring
`src/Arius.Core.Tests/Shared/ChannelReaderExtensionsTests.cs` (yields kept items, drops rejected,
surfaces predicate faults, honors cancellation).

## Types (`Features/RestoreCommand/Models.cs`)

- **Remove** `ResolvePipeline` (and `StartResolvePipeline`) — the only truly unnecessary type.
- **`ChunkClassification`: `record` → `sealed class`.** It's a mutable accumulator (`RefCount++`,
  `Status` set, sizes summed via dictionary reference); `record` wrongly implies value semantics. Keep
  its fields (including `IsLargeChunk`, which the `Handle` counting loop reads). No call-site changes.
- **Keep** `FileToRestore` (mirrors Archive's `FileToUpload` — symmetric vocabulary), `ResolvedFile`,
  `ChunkToRestore`, public `RestoreCostEstimate`, and `DownloadKind` (consumed by `Arius.Cli`).
- **Constants:** remove `ChannelCapacity` (no walk/route/resolved channels left); keep `RouteWorkers`,
  `ResolveBatchSize`, `DownloadWorkers`.

## Class `<summary>` (numbered to match the `Handle` banners, ArchiveCommandHandler style)

Rewrite lines 17-55 to describe a streaming, memory-bounded restore: a **breadth-first** walk composed
as `IAsyncEnumerable` (Walk → Route → Resolve) mirroring `ListQueryHandler`, two passes over the tree
(cache-backed, so walk #2 is cheap), and the **one** remaining channel (pass-2 download fan-out). Use a
`## Stages` list numbered **1–6 identically to the in-body `// ── Stage N ──` banners**:

1. **Resolve snapshot**
2. **Classify** (walk #1) — Walk → Route → Resolve, accumulating an O(distinct chunks)
   `ChunkClassification` map (status, summed sizes, refcount). Rehydration state from one
   rehydrated-prefix listing + index tier hints.
3. **Confirm** — publish cost estimate, invoke `ConfirmRehydration`; cancel downloads nothing.
4. **Download** (walk #2, events suppressed) — grouper dispatches to workers: large 1:1; tar buffered +
   flushed at refcount (each tar downloaded exactly once); still-archived blob re-routed to rehydration.
5. **Rehydrate** — request rehydration for archive-tier chunks (skip already pending).
6. **Cleanup** — when nothing pending, optionally delete leftover rehydrated blobs.

Then a `## Pipeline (shared by stages 2 & 4)` line — `Walk (BFS) → Route (×N parallel) → Resolve
(batched index lookup)`, composed as `IAsyncEnumerable` — and a one-row `## Channels` note for
`chunkChannel` (bounded `DownloadWorkers`, walk #2 only).

## Critical files

- `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs` — main rewrite.
- `src/Arius.Core/Features/RestoreCommand/Models.cs` — `ChunkClassification` → class; remove
  `ResolvePipeline`.
- `src/Arius.Core/Shared/AsyncEnumerableExtensions.cs` — new `WhereParallelAsync<T>`.
- `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs` — reference only (BFS idiom to mirror).
- `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs:452` — update the
  "(sorted depth-first)" comment to "(breadth-first / scattered across the tree)" (comment only; the
  assertions are order-independent).
- `src/Arius.Core.Tests/Shared/` — add `AsyncEnumerableExtensionsTests.cs`.

## Verification

1. `dotnet build` the solution (clean, no new warnings).
2. Run the pinning tests:
   - `dotnet test src/Arius.Core.Tests --filter "FullyQualifiedName~RestoreCommandHandlerTests"`
     (duplicates, tar-once, download-failure-no-deadlock, cancel-rehydration, invalid-snapshot,
     target-path, no-pointers, file-target).
   - `dotnet test src/Arius.Integration.Tests --filter "FullyQualifiedName~RestoreRoute"` (four route
     cases + events) and `~RestoreCostModel`, `~RestorePointerTimestamp`.
   - `dotnet test src/Arius.Core.Tests --filter "FullyQualifiedName~AsyncEnumerableExtensions"` (new
     helper).
   - E2E: `RepresentativeArchiveRestoreTests`.
3. Sanity-check memory/streaming intent: pull-based composition buffers ≤ `RouteWorkers` (route) /
   `ResolveBatchSize` (resolve), strictly tighter than the old bounded-256 channels; pass-1
   classification stays O(distinct chunks); pass-2 in-flight stays O(`DownloadWorkers`).
