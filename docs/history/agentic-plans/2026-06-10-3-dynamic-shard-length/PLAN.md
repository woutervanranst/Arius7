# Dynamic chunk-index shard prefix length

Repo: `/Users/wouter/.superset/worktrees/Arius7/dynamic-shard-prefix` (branch `dynamic-shard-prefix`)

## Context

The chunk index currently uses a fixed 2-hex-char shard prefix: every entry lives in one of 256 remote blobs `chunk-index/aa`. The spec already calls this an "interim internal routing decision." As repos grow, shards grow unbounded, making each cold download and each flush rewrite heavier. This change makes the prefix length dynamic: small archives keep the 256-shard layout; when a shard exceeds a threshold at flush time it splits 16-way by the next hex char (`aa` → `aa0`..`aaf`), recursively and unevenly per subtree (`aaf` may later split into `aaf0`..`aafc` while `ab` never splits). Only **non-empty** shards are written (current contract preserved). No layout manifest — the layout is self-describing from blob existence.

**Scope decisions (user-confirmed):**
- Single archiver assumed. Concurrent archives from two machines are a pre-existing known limitation (last-writer-wins shard overwrites exist today); etag preconditions (If-Match) are explicitly deferred. Repair is the backstop.
- Threshold = `internal const int MaxShardEntryCount = 10_000` — trivially changeable later; routing is existence-based so changing it needs no migration (a lowered value splits shards on their next flush).
- Breaking changes to persisted formats OK (no migration; user wipes `~/.arius` + old repos).
- `openspec/` spec updates deferred. `docs/cache.md` IS updated (otherwise actively misleading).

## Design rules (the protocol)

1. **Authority walk (lookup, PARENT WINS):** for a hash, start `p = hash[:2]`. If blob `p` exists → `p` is authoritative. Else if any existing blob strictly longer than `p` starting with `p` exists → descend (`p` += next hex char of hash) and repeat. Else → range empty (miss; this `emptyAt` depth is also where a new shard would be created). Never assumes `hash[:2]` exists; handles skip-level layouts (`aa` absent, `aa3` absent, `aa3f` present).
2. **Why parent wins is crash-safe:** archive order is upload chunks → flush (split: write children, THEN delete parent) → publish snapshot. A crashed split never published its snapshot, so every entry any *published* snapshot references is still in the intact parent (deleted last). Half-written children carry only copies + unpublished entries; the crashed machine's pending SQLite rows survive and its retry re-flushes.
3. **Flush (self-healing):** group pending hashes by 2-char root; parallel across roots (`FlushWorkers`), per-root gate. Target shard per pending hash = same authority walk over one subtree listing (fresh leaf at `emptyAt` when nothing exists — never write at a proper ancestor of the resolved depth). Merged shard ≤ threshold → overwrite upload. Over threshold → split: partition recursively into non-empty leaves, upload ALL leaves, then delete the parent and every listed blob in range(p) not just written (cleans own/foreign crashed-split leftovers). `MarkPendingFlushesSynchronized` once after ALL roots succeed (unchanged invariant).
4. **Empty-vs-descend is decided by the LISTING, never by `TryDownload == null`.** A download-null after a listing hit (racing reader-vs-split) → re-list the root once; second miss → treat as empty. Negative ("empty") coverage claims are recorded at the terminal walk depth only.
5. **Coverage claims (`loaded_prefixes`)** become variable-length validated range claims. Hot path stays zero-remote-calls: a claim whose prefix covers the hash at the latest snapshot version serves locally. Inserting a claim transactionally deletes overlapping ancestor/descendant claims (so a split replaces the parent claim with child claims at mark-clean time).
6. **Repair re-balances:** after staging all entries in SQLite, compute the layout fresh (recursive count-based split from each 2-char root), upload non-empty leaves, existing stale-deletion pass removes everything else — including coarsening an over-split layout.

Adversarially reviewed: single-machine crash story verified at every crash point (incl. `InvalidateCaches` between crash and retry). Multi-writer holes (parent resurrection by a stale second archiver) acknowledged and out of scope per the single-archiver decision; noted in docs as a known limitation.

## Work items (ordered; each is a commit)

### WI-1: Blob listing by raw name prefix — **blocking discovery**
`IBlobContainerService.ListAsync(RelativePath, ...)` is segment-aligned: Azure (`AzureBlobContainerService.cs:187` via `ToBlobPrefix`) appends a trailing `/`, the fake uses segment-boundary `StartsWith` — so listing `chunk-index/aa` returns nothing, not even `chunk-index/aa` itself. Add an overload:
```csharp
IAsyncEnumerable<BlobListItem> ListAsync(RelativePath directory, string namePrefix,
    bool includeMetadata = false, CancellationToken cancellationToken = default);
```
- `src/Arius.Core/Shared/Storage/IBlobContainerService.cs`, `src/Arius.AzureBlob/AzureBlobContainerService.cs` (native: `prefix: $"{directory.ToBlobPrefix()}{namePrefix}"`), `src/Arius.Tests.Shared/Storage/FakeInMemoryBlobContainerService.cs` (raw string filter; record into a NEW `ListedPrefixes` collection, NOT `RequestedBlobNames`, so download-count assertions stay meaningful), plus one-line delegations in the ~16 fakes/decorators (compiler will enumerate). `CountingBlobContainerService` gets a `ChunkIndexLists` counter.
- Tests: Azurite/fake parity — seed `chunk-index/aa`, `chunk-index/aa0`, `chunk-index/ab`; `ListAsync(ChunkIndexPrefix, "aa")` returns exactly the first two, with ETags.

### WI-2: Local store schema v2 + router helpers
`src/Arius.Core/Shared/ChunkIndex/ChunkIndexLocalStore.cs`:
- Drop the routed `prefix` column from `chunk_index_entries` (+ its indexes; replace pending index with partial `ON chunk_index_entries(pending_flush) WHERE pending_flush = 1`). Range queries via BLOB bounds on the `content_hash` PK (memcmp ordering): bounds = `hexPrefix.PadRight(64,'0')` / `PadRight(64,'f')` — odd-nibble prefixes (`aa3`) work naturally.
- `SchemaVersion = "2"` + **add version enforcement** (today `CreateOrUpgradeSchema` blindly upserts the version; a v1 DB would break inserts). On mismatch: recreate DB (no migration, per scope).
- New/changed methods: `ReadRangeEntries(prefix)`, `CountRangeEntries(prefix)`, `GetRootsWithPendingFlushes()` (`SELECT DISTINCT lower(substr(hex(content_hash),1,2)) … WHERE pending_flush = 1` — note SQLite `hex()` is uppercase), `GetPendingFlushHashes(root)`, `GetStoredRootPrefixes()`, `FindCoveredPrefix(hash, snapshotVersion)` (+ a `FindCoveredPrefixState` variant returning remote_exists/etag for the flush fast path). `UpdatePrefix`/`AddEmptyPrefix` delete clean rows **by range**. `BindEntry`/`CreateUpsertCommand` lose the `$prefix` binding.
- Coverage overlap maintenance in `UpsertLoadedPrefix`: delete claims where the existing row is an ancestor or descendant of the inserted prefix (directional `substr` comparison; never siblings).

`src/Arius.Core/Shared/ChunkIndex/ChunkIndexRouter.cs` (stays internal — `DependencyTests.cs:330-355`): `GetRootPrefix(hash)`, `FindAuthoritativeShard(existingNames, hash, out emptyAt)` (the walk), `PartitionIntoLeaves(basePrefix, entries, maxEntries)` (recursive, non-empty only), `GetHashRangeBounds(hexPrefix)`. Delete `Shard.PrefixOf` (`Shard.cs:149`).

`ChunkIndexService.cs` constants: `ShardPrefixLength = 2` → `MinShardPrefixLength = 2`; add `MaxShardEntryCount = 10_000` + internal ctor param `int maxShardEntryCount = MaxShardEntryCount` (split tests use 3–5 instead of 10k entries).

### WI-3: Lookup — coverage-based, per-root gates
`ChunkIndexService.cs`: replace `EnsurePrefixLoadedAndSynchronizedAsync(prefix, …)` with `EnsureCoverageForHashesAsync(root, hashes, latestSnapshot, ct)`; `_prefixGates` keyed by 2-char root (lookup and flush MUST agree on key derivation). Under the gate:
1. Filter hashes already covered (`FindCoveredPrefix` at latest snapshot) → all covered = zero remote calls (preserves hot path).
2. One subtree listing per root (WI-1 overload) → name→etag map.
3. Per uncovered hash: `FindAuthoritativeShard` → null ⇒ `AddEmptyPrefix(emptyAt, …)` (dedupe); shard `p` ⇒ load set.
4. Per shard: etag from listing matches stored → `SetPrefixSnapshotVersion` (revalidation now costs zero downloads); else `TryDownloadAsync` + deserialize (keep `ChunkIndexCorruptException` wrapping) + `UpdatePrefix`. Download-null after listing hit → re-list once, then treat as empty.

`LookupAsync(batch)`: group by `GetRootPrefix` instead of leaf prefix; pending-flush short-circuit and final `FindEntry` per hash unchanged. Single-hash `LookupAsync` delegates with a one-element list. `InvalidateCaches`/`PromoteToSnapshotVersionAsync` unchanged.

### WI-4: Flush — split at threshold, self-healing
`FlushAsync`: iterate `GetRootsWithPendingFlushes()`, `Parallel.ForEachAsync` with `FlushWorkers`, `FlushRootAsync(root, …)` owns the root gate (**factor a gate-free loading core shared with WI-3 — re-entrant gate = deadlock**). Per root:
1. Fast path per pending hash: existing coverage row at latest snapshot resolves the target with zero remote calls (preserves warm-flush scenarios). Otherwise one subtree listing + authority walk; ensure each authoritative shard is loaded/revalidated before merging.
2. Per target `p`: `Shard` from `ReadRangeEntries(p)` (clean + pending in range). Count ≤ `_maxShardEntryCount` → `UploadShardAsync(p)` (overwrite, as today), record etag. Else split: `PartitionIntoLeaves`, upload every non-empty leaf, **then** re-list subtree and delete every blob in range(p) not just written (parent + stale leftovers).
3. After ALL roots: `MarkPendingFlushesSynchronized(uploadedStates, …)` — unchanged all-or-nothing invariant; coverage-overlap deletion (WI-2) replaces the parent claim with leaf claims here.

Convergence notes to encode as comments/tests: crash mid-split leaves parent+children → parent wins on read; same-machine retry re-resolves authority = parent (shallowest), reloads, re-splits, deletes parent. A non-split overwrite may leave a foreign stale child shadowed by its parent — harmless (parent wins), cleaned by next split or repair.

### WI-5: Repair — layout computation / re-balancing
`RepairAsync`: replace `GetStoredPrefixes()` with recursive `CollectLeaves(root)` using `CountRangeEntries` (≤ threshold → leaf; else recurse into 16 children, skip empty). Upload pass over leaves (range-based shard build); existing stale-deletion pass (`ListAsync(ChunkIndexPrefix)` + `rebuiltPrefixes.Contains`) works unchanged — flat single-segment names — and provides coarsening for free.

### WI-6: Docs
`docs/cache.md` (~lines 97–106 + flush flowchart): describe dynamic depth, split-at-flush, parent-wins walk; document the single-archiver limitation. `BlobPaths.ChunkIndexShardPath` doc comment mentions variable-length prefixes. openspec/ untouched.

## Test changes

Mechanical: `ShardPrefixLength` → `MinShardPrefixLength` (6 call sites across Core.Tests + Integration.Tests); `Shard.PrefixOf`/`GetLeafPrefix` → `GetRootPrefix` (~30 sites incl. `ArchiveRecoveryTests.cs:105`, `ShardTests.cs:214`, `ChunkIndexLocalStoreTests` ×10).

Behavioral: miss-path lookup tests now assert 1 list + 0 downloads (`LookupAsync_MissingRemoteShard_ReturnsMiss` etc.); etag-revalidation scenario gets cheaper (list-only); `ChunkIndexServiceArchiveScenarioTests` traffic tables updated (unit + integration); `ChunkIndexLocalStoreTests` schema `"1"`→`"2"`, white-box prefix-column SELECT removed.

New tests:
- `ChunkIndexRouterTests`: walk (parent wins, descend, skip-level `aa`/`aa3f`, emptyAt depths), `PartitionIntoLeaves` (non-empty only, recursive), `GetHashRangeBounds` odd-nibble.
- LocalStore: range queries incl. odd-nibble boundaries; roots-with-pending; coverage `FindCoveredPrefix` + overlap-deletion (ancestor↔descendant, snapshot filtering); schema-recreate-on-mismatch.
- Flush (tiny ctor threshold): split-at-threshold (children only, parent deleted, all hashes resolvable from a fresh instance); recursive split; **interrupted-split retry converges** (fault after first child upload → parent+partial children → fresh instance flush → clean layout); new entries in an empty child range after a complete split (writes just that child, no parent resurrection); foreign stale child cleaned on split.
- Lookup: parent+child coexist → parent wins (only parent downloaded); descend-to-child after complete split (then zero-call repeat); empty child range = miss with 1 list/0 downloads.
- Repair: splits an over-threshold root; coarsens an over-split layout.
- Integration (Azurite): split roundtrip — archive past threshold, flush, second machine resolves all hashes (this catches the WI-1 trailing-slash semantics against real Azure); `ChunkIndexLists` counter assertions.

## Verification

1. `dotnet build` + `dotnet test` on `Arius.Core.Tests`, `Arius.Architecture.Tests` (router/local-store internality rules must still pass), then `Arius.Integration.Tests` (Azurite).
2. Coverage gate: spec requires ≥90% line coverage for `src/Arius.Core/Shared/ChunkIndex/` — run the existing coverage check.
3. End-to-end sanity: archive a directory against Azurite with a tiny threshold override, verify `chunk-index/` blob names split as expected, restore on a fresh cache identity, verify all files restore.

## Known limitations (documented, accepted)

- Concurrent archives from two machines can overwrite each other's shards (pre-existing; splits raise stakes). Fix = etag preconditions on chunk-index writes/deletes — deferred follow-up.
- `PromoteToSnapshotVersionAsync` wholesale promotion and the flush-without-publish path (unchanged root hash) are pre-existing multi-machine staleness issues, unchanged by this work.
