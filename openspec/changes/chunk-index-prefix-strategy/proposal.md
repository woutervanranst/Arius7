## Why

The chunk-index shard prefix is currently hardcoded to 4 hex characters. That may not remain the best tradeoff as repositories scale: a shorter fixed prefix such as 3 hex characters could reduce the number of shard blobs, while a longer fixed prefix or a dynamic prefix policy could limit shard growth and hot-spotting. This question is larger than a simple performance tweak because shard-prefix strategy affects repository layout, cache behavior, and recovery semantics.

Chunk-index writes are not transactional across blobs. A run can upload some shard blobs and not others, another machine can extend overlapping prefixes concurrently, and local L1/L2 caches can observe mixed old/new state. Any future prefix strategy work therefore needs to treat durability and recoverability as first-class concerns, not just shard-count optimization.

## What Changes

- **Generalize prefix strategy**: replace the implicit "always 4 hex chars" assumption with an explicit chunk-index prefix strategy that can later be implemented as either a fixed length (for example always 3 or always 4 chars) or a dynamic prefix-length policy.
- **Explore tradeoffs before implementation**: compare fixed and dynamic strategies across shard count, shard size, lookup cost, flush cost, cache pressure, and operational simplicity.
- **Define durability and recoverability rules**: specify what correctness means under partial flushes, crashes, concurrent writers, cache invalidation, and mixed local/remote state while blob storage remains non-transactional across shard blobs.
- **Decide repository contract**: determine whether the chosen prefix strategy is repository metadata, configuration, a versioned format decision, or a deterministic algorithm derived from repository state.
- **Plan rollout and recovery**: define how caches and remote layout transitions are detected, invalidated, recovered, or migrated before any prefix change ships.
- **Require recovery proof**: add an integration test that deletes or corrupts the local and remote chunk-index state for a repository and proves Arius can rebuild a correct chunk-index view from scratch rather than depending on intact shard state.

## Non-goals

- Changing shard prefix length as part of `parallel-flush-and-tree-upload`.
- Choosing a final prefix strategy in this proposal without the durability and recoverability model.

## Capabilities

### New Capabilities
- `chunk-index-prefix-strategy`: Explicit repository rules for chunk-index shard partitioning, including future support for fixed or dynamic prefix length strategies.

### Modified Capabilities
- `blob-storage`: Chunk-index shard layout becomes a governed repository concern rather than an unexamined hardcoded constant.
- `archive-pipeline`: Future flush and lookup behavior may depend on the repository's declared chunk-index prefix strategy.

## Impact

- **Storage contract**: future implementation may affect `chunk-index/` naming, cache layout, or repository metadata.
- **Correctness work first**: durability, recoverability, mixed-state handling, and a recovery-from-scratch test are explicit design constraints for the follow-up phase.
- **Separation of concerns**: finalization parallelism can proceed now without blocking on a shard-layout decision.
