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

Alternative considered: keep cache methods private inside `ChunkIndexService` and only extract write buffering. That leaves the largest mixed responsibility in place and gives little future seam for routing and split policy.

### Extract a read-only reader component

Move persisted-index lookup behavior into an internal reader, tentatively `ChunkIndexReader`, backed by the shard cache/store. The reader resolves content hashes to shard prefixes using the current fixed prefix calculation and looks up entries in loaded shards.

The reader should not know about session write-buffer entries. `ChunkIndexService` can preserve current lookup semantics by checking the write session overlay first, then delegating misses to the reader.

Alternative considered: let the reader check session entries. That keeps the lookup API convenient but makes the read-only component depend on archive write state, which is the coupling this change is meant to remove.

### Extract an archive write session component

Move `_sessionEntries`, `_pendingEntries`, `AddEntry`, and `FlushAsync` behavior into an internal write-session component, tentatively `ChunkIndexWriteSession`.

The write session remains unbounded in this change. It should keep the current semantics:

- newly added entries are visible to same-service lookups before flush
- pending entries are grouped by fixed shard prefix at flush
- each touched shard is loaded, merged, uploaded, saved to L2, and promoted to L1
- session and pending state are cleared after successful flush or repair

Alternative considered: make the write session immediately bounded or disk-backed. That is the likely next hardening step for very large archives, but it changes archive runtime behavior and failure modes. This change only separates responsibilities.

### Keep repair orchestration in the facade initially

Full repair can remain on `ChunkIndexService` for the first split. It should use the shard cache/store for local shard writes, remote shard upload, and cache invalidation where practical, and clear the write session after successful repair.

Repair-in-progress marker enforcement stays on `ChunkIndexService` for normal operations. Lookup, entry recording, and flush should fail before delegating when the marker exists. The extracted internal components should not independently block shard-cache/store operations solely because the marker exists, because explicit repair owns that marker and must be able to rebuild local shard state and upload repaired shards while the marker is present.

Alternative considered: extract `ChunkIndexRepairService` now. That may be useful later, but repair has domain-specific reconstruction rules and command-facing behavior. Extracting it at the same time risks turning a minimal split into a broader rewrite.

### Keep fixed prefix calculation unchanged

The refactor should keep using `Shard.PrefixOf(contentHash)` and `ChunkIndexService.ShardPrefixLength`. No routing table, longest-prefix lookup, split manifest, or new blob layout is introduced.

This creates a narrow seam for future routing without changing today's repository format.

## Risks / Trade-offs

- [Risk] The facade plus internal components may temporarily add indirection without reducing public API surface. -> Mitigation: keep extracted components internal, small, and directly aligned to existing methods.
- [Risk] Behavior can drift during extraction, especially session-entry visibility and repair-marker checks. -> Mitigation: preserve existing tests and add focused tests where behavior moves behind new seams.
- [Risk] The write session remains unbounded, so the memory risk is not solved. -> Mitigation: document this as a non-goal and keep the extracted write session as the future place for bounded flushing.
- [Risk] Repair may still feel too large in `ChunkIndexService`. -> Mitigation: leave repair extraction as a separate follow-up after read/write/cache seams are stable.
- [Risk] Internal components become accidental service boundaries. -> Mitigation: keep them internal, avoid separate DI registrations, and add an architecture test that enforces `ChunkIndexService` as the operational boundary.
- [Risk] Internal component names may imply future adaptive routing before it exists. -> Mitigation: keep names tied to current behavior: reader, write session, shard cache/store.
