## 1. Preserve Current Behavior With Focused Coverage

- [x] 1.1 Review existing chunk-index lookup, flush, invalidation, and repair tests for coverage of facade behavior before extraction.
- [x] 1.2 Split existing chunk-index tests by responsibility so facade behavior remains in `ChunkIndexService` tests and moved implementation behavior has focused component tests.
- [x] 1.3 Add or adjust focused tests only where behavior currently lacks coverage for same-session lookup visibility, session-overlay-first batched lookup, batched persisted lookup grouping, cache invalidation, repair-marker facade checks, mutable shard updates, per-prefix cache/store synchronization, and successful flush clearing write-session state.
- [x] 1.4 Add or preserve coverage that a partial flush failure fails the archive path without clearing write-session state or publishing a snapshot.

## 2. Extract Shard Cache/Store

- [x] 2.1 Create an internal shard cache/store component for prefix-scoped L1/L2/L3 read and update operations plus L1 eviction.
- [x] 2.2 Move local L2 shard save behavior into the shard cache/store without changing local file format or paths.
- [x] 2.3 Add shard update/rebuild operations that upload the remote shard, save L2, and promote L1 for flush and repair use without handing out caller-owned mutable cached shards.
- [x] 2.4 Move L1 and L2 cache invalidation behavior into the shard cache/store while preserving repair-marker safety.
- [x] 2.5 Treat `Shard` as an owned mutable page: replace copy-on-merge usage with explicit mutation operations and remove `Shard.Merge`.
- [x] 2.6 Add async per-prefix synchronization inside the shard cache/store for read/load/mutate/save/upload/promote operations; do not make `Shard` internally concurrent.
- [x] 2.7 Ensure read-only lookup returns copied results under the same prefix gate used by update/rebuild operations and never exposes cached mutable `Shard` ownership.

## 3. Extract Read And Write Responsibilities

- [x] 3.1 Create an internal read-only chunk-index reader that performs single and batched persisted-index lookups through the shard cache/store, grouping batched persisted misses by shard prefix.
- [x] 3.2 Create an internal chunk-index write session that owns session entries, pending entries, `AddEntry`, and `FlushAsync`.
- [x] 3.3 Keep same-session entries visible through the `ChunkIndexService` facade before falling back to the read-only reader.
- [x] 3.4 Keep fixed two-character shard-prefix calculation unchanged for reader and write-session grouping.
- [x] 3.5 Make concurrent `AddEntry` calls safe with a write-session gate or stronger equivalent, and reject entry recording while `FlushAsync` is in progress.
- [x] 3.6 Ensure `FlushAsync` snapshots pending entries before shard-cache/store I/O and clears write-session state only after the whole flush succeeds.

## 4. Slim Facade And Repair Integration

- [x] 4.1 Update `ChunkIndexService` to delegate lookup, add, flush, and cache invalidation to the extracted internal components.
- [x] 4.1a Construct extracted chunk-index collaborators inside `ChunkIndexService` instead of registering them separately in DI.
- [x] 4.1b Keep `ChunkIndexService` public during this responsibility split and keep extracted collaborators non-public.
- [x] 4.2 Keep repair orchestration on `ChunkIndexService` while reusing shard cache/store save and invalidation operations where practical.
- [x] 4.3 Ensure successful repair clears write-session state and leaves the repair in-progress marker behavior unchanged.
- [x] 4.4 Keep existing handler and DI call sites using `ChunkIndexService` as the facade.
- [x] 4.5 Keep repair-marker checks at the `ChunkIndexService` facade boundary for normal lookup, entry recording, and flush operations so explicit repair can reuse internal shard-cache/store operations while the marker exists.
- [x] 4.6 Ensure repair writes complete rebuilt shards as prefix replacements from in-memory groups rather than merging with stale L2 or remote shard contents.
- [x] 4.7 Parallelize repair rebuilt-shard L2 write and remote upload work per prefix with bounded `Parallel.ForEachAsync`, keeping one worker per prefix.
- [x] 4.8 Add an architecture test proving extracted chunk-index components are not consumed directly outside the chunk-index implementation boundary and `ChunkIndexService` remains the operation entry point.
- [x] 4.9 Preserve and test the current full-repair behavior that groups reconstructed entries by shard prefix in memory before writing rebuilt L2 shard files.
- [x] 4.10 Preserve full-repair stale shard deletion and idempotent rerun behavior while parallelizing rebuilt-prefix writes/uploads.

## 5. Documentation And Verification

- [x] 5.1 Update `docs/cache.md` after extraction if the current description no longer accurately describes the separated read-through shard cache, write-session overlay, repair-marker behavior, and full-repair rebuild flow.
- [x] 5.2 Measure coverage for `src/Arius.Core/Shared/ChunkIndex/` and confirm line coverage is greater than 90%, including the extracted components.
- [x] 5.3 Run focused chunk-index component, facade lookup, flush, invalidation, and repair tests.
- [x] 5.4 Run `dotnet test --project src/Arius.Architecture.Tests/Arius.Architecture.Tests.csproj`.
- [x] 5.5 Run relevant feature tests that use chunk-index lookup or write behavior.
- [x] 5.6 Run `openspec validate split-chunk-index-responsibilities --strict` and confirm the change is apply-ready.
