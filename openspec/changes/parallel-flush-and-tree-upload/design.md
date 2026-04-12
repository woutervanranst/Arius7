## Context

The archive pipeline already uses substantial parallelism for hashing and chunk upload, but the finalization tail is still largely serialized. After upload work completes, `ArchiveCommandHandler` currently does the following in order:

1. `FileTreeService.ValidateAsync()`
2. `ChunkIndexService.FlushAsync()`
3. `ManifestSorter.SortAsync()`
4. `FileTreeBuilder.BuildAsync()`
5. `SnapshotService.CreateAsync()`

This shape leaves two distinct sources of end-of-pipeline latency:

- `ChunkIndexService.FlushAsync()` flushes touched shard prefixes one at a time.
- `FileTreeBuilder.BuildAsync()` computes tree blobs bottom-up and uploads each missing tree immediately, so upload latency is paid inline during tree construction.

The codebase has evolved since this idea was first proposed. `FileTreeBuilder.BuildAsync()` now calls `FileTreeService.ValidateAsync()` itself before `ExistsInRemote()`, which means the validation precondition already lives inside the tree-building boundary. The old proposal also bundled shard-prefix changes, but shard layout is a separate repository-contract problem and is out of scope here.

The design also has to respect Arius's scale and durability constraints. Arius is a backup tool for important files. Repositories may be large in total bytes and still contain many thousands of small files, so file-count scale matters as much as byte scale for archive, restore, and list operations. Blob storage is non-transactional across blobs, mutable caches can be stale or corrupt, and partial updates are unavoidable in crash scenarios. Finalization work therefore must stay recoverable, retry-safe, and clearly separated from the repository commit point.

## Goals / Non-Goals

**Goals:**
- Reduce archive end-of-pipeline wall-clock time without changing repository layout.
- Keep one clear owner for filetree validation.
- Allow chunk-index flush to use parallel workers across independent shard prefixes.
- Split tree building into a local hash-computation phase and a parallel upload phase without requiring unbounded memory.
- Emit explicit progress events for finalization so the CLI shows useful progress after uploads finish.
- Preserve durability semantics: snapshot creation remains the only repository commit point, and pre-snapshot work must be recoverable after interruption.

**Non-Goals:**
- Changing chunk-index shard prefix length, shard naming, or repository metadata.
- Solving durability/recoverability for alternative shard-prefix strategies.
- Reworking the manifest sort algorithm beyond fitting it into the new orchestration shape.
- Moving archive orchestration out of `ArchiveCommandHandler` into Shared.

## Decisions

### Decision 1: `FileTreeBuilder` remains the validation owner

`FileTreeBuilder.BuildAsync()` will remain responsible for ensuring `FileTreeService.ValidateAsync()` has run before any call to `ExistsInRemote()`.

That means `ArchiveCommandHandler` will stop doing a redundant explicit pre-validation call. The builder already owns the precondition for tree existence checks, and keeping validation ownership there avoids splitting one invariant across two layers.

Alternative considered: keep validation in `ArchiveCommandHandler` and remove it from `FileTreeBuilder`.
Rejected because it makes `FileTreeBuilder` easier to misuse outside the archive handler and weakens the service boundary that has already emerged.

### Decision 2: Tree building becomes a two-phase operation

`FileTreeBuilder.BuildAsync()` will be refactored into a bounded producer/consumer pipeline:

1. Validate once.
2. Read the sorted manifest.
3. Build directory entry sets and compute tree hashes bottom-up without remote I/O.
4. For each computed tree hash, use `ExistsInRemote()` to decide whether upload is needed.
5. For trees that need upload, serialize plaintext bytes to a temporary spool file outside the final cache directory and enqueue a descriptor into a bounded channel.
6. Parallel upload workers consume descriptors, upload the tree blob, then publish the plaintext bytes into the final filetree cache path only after upload succeeds.
7. Return the root tree hash after upload completion.

The important observation is that parent directory hashes depend on child directory hashes, not on child uploads. Tree upload is therefore a persistence concern, not a computation dependency.

The temporary spool file is important for both memory pressure and correctness:

- It avoids holding all computed `FileTreeBlob` instances in memory when the repository has many directories.
- It allows compute and upload to overlap.
- It avoids publishing a final cache file before remote upload is known to have succeeded. Temp spool files are tentative local state; final filetree cache files represent confirmed remote existence.

Alternative considered: keep the current inline `EnsureUploadedAsync()` call during bottom-up traversal and only parallelize the upload call itself.
Rejected because the current structure still serializes existence checks and remote upload latency into the tree computation loop.

Alternative considered: compute all trees in memory first, then upload from memory.
Rejected because file-count scale can be large even when tree blobs are individually small; bounded disk-backed spooling is a better fit for Arius's scale assumptions.

### Decision 3: `ChunkIndexService.FlushAsync()` parallelizes by shard prefix

Flush workers will operate independently per touched shard prefix:

`load existing shard -> merge pending entries for prefix -> serialize -> upload -> save L2 -> promote L1`

The concurrency unit is one prefix. Prefixes are independent because each worker writes a distinct `chunk-index/<prefix>` blob and a distinct L2 file.

Shared mutable state inside `ChunkIndexService` is limited to:

- draining and grouping pending entries before worker launch
- L1 cache promotion under the existing lock
- progress counters

Alternative considered: overlapping shard upload work while retaining sequential load/merge.
Rejected because most of the per-prefix latency is in the whole end-to-end prefix pipeline, not just the final upload call.

### Decision 4: Archive finalization overlaps only across real dependency boundaries

The new orchestration is not "everything parallel with everything." The dependency graph is:

```text
flush ----------------------------\
                                   -> snapshot
sort -> compute tree hashes -> upload /
```

So the archive handler will overlap:

- chunk-index flush branch
- manifest sort + tree branch

`SnapshotService.CreateAsync()` still waits until both branches are complete.

Alternative considered: keep the archive handler fully sequential and rely only on internal service-level parallelism.
Rejected because manifest sort and tree work are independent of chunk-index flush once manifest writing is complete, so handler-level overlap is real and valuable.

### Decision 5: Finalization progress is modeled as two explicit event streams

Add two notification events:

- `ChunkIndexFlushProgressEvent(int ShardsCompleted, int TotalShards)`
- `TreeUploadProgressEvent(int BlobsUploaded, int TotalBlobs)`

These are emitted by the components doing the work, not synthesized afterward. The CLI can then render finalization progress as two concurrent progress lines instead of appearing stalled between upload completion and snapshot creation.

Alternative considered: a single generic "archive phase changed" event.
Rejected because it loses the two independent denominators the CLI needs for meaningful progress display.

### Decision 6: Manifest sort remains a separate prerequisite for tree computation

`ManifestSorter.SortAsync()` stays separate for now. We will overlap it with chunk-index flush but not fold it into tree-building changes in this design.

This keeps the scope on finalization parallelism rather than mixing in a larger manifest-sorting redesign. If sort later becomes a dominant bottleneck, that can be addressed independently.

There is one manifest temp file per archive run, not multiple manifests sorted in parallel. The relevant concurrency is therefore between the single manifest-sort/tree branch and the chunk-index flush branch.

### Decision 7: Snapshot remains the only repository commit point

Parallel finalization work may leave partial remote state behind if the run fails: some chunk-index shards may have flushed and some filetrees may have uploaded. That is acceptable only because the repository is not considered committed until `SnapshotService.CreateAsync()` publishes the new snapshot.

This design relies on the following invariant:

- Pre-snapshot finalization artifacts are recoverable tentative state.
- The latest snapshot is the only authoritative description of repository state.
- Local caches must never convert tentative local work into claimed remote truth prematurely.

That is why tree upload temp spool files are separate from the final filetree cache path, and why cache validation remains explicit.

### Decision 8: Terminology note for manifest vs snapshot

The current domain language uses `manifest` for two different things:

- the temporary on-disk file-entry input to tree building
- the durable snapshot JSON object stored under `snapshots/`

That overlap is confusing. This change does not rename either artifact, but it records the issue so domain language can later be normalized. A likely direction is to reserve `snapshot` for the durable repository commit object and use a more specific term such as `tree-build manifest` or `archive manifest` for the temporary sorted-input file.

## Risks / Trade-offs

- **Parallel flush increases service-internal concurrency pressure** -> Mitigation: snapshot pending entries into per-prefix groups before worker launch, keep L1 promotion under the existing lock, and use one progress counter per flush operation.
- **Tree compute/upload pipeline can increase temp-disk usage** -> Mitigation: use bounded channels, delete temp spool files after upload, and keep spool files outside the final cache path so crash recovery can distinguish tentative local work from confirmed remote state.
- **Slow-path validation may still dominate some runs** -> Mitigation: accept that the slow path is a different cost center. This change focuses on the serialized flush and tree-upload tail, while validation remains correct and explicit.
- **Progress events from parallel workers can arrive out of order** -> Mitigation: events carry completed/total counts rather than assuming ordered delivery.
- **Concurrency split across handler and services can blur ownership** -> Mitigation: keep validation and tree-upload internals inside `FileTreeBuilder`, keep per-prefix shard work inside `ChunkIndexService`, and let `ArchiveCommandHandler` only compose the two high-level branches.
- **Partial remote updates can look like corruption** -> Mitigation: treat snapshot publication as the commit point, design caches as hints rather than truth, and keep finalization idempotent and retry-safe.

## Migration Plan

1. Remove the redundant archive-handler call to `FileTreeService.ValidateAsync()`.
2. Refactor `FileTreeBuilder.BuildAsync()` into local compute first, then parallel upload of missing tree blobs.
3. Refactor `ChunkIndexService.FlushAsync()` to snapshot touched prefixes and flush them in parallel.
4. Update `ArchiveCommandHandler` to overlap flush with sort/tree finalization while preserving snapshot-after-both semantics.
5. Add finalization progress events and CLI handlers.
6. Add tests for validation ownership, bounded disk-spooled tree behavior, parallel flush correctness, orchestration ordering, and progress emission.

Rollback is straightforward because the change does not alter repository layout: restore sequential orchestration and the prior inline tree upload behavior if the new concurrency shape proves awkward.

## Open Questions

- Whether `ManifestSorter.SortAsync()` is small enough in practice to leave as-is once flush and tree upload are parallelized.
