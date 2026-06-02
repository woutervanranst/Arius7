## Why

`ChunkIndexService` currently owns read-through shard caching, archive write buffering, shard persistence, and repair behavior in one class. That coupling makes archive memory behavior harder to reason about and blocks future chunk-index evolution, including bounded write sessions and adaptive shard routing.

## What Changes

- Split the existing chunk-index implementation into focused internal components while preserving current external behavior.
- Keep `ChunkIndexService` as the operational boundary and public facade injected into existing handlers during this change.
- Add architecture coverage that prevents feature handlers, DI registrations, and other shared services from consuming the extracted internal components directly.
- Move L1/L2/L3 shard loading, persistence, and cache invalidation into a dedicated shard cache/store component.
- Treat `Shard` as an owned mutable in-memory page behind the shard cache/store boundary, replacing `Shard.Merge` with explicit add-or-update mutation.
- Move archive-session write buffering and flushing into a dedicated write-session component.
- Move read-only lookup behavior into a dedicated reader component that uses the shard cache/store.
- Parallelize full repair's rebuilt-shard write/upload work per shard prefix with bounded `Parallel.ForEachAsync` while preserving the one metadata-aware chunk listing and in-memory reconstruction grouping.
- Keep fixed two-character shard prefixes and the existing blob layout unchanged.
- Keep repair command behavior and repository safety checks unchanged, while allowing repair to use the extracted components.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `chunk-index-service`: Clarify that chunk-index responsibilities are separated between read-through lookup/cache behavior and archive write-session behavior without changing repository data format or user-visible command behavior.
- `architecture-tests`: Enforce that extracted chunk-index internals stay behind the `ChunkIndexService` facade.

## Impact

- Affected code: `src/Arius.Core/Shared/ChunkIndex/`, `src/Arius.Core/ServiceCollectionExtensions.cs`, and tests under `src/Arius.Core.Tests/Shared/ChunkIndex/`.
- Affected tests: split existing chunk-index tests by extracted responsibility, add focused coverage for the new components, and add an architecture test for the facade boundary under `src/Arius.Architecture.Tests/`.
- Public behavior: no command-line behavior, persisted blob naming, shard serialization, or repository compatibility changes.
- APIs: `ChunkIndexService` remains the compatibility facade for current callers; extracted components are internal implementation details.
- Dependencies: no new external dependencies.
