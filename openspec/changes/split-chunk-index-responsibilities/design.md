## Context

`ChunkIndexService` is the shared repository metadata index that maps content hashes to chunk metadata. It currently combines several responsibilities in one class: public lookup APIs, archive write buffering, pending-entry flush, L1/L2/L3 shard cache mechanics, cache invalidation, and full repair orchestration.

This makes the archive write path look like part of the read cache even though it has different constraints. The L1 shard-cache budget only bounds materialized shard pages; the archive write buffer grows with newly uploaded chunks until flush. A minimal responsibility split will make that distinction explicit without changing persisted data or command behavior.

## Goals / Non-Goals

**Goals:**

- Preserve current external behavior and repository compatibility.
- Keep `ChunkIndexService` as the facade used by feature handlers and tests during this change.
- Separate read-through shard cache mechanics from archive write-session state.
- Separate read-only lookup behavior from write buffering and flushing.
- Keep repair behavior correct while allowing it to reuse the extracted shard cache/store functionality.
- Make future bounded write sessions and dynamic shard routing easier to introduce later.

**Non-Goals:**

- Do not introduce dynamic shard prefixes or shard splitting.
- Do not change the two-character shard-prefix layout.
- Do not change chunk-index blob names, local cache paths, or shard serialization formats.
- Do not make archive write buffering bounded in this change.
- Do not replace current feature-handler injection with separate public reader/writer services.
- Do not introduce distributed locking or concurrent archive coordination.

## Decisions

### Keep `ChunkIndexService` as the facade

Current callers use `ChunkIndexService` directly from archive, restore, list, hydration-status, repair, integration tests, and E2E fixtures. This refactor keeps that facade stable and delegates work internally.

`ChunkIndexService` remains the operational boundary. The extracted reader, write session, and shard cache/store are implementation details, not independently callable services. They should not be registered separately in DI or consumed directly by feature handlers, other shared services, tests that exercise user-facing behavior, or future callers. An architecture test should capture this boundary so the split does not turn into a new public service graph by accident.

Alternative considered: inject separate reader and writer services into feature handlers immediately. That is a cleaner endpoint, but it expands the change across more callers and tests without changing behavior. Keeping the facade limits the first refactor to the chunk-index implementation boundary.

### Extract a shard cache/store component

Move L1/L2/L3 shard loading, L1 eviction, L2 persistence, remote shard upload, and cache invalidation into an internal component, tentatively `ChunkIndexShardCache`.

This component should expose operations at the shard-prefix level:

- load a shard by prefix through L1/L2/L3
- save/upload a complete shard and update L2/L1
- invalidate L1
- invalidate L1 and L2 cache state

It should not own `_sessionEntries`, `_pendingEntries`, public lookup behavior, or repair entry reconstruction.

The shard cache/store may be used by other chunk-index implementation components where that keeps the facade small, but it must not become a direct dependency of non-chunk-index code. Normal archive, restore, list, and repair workflows still enter chunk-index behavior through `ChunkIndexService`.

`Shard` should be treated as an owned mutable in-memory shard page, not an immutable value. Replace the current copy-on-merge shape with explicit mutation operations such as `AddOrUpdate` or `AddOrUpdateRange`, and remove the `Shard.Merge` method. Deterministic serialization still sorts entries by content hash, so persisted format remains unchanged.

The shard cache/store owns thread-safety for mutable shard pages. It should provide per-prefix synchronization around operations that load, mutate, save, upload, or promote a shard for a prefix. The implementation should not make `Shard` itself thread-safe with `ConcurrentDictionary`; shard-level concurrency does not protect the larger L1/L2/L3 update sequence. Current flush and repair callers still group work so one worker processes a prefix, but the cache/store should enforce the prefix-level boundary rather than relying only on caller discipline.

Alternative considered: keep cache methods private inside `ChunkIndexService` and only extract write buffering. That leaves the largest mixed responsibility in place and gives little future seam for routing and split policy.

Alternative considered: make `Shard` internally thread-safe with `ConcurrentDictionary`. That adds per-entry overhead and still does not synchronize L1 promotion, L2 persistence, or remote upload for a prefix, so synchronization belongs at the shard cache/store boundary instead.

### Extract a read-only reader component

Move persisted-index lookup behavior into an internal reader, tentatively `ChunkIndexReader`, backed by the shard cache/store. The reader resolves content hashes to shard prefixes using the current fixed prefix calculation and looks up entries in loaded shards.

The reader should not know about session write-buffer entries. `ChunkIndexService` can preserve current lookup semantics by checking the write session overlay first, then delegating misses to the reader.

For batched lookup, the facade should split the input into write-session hits and persisted-index misses before calling the reader. The reader should receive only the misses, and the facade should merge the session hits with reader results. This keeps session overlay behavior at the operational boundary and avoids loading shards for content hashes already recorded during the current archive session.

Alternative considered: let the reader check session entries. That keeps the lookup API convenient but makes the read-only component depend on archive write state, which is the coupling this change is meant to remove.

Alternative considered: pass all hashes to the reader and let facade-level session entries overwrite persisted results afterward. That preserves returned values but performs unnecessary shard loads and weakens the reader/write-session separation.

### Extract an archive write session component

Move `_sessionEntries`, `_pendingEntries`, `AddEntry`, and `FlushAsync` behavior into an internal write-session component, tentatively `ChunkIndexWriteSession`.

The write session remains unbounded in this change. It should keep the current semantics:

- newly added entries are visible to same-service lookups before flush
- pending entries are grouped by fixed shard prefix at flush
- each touched shard is loaded, mutated with pending entries, uploaded, saved to L2, and promoted to L1
- session and pending state are cleared only after the whole flush succeeds, or after successful repair

The write session should not clear entries per prefix as each prefix succeeds. If any touched shard upload or cache update fails, `FlushAsync` should fail without publishing a snapshot and without treating the write-session state as successfully flushed. This preserves current retry semantics and keeps partial remote flush recovery aligned with the archived scalability change: rerun archive or run explicit full repair.

Alternative considered: make the write session immediately bounded or disk-backed. That is the likely next hardening step for very large archives, but it changes archive runtime behavior and failure modes. This change only separates responsibilities.

Alternative considered: clear successfully flushed prefixes as each prefix completes. That may reduce duplicate work after an in-process retry, but it changes partial-failure semantics and would need a more explicit recovery contract for entries that were cleared locally before the overall archive failed.

### Keep repair orchestration in the facade initially

Full repair can remain on `ChunkIndexService` for the first split. It should use the shard cache/store for local shard writes, remote shard upload, and cache invalidation where practical, and clear the write session after successful repair.

Repair-in-progress marker enforcement stays on `ChunkIndexService` for normal operations. Lookup, entry recording, and flush should fail before delegating when the marker exists. The extracted internal components should not independently block shard-cache/store operations solely because the marker exists, because explicit repair owns that marker and must be able to rebuild local shard state and upload repaired shards while the marker is present.

Full repair remains an in-memory reconstruction workflow in this change: it scans committed chunks, groups reconstructed entries by shard prefix in memory, then writes rebuilt shard contents to L2 before uploading remote shards. The shard cache/store extraction should support repair writing complete rebuilt shards, but it should not turn repair into a streaming per-entry L2 merge workflow during this refactor.

Repair should parallelize rebuilt-shard processing per prefix with bounded `Parallel.ForEachAsync`. The metadata-aware `chunks/` listing remains a single scan that builds the in-memory prefix groups. After that scan, each rebuilt prefix can independently write its L2 shard and upload the corresponding remote shard. No two repair workers should process the same prefix concurrently.

Alternative considered: extract `ChunkIndexRepairService` now. That may be useful later, but repair has domain-specific reconstruction rules and command-facing behavior. Extracting it at the same time risks turning a minimal split into a broader rewrite.

Alternative considered: stream each reconstructed repair entry directly into L2 shard state while listing chunks, keeping only prefix metadata in memory. That would better bound repair memory usage, but it changes the current repair implementation shape and is not required for this responsibility split.

### Keep fixed prefix calculation unchanged

The refactor should keep using `Shard.PrefixOf(contentHash)` and `ChunkIndexService.ShardPrefixLength`. No routing table, longest-prefix lookup, split manifest, or new blob layout is introduced.

The extracted internal reader, write-session, and shard cache/store components may depend on `ChunkIndexService.ShardPrefixLength` and `ChunkIndexService.FlushWorkers` during this change. Introducing a separate chunk-index layout/options abstraction is intentionally deferred until there is more layout state to own. This keeps the follow-up focused on responsibility extraction and preserves the accepted fixed-prefix design from `improve-chunk-index-scalability`.

This creates a narrow seam for future routing without changing today's repository format.

Alternative considered: introduce an internal chunk-index layout/options type now and move prefix length or flush-worker constants there. That may become appropriate with adaptive routing, persisted layout metadata, or configurable write-session policy, but it is unnecessary indirection for the current fixed-layout refactor.

## Risks / Trade-offs

- [Risk] The facade plus internal components may temporarily add indirection without reducing public API surface. -> Mitigation: keep extracted components internal, small, and directly aligned to existing methods.
- [Risk] Behavior can drift during extraction, especially session-entry visibility and repair-marker checks. -> Mitigation: preserve existing tests and add focused tests where behavior moves behind new seams.
- [Risk] The write session remains unbounded, so the memory risk is not solved. -> Mitigation: document this as a non-goal and keep the extracted write session as the future place for bounded flushing.
- [Risk] Repair may still feel too large in `ChunkIndexService`. -> Mitigation: leave repair extraction as a separate follow-up after read/write/cache seams are stable.
- [Risk] Internal components become accidental service boundaries. -> Mitigation: keep them internal, avoid separate DI registrations, and add an architecture test that enforces `ChunkIndexService` as the operational boundary.
- [Risk] Internal component names may imply future adaptive routing before it exists. -> Mitigation: keep names tied to current behavior: reader, write session, shard cache/store.
