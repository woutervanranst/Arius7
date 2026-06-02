## 1. Preserve Current Behavior With Focused Coverage

- [ ] 1.1 Review existing chunk-index lookup, flush, invalidation, and repair tests for coverage of facade behavior before extraction.
- [ ] 1.2 Add or adjust focused tests only where behavior currently lacks coverage for same-session lookup visibility, cache invalidation, and successful flush clearing write-session state.

## 2. Extract Shard Cache/Store

- [ ] 2.1 Create an internal shard cache/store component for L1/L2/L3 shard loading and L1 eviction.
- [ ] 2.2 Move local L2 shard save behavior into the shard cache/store without changing local file format or paths.
- [ ] 2.3 Add a shard save/upload operation that uploads the remote shard, saves L2, and promotes L1 for flush and repair use.
- [ ] 2.4 Move L1 and L2 cache invalidation behavior into the shard cache/store while preserving repair-marker safety.

## 3. Extract Read And Write Responsibilities

- [ ] 3.1 Create an internal read-only chunk-index reader that performs single and batched persisted-index lookups through the shard cache/store.
- [ ] 3.2 Create an internal chunk-index write session that owns session entries, pending entries, `AddEntry`, and `FlushAsync`.
- [ ] 3.3 Keep same-session entries visible through the `ChunkIndexService` facade before falling back to the read-only reader.
- [ ] 3.4 Keep fixed two-character shard-prefix calculation unchanged for reader and write-session grouping.

## 4. Slim Facade And Repair Integration

- [ ] 4.1 Update `ChunkIndexService` to delegate lookup, add, flush, and cache invalidation to the extracted internal components.
- [ ] 4.2 Keep repair orchestration on `ChunkIndexService` while reusing shard cache/store save and invalidation operations where practical.
- [ ] 4.3 Ensure successful repair clears write-session state and leaves the repair in-progress marker behavior unchanged.
- [ ] 4.4 Keep existing handler and DI call sites using `ChunkIndexService` as the facade.

## 5. Documentation And Verification

- [ ] 5.1 Update `docs/cache.md` to describe the separated read-through shard cache and archive write-session responsibilities.
- [ ] 5.2 Run focused chunk-index lookup and repair tests.
- [ ] 5.3 Run relevant feature tests that use chunk-index lookup or write behavior.
- [ ] 5.4 Run `openspec status --change split-chunk-index-responsibilities` and confirm the change is apply-ready.
