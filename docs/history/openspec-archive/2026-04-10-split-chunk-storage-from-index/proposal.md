## Why

Chunk blob mechanics are currently split across `ArchiveCommandHandler`, `RestoreCommandHandler`, and `ChunkHydrationStatusQueryHandler`, while `ChunkIndexService` only owns content-hash lookup and shard caching. That makes feature handlers responsible for chunk blob names, metadata keys, content types, hydration state rules, crash-recovery upload rules, and primary-vs-rehydrated download selection, which weakens the intended Shared-vs-Features boundary.

## What Changes

- Add a new shared `ChunkStorageService` that owns chunk blob upload, download, hydration status, rehydration initiation, and rehydrated-cleanup planning.
- Keep `ChunkIndexService` focused on content-hash lookup, pending shard recording, shard flush, and cache invalidation.
- Move the shared `ChunkHydrationStatus` type out of the query feature and into `Shared/ChunkIndex/ChunkHydrationStatus.cs` so chunk storage and features reuse one shared vocabulary.
- Refactor `ArchiveCommandHandler`, `RestoreCommandHandler`, and `ChunkHydrationStatusQueryHandler` to use `ChunkStorageService` for chunk blob protocol details while keeping orchestration decisions in the features.
- Introduce `RepositoryPaths` as the shared home for repository-local cache/log directory helpers that are currently exposed from `ChunkIndexService`.

## Capabilities

### New Capabilities
- `chunk-storage-service`: Shared ownership of chunk blob protocol, hydration state, rehydrated cleanup planning, and feature-facing chunk storage APIs.

### Modified Capabilities

## Impact

- **Core layer** (`Arius.Core`): adds `ChunkStorageService`, a cleanup-plan abstraction, and a shared `ChunkHydrationStatus` type; narrows `ChunkIndexService` responsibility.
- **Feature handlers**: `ArchiveCommandHandler`, `RestoreCommandHandler`, and `ChunkHydrationStatusQueryHandler` stop knowing about `BlobPaths.Chunk*`, chunk metadata keys, chunk content types, and hydration state rules.
- **Local repository paths**: `GetRepoDirectoryName` and related cache-path helpers move out of `ChunkIndexService` into `RepositoryPaths`.
- **Tests**: chunk upload/download/hydration tests move toward the new shared service boundary, and feature tests verify handler orchestration through the two chunk services rather than raw blob details.
