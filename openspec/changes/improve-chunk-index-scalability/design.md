## Context

The chunk index is mutable repository metadata. It currently maps content hashes to chunk metadata through fixed 4-hex shard blobs under `chunk-index/`, with an L1 memory cache, L2 local disk cache, and L3 blob storage. This layout creates many tiny shards for small archives because random content hashes spread even modest file counts across many prefixes. At larger repository scales, any fixed prefix can still produce very large shard files.

Flush currently groups pending entries by fixed prefix, then processes each touched shard sequentially: load existing shard, merge entries, serialize, upload with overwrite, save L2, and promote to L1. If an archive crashes halfway through flushing multiple touched shards, some chunk-index blobs may include the new entries while others do not. The published snapshot remains safe because snapshot creation happens later, but recovery is implicit and depends on rerunning the archive.

`FileTreeService.ValidateAsync` currently compares local and remote snapshot state and, on mismatch, invalidates chunk-index caches as a hidden side effect. Issue #80 identifies this as a domain-boundary leak: `FileTreeService` owns immutable filetree cache behavior, while `ChunkIndexService` owns mutable chunk-index cache behavior.

## Goals / Non-Goals

**Goals:**

- Reduce tiny-shard proliferation by replacing the hard-coded 4-hex prefix with one internal repo-wide prefix-length constant on `ChunkIndexService`.
- Parallelize chunk-index flush safely across touched shard prefixes with an internal worker-count constant.
- Add configurable lookup repair behavior so corrupt shard state can be rebuilt from chunk blobs on demand, and restore can recover from missing shard blobs without making normal archive misses expensive.
- Make restore run one full chunk-index repair and retry unresolved snapshot content hashes before failing.
- Add an explicit full chunk-index repair API and command for maintenance and tests.
- Let repair list chunks with metadata in the same storage listing call where supported.
- Make archive-tail cache coordination explicit in `ArchiveCommandHandler` and remove hidden chunk-index invalidation from `FileTreeService`.
- Run chunk-index flush and filetree synchronization concurrently after uploads complete, while keeping snapshot publication after both complete.

**Non-Goals:**

- No adaptive shard resizing or split/merge algorithm in this change.
- No CLI/config option for shard prefix length yet.
- No backward-compatible reader for old 4-hex chunk-index shards; the repository is still in development and repair can rebuild missing new-layout shards from chunks.
- No automatic full repository repair on restore/list startup; restore only runs full repair after unresolved snapshot content hashes prove the chunk index cannot satisfy the requested restore.

## Decisions

### Decision: Fixed Prefix-Length Constant

Use one internal constant on `ChunkIndexService` for chunk-index shard prefix length, for example `internal const int ShardPrefixLength = 2`. `Shard.PrefixOf(ContentHash)` uses this constant instead of `ContentHash.Prefix4`, slicing `contentHash.ToString()` rather than adding prefix-specific properties to `ContentHash`.

Alternatives considered:
- Keep 4 hex chars: preserves current layout but keeps tiny-shard behavior for small archives.
- Use 3 hex chars: middle ground, but still creates up to 4096 shards and remains sparse for small archives.
- Dynamic/adaptive prefix: solves both extremes, but introduces routing, migration, and partial-resize recoverability complexity.

Rationale: A 2-hex prefix bounds shard count at 256 and gives acceptable distribution for current scale targets. Keeping the constant on `ChunkIndexService` is the smallest change and avoids adding a separate layout type before there is more layout state to own.

### Decision: Lookup Repair Modes

`ChunkIndexService` exposes configurable lookup repair behavior through a repair mode. The planned modes are:

- `None`: lookup never repairs.
- `OnCorruptShard`: if a shard blob exists but cannot be deserialized, rebuild that prefix from chunks and retry.
- `OnMissingShardProbe`: includes corrupt-shard repair and, when a requested shard blob is missing, probes `chunks/<prefix>*` for at most one matching chunk. If a chunk exists for that prefix, rebuild the prefix and retry; if no chunk exists, treat the shard as empty.

Recommended callers:

- Archive: `OnCorruptShard`, because missing entries are normal for new content and must not trigger repair scans.
- Restore: `OnMissingShardProbe`, because a snapshot referencing a content hash whose shard is absent is suspicious and worth prefix-scoped repair.
- List: `OnCorruptShard` or `None`, because listing should avoid unexpected remote scans unless the caller explicitly accepts repair work.
- Explicit repair command: full rebuild, not lookup repair.

Valid shards are trusted. If a shard exists and parses but does not contain a requested entry, lookup returns a miss. That avoids treating every normal archive miss as possible corruption.

The repair scans `chunks/<prefix>*` and reconstructs entries from chunk blobs:
- Large chunk: blob name is the content hash and chunk hash; metadata supplies original and compressed sizes.
- Thin chunk: blob name is the content hash; metadata supplies original and compressed sizes; blob body supplies the tar chunk hash.
- Tar chunk: ignored directly for index reconstruction because thin chunks are the per-file mapping source.

Alternatives considered:
- Repair valid-but-missing entries: can heal partial indexes, but it makes ordinary archive misses expensive and ambiguous.
- Automatic full repair: resilient but too surprising and expensive for read paths.
- Rebuild every missing shard immediately: wasteful because missing shards are normal for prefixes with no chunks.

Rationale: Corrupt shard repair is unambiguously correct. Missing-shard probing covers the practical "shard blob was deleted" case for restore without making archive treat every new content hash as a repair trigger.

### Decision: Restore Full Repair Fallback

Restore first resolves all snapshot-referenced content hashes through `ChunkIndexService` using `OnMissingShardProbe`. If any content hashes remain unresolved after normal lookup and missing-shard probing, restore runs full chunk-index repair once, then retries lookup for the unresolved hashes. Restore fails only if entries remain unresolved after that retry.

Alternatives considered:
- Fail immediately and ask the user to run repair: explicit but unnecessary friction, because restore already knows repair is the likely next step.
- Repair valid-but-missing entries prefix-by-prefix during lookup: risks repeated prefix scans and conflates normal misses with incomplete index state.
- Run full repair at restore startup: robust but expensive even when the index is healthy.

Rationale: Full repair is expensive, but restore only pays it after proving that snapshot content cannot be resolved. Running it once avoids repeated prefix scans and makes restore self-healing for valid-but-incomplete chunk-index shards.

### Decision: Explicit Full Repair API and Command

Add a full chunk-index repair path that enumerates all chunks, rebuilds all shard files for the configured prefix length, and overwrites live `chunk-index/<prefix>` shard blobs in place. This is exposed as an API on the shared chunk-index service and as a CLI maintenance command. Full repair is idempotent: if interrupted, rerunning repair reconstructs the same shard contents from committed chunks and converges.

Alternatives considered:
- Tests call internal prefix repair only: insufficient coverage for maintenance behavior.
- Only expose CLI command: harder to test and reuse from future workflows.
- Stage repair output under a temporary prefix before publishing: cleaner in theory, but Azure Blob Storage has no atomic folder move; publishing would still require per-shard writes unless the storage format gained a manifest indirection.

Rationale: Prefix repair is the day-to-day safety net; full repair is the operator/test maintenance tool. Since repair starts from committed chunks and does not publish snapshots, in-place overwrite is acceptable and rerunnable.

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

For large chunks this naturally recovers from an index miss because the upload target is `chunks/<content-hash>`. For small files, archive does not add a pre-bundling remote thin-chunk check in this change, because that would add per-small-file remote I/O to the hot path. Thin chunk/index gaps are repaired by explicit full repair or by restore missing-shard probing when the shard itself is absent.

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

- **[Risk] Restore repair fallback can be slow** -> Probe only when the shard blob is absent, stop the probe after the first matching chunk, and run full repair at most once per restore after unresolved snapshot content remains. Log/report repair work.
- **[Risk] Changing `ListAsync` return type touches many callers** -> Keep the method name and add a simple `BlobListItem.Name` property; update callers mechanically.
- **[Risk] 2-hex prefix may create large shards for very large repositories** -> The prefix length is centralized as a const so it can be adjusted before release. Adaptive resizing remains intentionally out of scope.
- **[Risk] Parallel flush increases remote write pressure** -> Use a conservative bounded degree of parallelism and keep one worker per prefix.
- **[Risk] Valid-but-incomplete shards are not repaired during lookup** -> Restore handles this at feature level by running full repair once after unresolved snapshot content remains, then retrying unresolved lookups.

## Migration Plan

No backward-compatible migration is required. Existing repositories are development data. After the prefix length changes, old chunk-index shards at the previous layout are ignored; prefix-scoped repair and full repair rebuild the new layout from chunk blobs.

Rollback during development is also repair-based: if the prefix length is changed again, delete/rebuild chunk-index shards from chunks.
