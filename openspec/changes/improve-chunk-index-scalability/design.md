## Context

The chunk index is mutable repository metadata. It currently maps content hashes to chunk metadata through fixed 4-hex shard blobs under `chunk-index/`, with an L1 memory cache, L2 local disk cache, and L3 blob storage. This layout creates many tiny shards for small archives because random content hashes spread even modest file counts across many prefixes. At larger repository scales, any fixed prefix can still produce very large shard files.

Flush currently groups pending entries by fixed prefix, then processes each touched shard sequentially: load existing shard, merge entries, serialize, upload with overwrite, save L2, and promote to L1. If an archive crashes halfway through flushing multiple touched shards, some chunk-index blobs may include the new entries while others do not. The published snapshot remains safe because snapshot creation happens later, but recovery is implicit and depends on rerunning the archive.

`FileTreeService.ValidateAsync` currently compares local and remote snapshot state and, on mismatch, invalidates chunk-index caches as a hidden side effect. Issue #80 identifies this as a domain-boundary leak: `FileTreeService` owns immutable filetree cache behavior, while `ChunkIndexService` owns mutable chunk-index cache behavior.

## Goals / Non-Goals

**Goals:**

- Reduce tiny-shard proliferation by replacing the hard-coded 4-hex prefix with one internal repo-wide prefix-length constant on `ChunkIndexService`.
- Parallelize chunk-index flush safely across touched shard prefixes with an internal worker-count constant.
- Make corrupt chunk-index shards and interrupted repair state fail clearly from normal archive, restore, and list operations instead of triggering automatic repair.
- Make restore report unresolved snapshot content hashes with an actionable repair instruction instead of running full repair automatically.
- Add an explicit full chunk-index repair API and command for maintenance and tests.
- Let repair list chunks with metadata in the same storage listing call where supported.
- Make archive-tail cache coordination explicit in `ArchiveCommandHandler` and remove hidden chunk-index invalidation from `FileTreeService`.
- Run chunk-index flush and filetree synchronization concurrently after uploads complete, while keeping snapshot publication after both complete.

**Non-Goals:**

- No adaptive shard resizing or split/merge algorithm in this change.
- No CLI/config option for shard prefix length yet.
- No backward-compatible reader for old 4-hex chunk-index shards; the repository is still in development and repair can rebuild missing new-layout shards from chunks.
- No automatic chunk-index repair from archive, restore, or list operations; users run the explicit repair command when normal operations detect corrupt, incomplete, or unresolved chunk-index state.
- No distributed repair/archive lock in this change. This design assumes only one machine or process is actively mutating a given remote archive at a time.

## Decisions

### Decision: Fixed Prefix-Length Constant

Use one internal constant on `ChunkIndexService` for chunk-index shard prefix length, for example `internal const int ShardPrefixLength = 2`. `ContentHash` exposes a general `Prefix(int length)` method, and `Shard.PrefixOf(ContentHash)` uses `contentHash.Prefix(ChunkIndexService.ShardPrefixLength)` instead of the current fixed `Prefix4` property.

Alternatives considered:
- Keep 4 hex chars: preserves current layout but keeps tiny-shard behavior for small archives.
- Use 3 hex chars: middle ground, but still creates up to 4096 shards and remains sparse for small archives.
- Dynamic/adaptive prefix: solves both extremes, but introduces routing, migration, and partial-resize recoverability complexity.

Rationale: A 2-hex prefix bounds shard count at 256 and gives acceptable distribution for current scale targets. Keeping the constant on `ChunkIndexService` is the smallest change and avoids adding a separate layout type before there is more layout state to own. A general `Prefix(int length)` method avoids stringify/slice logic at call sites while also avoiding prefix-length-specific properties such as `Prefix2` or `Prefix3`.

This fixed constant is an interim layout decision. Future dynamic sharding should replace `Shard.PrefixOf` routing and repository layout state behind the chunk-index service boundary without requiring feature callers to understand or pass prefix lengths.

Design constraint: any future dynamic shard layout metadata SHALL be owned and interpreted by `ChunkIndexService`. Feature handlers, filetree code, snapshot code, repair callers, and storage callers SHALL NOT persist, compute, expose, or branch on chunk-index prefix lengths or shard-routing state.

### Decision: Lookup Failure Behavior

`ChunkIndexService` lookup does not repair chunk-index state in normal archive, restore, or list operations. Missing entries in a valid shard remain normal misses. Missing shard blobs are treated as empty shards. If a shard blob exists but cannot be deserialized, or if local repair state indicates an interrupted full repair, lookup fails with a clear chunk-index corruption or repair-incomplete error that instructs the user to run the explicit chunk-index repair command.

Alternatives considered:
- Repair corrupt shards during lookup: convenient but introduces surprising remote scans on normal operations and complicates interruption semantics.
- Probe missing shard blobs by listing `chunks/<prefix>*`: helps restore recover from deleted shards, but makes lookup behavior dependent on expensive side effects.
- Run full repair automatically from restore: resilient, but too expensive and surprising for a user-facing restore path.

Rationale: Full repair can be expensive and mutates repository metadata. Keeping repair explicit makes operation costs predictable and keeps archive, restore, and list behavior simple. Restore remains durability-focused by failing with a clear actionable error when snapshot content cannot be resolved through the chunk index.

### Decision: Explicit Full Repair API and Command

Add a full chunk-index repair path that scans committed chunk blobs once, rebuilds local chunk-index shard files on disk, uploads the rebuilt shards, and removes stale remote shard blobs that were not rebuilt. This is exposed as an API on the shared chunk-index service and as a CLI maintenance command. Full repair is idempotent: if interrupted, rerunning repair starts by purging local chunk-index repair output, reconstructs shard contents again from committed chunks, and converges.

This repair path assumes no concurrent archive or repair operation is mutating the same remote archive. Concurrent archive/repair mutation from another machine or process is out of scope for this change because chunk-index shard uploads overwrite whole shard blobs and no remote lease or distributed lock is introduced here.

Full repair uses the existing local chunk-index cache as bounded disk-backed rebuild state rather than staging the whole repository index in memory. L2 can normally be incomplete because it is a cache of shards this machine has needed; the repair sentinel does not mean "L2 is incomplete". It means an explicit full repair was interrupted after replacing normal cache contents with rebuild output, so normal operations must fail until the user reruns repair.

The local repair sentinel path is owned by `ChunkIndexService` as an internal constant and SHALL live outside the purgeable shard-cache directory, for example `~/.arius/{repo}/chunk-index.repair-in-progress`. Cache invalidation may delete files under `~/.arius/{repo}/chunk-index/`, but it SHALL NOT delete or ignore the repair sentinel.

1. Invalidate L1 and purge the local L2 chunk-index cache before rebuilding.
2. Mark local chunk-index repair as in progress so normal operations fail clearly instead of trusting partially rebuilt L2 files after an interrupted repair.
3. Run one metadata-aware `ListAsync("chunks/", includeMetadata: true, ...)` scan.
4. For each committed large or thin chunk, reconstruct its shard entry and merge it into the corresponding local L2 shard file using the configured shard prefix length.
5. Track the shard prefixes that received entries during the scan.
6. Upload each rebuilt non-empty local shard to `chunk-index/<prefix>` using the normal remote shard serialization.
7. List existing `chunk-index/` blobs and delete shard blobs whose names are not in the rebuilt prefix set.
8. Clear the in-progress marker and keep the rebuilt L2 cache as the current local chunk-index cache.

Alternatives considered:
- Tests call internal prefix repair only: insufficient coverage for maintenance behavior.
- Only expose CLI command: harder to test and reuse from future workflows.
- Stage repair output under a temporary prefix before publishing: cleaner in theory, but Azure Blob Storage has no atomic folder move; publishing would still require per-shard writes unless the storage format gained a manifest indirection.
- Rebuild every prefix by separately listing `chunks/<prefix>*`: keeps per-prefix memory bounded, but turns full repair into many remote list calls and makes the future dynamic-sharding transition harder to reason about.

Rationale: Full repair is the operator/test maintenance tool and the explicit recovery path for corrupt, incomplete, or unresolved chunk-index state. Since repair starts from committed chunks and does not publish snapshots, direct overwrite is acceptable and rerunnable. A single chunk listing minimizes remote listing work, and disk-backed local rebuild keeps memory bounded while still converging remote chunk-index blobs to the rebuilt non-empty shard set.

### Decision: Metadata-Aware Listing Uses Existing ListAsync Name

Change `IBlobContainerService.ListAsync` to return blob list items and accept `bool includeMetadata = false`. Existing callers use item names only. Repair calls `ListAsync(prefix, includeMetadata: true, ...)` to receive metadata and content length from Azure listings using `BlobTraits.Metadata` where supported.

Alternatives considered:
- Add `ListWithMetadataAsync`: clearer separation but duplicates listing concepts in the storage boundary.
- Keep current name-only listing and call `GetMetadataAsync` per blob: simplest API, but repair becomes one remote round-trip per chunk.

Rationale: Repair is expected to scan potentially many chunks. Azure can include metadata in listing results, so the storage abstraction should expose that capability directly.

### Decision: Bounded Parallel Flush Per Prefix

`ChunkIndexService.FlushAsync` processes each touched prefix independently through bounded `Parallel.ForEachAsync` using an internal worker-count constant, for example `FlushWorkers = 8`. Each prefix is still handled by exactly one worker: load existing shard, merge entries, serialize, upload, save L2, and promote L1.

If parallel flush fails after writing some shard prefixes, archive fails and SHALL NOT publish a snapshot. A later archive rerun or full repair can converge from committed chunks.

Alternatives considered:
- Keep sequential flush: simple but slow for many touched prefixes.
- Channel pipeline with separate load/serialize/upload stages: more flexible but unnecessary until profiling proves serialization/upload imbalance.

Rationale: Per-prefix work is naturally independent. Bounded `Parallel.ForEachAsync` is the smallest correct concurrency change.

### Decision: Chunk Index Is The Fast Dedup Source, Chunks Are The Durable Recovery Source

Archive uses the chunk index as the fast path for deduplication. If the chunk index misses, archive attempts the chunk upload using create-if-not-exists storage semantics. If the target chunk blob already exists and has complete `arius-type` metadata, `ChunkStorageService` recovers the stored metadata and archive records the missing chunk-index entry instead of treating the condition as failure.

For large chunks this naturally recovers from an index miss because the upload target is `chunks/<content-hash>`. For small files, archive does not add a pre-bundling remote thin-chunk check in this change, because that would add per-small-file remote I/O to the hot path. Thin chunk/index gaps are repaired by explicit full repair.

Rationale: The chunk index is a performance and metadata index, not the only durable source of truth. Chunk blobs and their metadata remain the recoverable storage record.

### Decision: ArchiveCommandHandler Coordinates Cache Epochs

`FileTreeService.ValidateAsync` keeps the snapshot comparison because that comparison drives filetree cache fast/slow-path behavior. It stops invalidating chunk-index caches and instead returns a `FileTreeValidationResult` record containing `SnapshotMismatch`. `ArchiveCommandHandler` becomes the explicit coordinator: it calls `FileTreeService.ValidateAsync`, and when the result reports a snapshot mismatch, it asks `ChunkIndexService` to invalidate mutable shard caches before chunk-index flush or tree existence checks.

Alternatives considered:
- Move snapshot comparison out of `FileTreeService`: creates cleaner separation in theory, but the comparison already belongs to the filetree fast/slow-path decision and moving it now would be a larger change.
- Introduce a repository cache coordinator service: architecturally clean, but not necessary while only archive coordinates these two cache owners.
- Keep hidden invalidation in `FileTreeService`: current behavior, but it keeps issue #80 unresolved and obscures archive tail ordering.

Rationale: Archive already orchestrates the end-to-end workflow. Making it coordinate both cache owners is explicit and minimal.

### Decision: Concurrent Archive Tail

After cache validation and chunk uploads complete, archive runs chunk-index flush and filetree synchronization concurrently. Snapshot creation waits for both tasks to complete and uses the filetree root hash from synchronization.

Rationale: Chunk-index shard writes and filetree blob writes target different repository metadata prefixes. Both are prerequisites for publishing a snapshot, but neither depends on the other after cache validation has completed.

## Risks / Trade-offs

- **[Risk] Explicit repair adds user friction** -> Fail with clear, actionable messages that identify corrupt, incomplete, or unresolved chunk-index state and point to the repair command.
- **[Risk] Changing `ListAsync` return type touches many callers** -> Keep the method name and add a simple `BlobListItem.Name` property; update callers mechanically.
- **[Risk] 2-hex prefix may create large shards for very large repositories** -> The prefix length is centralized as a const so it can be adjusted before release. Adaptive resizing remains intentionally out of scope.
- **[Risk] Parallel flush increases remote write pressure** -> Use a conservative bounded degree of parallelism and keep one worker per prefix.
- **[Risk] Valid-but-incomplete shards are not repaired during lookup** -> Restore fails with a clear unresolved-entry error and instructs the user to run explicit repair.

## Acceptance Criteria

- Test coverage for `src/Arius.Core/Shared/ChunkIndex/` SHALL be at least 90% after this change.

## Migration Plan

No backward-compatible migration is required. Existing repositories are development data. After the prefix length changes, old chunk-index shards at the previous layout are ignored; full repair rebuilds the new layout from chunk blobs.

Rollback during development is also repair-based: if the prefix length is changed again, delete/rebuild chunk-index shards from chunks.
