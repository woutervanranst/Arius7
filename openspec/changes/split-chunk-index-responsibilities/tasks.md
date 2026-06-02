## 1. Preserve Current Behavior With Focused Coverage

- [ ] 1.1 Review existing chunk-index lookup, flush, invalidation, and repair tests for coverage of facade behavior before extraction.
- [ ] 1.2 Split existing chunk-index tests by responsibility so facade behavior remains in `ChunkIndexService` tests and moved implementation behavior has focused component tests.
- [ ] 1.3 Add or adjust focused tests only where behavior currently lacks coverage for same-session lookup visibility, batched persisted lookup grouping, cache invalidation, mutable shard updates, per-prefix cache/store synchronization, and successful flush clearing write-session state.

## 2. Extract Shard Cache/Store

- [ ] 2.1 Create an internal shard cache/store component for prefix-scoped L1/L2/L3 read and update operations plus L1 eviction.
- [ ] 2.2 Move local L2 shard save behavior into the shard cache/store without changing local file format or paths.
- [ ] 2.3 Add shard update/rebuild operations that upload the remote shard, save L2, and promote L1 for flush and repair use without handing out caller-owned mutable cached shards.
- [ ] 2.4 Move L1 and L2 cache invalidation behavior into the shard cache/store while preserving repair-marker safety.
- [ ] 2.5 Treat `Shard` as an owned mutable page: replace copy-on-merge usage with explicit mutation operations and remove `Shard.Merge`.
- [ ] 2.6 Add async per-prefix synchronization inside the shard cache/store for read/load/mutate/save/upload/promote operations; do not make `Shard` internally concurrent.
- [ ] 2.7 Ensure read-only lookup returns copied results under the same prefix gate used by update/rebuild operations and never exposes cached mutable `Shard` ownership.

## 3. Extract Read And Write Responsibilities

- [ ] 3.1 Create an internal read-only chunk-index reader that performs single and batched persisted-index lookups through the shard cache/store, grouping batched persisted misses by shard prefix.
- [ ] 3.2 Create an internal chunk-index write session that owns session entries, pending entries, `AddEntry`, and `FlushAsync`.
- [ ] 3.3 Keep same-session entries visible through the `ChunkIndexService` facade before falling back to the read-only reader.
- [ ] 3.4 Keep fixed two-character shard-prefix calculation unchanged for reader and write-session grouping.
- [ ] 3.5 Make concurrent `AddEntry` calls safe with a write-session gate or stronger equivalent, and reject entry recording while `FlushAsync` is in progress.
- [ ] 3.6 Ensure `FlushAsync` snapshots pending entries before shard-cache/store I/O and clears write-session state only after the whole flush succeeds.

## 4. Slim Facade And Repair Integration

- [ ] 4.1 Update `ChunkIndexService` to delegate lookup, add, flush, and cache invalidation to the extracted internal components.
- [ ] 4.1a Construct extracted chunk-index collaborators inside `ChunkIndexService` instead of registering them separately in DI.
- [ ] 4.1b Keep `ChunkIndexService` public during this responsibility split and keep extracted collaborators non-public.
- [ ] 4.2 Keep repair orchestration on `ChunkIndexService` while reusing shard cache/store save and invalidation operations where practical.
- [ ] 4.3 Ensure successful repair clears write-session state and leaves the repair in-progress marker behavior unchanged.
- [ ] 4.4 Keep existing handler and DI call sites using `ChunkIndexService` as the facade.
- [ ] 4.5 Keep repair-marker checks at the `ChunkIndexService` facade boundary for normal lookup, entry recording, and flush operations so explicit repair can reuse internal shard-cache/store operations while the marker exists.
- [ ] 4.6 Ensure repair writes complete rebuilt shards as prefix replacements from in-memory groups rather than merging with stale L2 or remote shard contents.
- [ ] 4.7 Parallelize repair rebuilt-shard L2 write and remote upload work per prefix with bounded `Parallel.ForEachAsync`, keeping one worker per prefix.
- [ ] 4.8 Add an architecture test proving extracted chunk-index components are not consumed directly outside the chunk-index implementation boundary and `ChunkIndexService` remains the operation entry point.
- [ ] 4.9 Preserve and test the current full-repair behavior that groups reconstructed entries by shard prefix in memory before writing rebuilt L2 shard files.

## 5. Documentation And Verification

- [ ] 5.1 Update `docs/cache.md` to describe the separated read-through shard cache and archive write-session responsibilities.
- [ ] 5.2 Measure coverage for `src/Arius.Core/Shared/ChunkIndex/` and confirm line coverage is greater than 90%, including the extracted components.
- [ ] 5.3 Run focused chunk-index component, facade lookup, flush, invalidation, and repair tests.
- [ ] 5.4 Run `dotnet test --project src/Arius.Architecture.Tests/Arius.Architecture.Tests.csproj`.
- [ ] 5.5 Run relevant feature tests that use chunk-index lookup or write behavior.
- [ ] 5.6 Run `openspec validate split-chunk-index-responsibilities --strict` and confirm the change is apply-ready.
