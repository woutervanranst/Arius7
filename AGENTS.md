<!-- https://dev.to/webdeveloperhyper/how-to-make-ai-follow-your-instructions-more-for-free-openspec-2c85 -->

# AGENTS.md

## Way of Working

- Work Test-Driven: first, write a failing test. Then, implement.
- Avoid coupling the test to the implementation - test the behavior.
- When making code changes, always run ALL the tests.
- When the tests pass, make a conventional git commit.

## Session Rules

- Always update `README.md` (for humans) and `AGENTS.md` (for AI coding agents) to reflect the current state of the project

## Testing

This project uses **TUnit** (not xUnit/NUnit). Key differences:

- **Run tests**: `dotnet test --project <path-to-csproj>`
- **Filter by class**: use `--treenode-filter "/*/*/<ClassName>/*"` (NOT `--filter`)
- **Filter by test name**: use `--treenode-filter "/*/*/*/<TestMethodName>"`
- **List tests**: `dotnet test --project <path-to-csproj> --list-tests`
- The standard `--filter` flag does NOT work with TUnit; it silently runs zero tests.

- Use FakeLogger instead of NullLogger

## Code Style Preference

- Make classes `internal`. Only make them `public` when they need to be visible outside of the assembly.
- Prefer **local methods** over private static methods for helper functionality that is only used within a single method

## Architecture

### Arius.Core shape

- `src/Arius.Core/Features/` contains vertical slices (`*Command`, `*Query`) that orchestrate user-facing workflows.
- `src/Arius.Core/Shared/` contains reusable infrastructure and domain mechanisms that multiple features depend on.
- Keep orchestration in `Features` and storage/caching/serialization mechanics in `Shared`.
- Prefer injecting shared services into features instead of constructing them ad hoc inside handlers or helpers.

### Shared vs Features

- `Features` should decide **when** to resolve a snapshot, walk a tree, look up chunk metadata, upload chunks, or restore files.
- `Shared` should decide **how** snapshots are cached, how tree blobs are serialized/cached, how chunk-index shards are cached, and how blob names/content are interpreted.
- If logic is repository-wide and reused by more than one feature, it usually belongs in `Shared`.
- If logic is specific to one command/query flow, it usually belongs in that feature handler.

### Cache services

- `ChunkIndexService` owns the chunk-index cache.
- `ChunkStorageService` owns chunk blob upload/download and hydration/rehydration mechanics.
- `FileTreeService` owns the filetree blob cache.
- `SnapshotService` owns snapshot create/resolve/list behavior plus local snapshot disk state.

### Cache semantics

- Chunk-index shards are **mutable**. Multiple runs/machines can extend or overwrite shard content for the same prefix.
- Because chunk-index data is mutable, `ChunkIndexService` uses a tiered cache: L1 memory, L2 disk, L3 blob storage.
- On snapshot mismatch, chunk-index caches may be stale and must be invalidated.

- Filetree blobs are **immutable** and content-addressed.
- Because filetree blobs are immutable, `FileTreeService` can trust any non-corrupt local cache file permanently.
- Tree-cache validation is about remote existence knowledge and cross-machine coordination, not blob staleness.

- Snapshots are the coordination point between local cache state and remote repository state.
- Snapshot comparisons determine whether the current machine can trust its local tree/chunk cache view or must refresh remote knowledge.

### Service usage in features

- `ArchiveCommandHandler`
  Use `ChunkIndexService` for dedup/index recording and flush.
  Use `ChunkStorageService` for large, tar, and thin chunk blob operations.
  Use `FileTreeService` for tree existence checks and tree writes.
  Use `SnapshotService` for snapshot creation.
  Direct `IBlobContainerService` usage is an allowed exception for container creation.

- `RestoreCommandHandler`
  Use `SnapshotService` to resolve the target snapshot.
  Use `FileTreeService` to traverse filetrees.
  Use `ChunkIndexService` to resolve content hashes to chunk metadata.
  Use `ChunkStorageService` for chunk download, hydration status, rehydration start, and rehydrated cleanup planning.
  `restore` is a read-only repository operation and must not create blob containers.

- `ListQueryHandler`
  Use `SnapshotService` to resolve the snapshot.
  Use `FileTreeService` for tree traversal.
  Use `ChunkIndexService` for file size/original-size metadata.
  Do not keep stale direct blob/encryption dependencies when the handler no longer uses them.
  `ls` is a read-only repository operation and must not create blob containers.

- `ChunkHydrationStatusQueryHandler`
  Use `ChunkIndexService` to map file content hashes to chunk hashes.
  Use `ChunkStorageService` for hydration state resolution.

- Feature handlers and queries should not depend directly on `IBlobContainerService`, `IBlobService`, or `IBlobServiceFactory`.
  Current approved exceptions are `ArchiveCommandHandler` for container creation and `ContainerNamesQueryHandler` for repository-external container enumeration.

### DI expectations

- Register shared services once per repository/session in DI.
- Feature handlers should consume those shared instances through constructor injection.
- Helper types such as `FileTreeBuilder` should accept already-constructed shared services rather than creating fresh `ChunkIndexService`, `FileTreeService`, or `SnapshotService` instances internally.
- Avoid duplicate service graphs for the same repository because that can split cache state and validation state.
