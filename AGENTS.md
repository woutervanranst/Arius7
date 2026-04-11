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

## Domain language

- **binary file**: a file on disk that Arius archives and restores.
- **pointer file**: a file on disk containing the content hash.
- **hash** Arius is a content addressed storage and deduplicates binary files based on content hash.
  - **content hash**: the hash of the (original) binary file's content
  - **chunk hash**: the name of the chunk in which the content is actually stored (identical for large chunks, different for tar chunks)
- **chunk**: representing unique binary content:
  - **large chunk**: a chunk whose blob body stores one file directly as gzip plus optional encryption.
  - **tar chunk**: a chunk whose blob body stores a tar bundle of multiple small files, then gzip plus optional encryption. Why: small files are prohibitively expensive to rehydrate in Azure Blob Storage, so we tar them together into a ~large chunk.
  - **thin chunk**: a small pointer-like chunk blob whose body is the hash of the tar chunk that actually contains the file bytes. Why: as deduplication existence check and metadata.
- **chunk index**: the repository-wide mapping from content hash to chunk hash. Why: 1/ TAR lookups 2/ efficient existence checks for deduplicated content and 3/ metadata store.
  - **shard**: one mutable chunk-index blob, partitioned by hash prefix for storage and caching.
- **filetree**: an immutable Merkle-tree blob describing one directory's entries. Filetrees model repository structure, not chunk storage.
- **snapshot**: an immutable point-in-time manifest that records the root filetree hash and repository totals.

- Prefer these terms consistently in code, tests, docs, and reviews. Avoid using generic words like "blob" or "pointer" when the more precise domain term is known.

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

### Shared: Storage

- `src/Arius.Core/Shared/Storage/` contains the low-level storage boundary: `IBlobContainerService`, `IBlobService`, `IBlobServiceFactory`, blob metadata models, tier enums, and preflight/storage exceptions.
- Those interfaces describe primitive storage capabilities such as upload, download, list, metadata, tier changes, and container lookup.
- Higher-level shared services build repository semantics on top of that storage boundary:
  - `ChunkIndexService` for deduplication index lookup, mutation, flushing, and cache invalidation
  - `ChunkStorageService` for chunk blob upload/download, hydration, rehydration, and cleanup planning
  - `FileTreeService` for filetree traversal, caching, and persistence
  - `SnapshotService` for snapshot resolution, creation, listing, and local snapshot state
- Prefer those higher-level shared services over direct `Shared/Storage` dependencies.

### Shared: Cache

- `ChunkIndexService` owns the chunk-index cache.
- `ChunkStorageService` owns chunk blob upload/download and hydration/rehydration mechanics.
- `FileTreeService` owns the filetree blob cache.
- `SnapshotService` owns snapshot create/resolve/list behavior plus local snapshot disk state.

- Chunk-index shards are **mutable**. Multiple runs/machines can extend or overwrite shard content for the same prefix.
- Because chunk-index data is mutable, `ChunkIndexService` uses a tiered cache: L1 memory, L2 disk, L3 blob storage.
- On snapshot mismatch, chunk-index caches may be stale and must be invalidated.

- Filetree blobs are **immutable** and content-addressed.
- Because filetree blobs are immutable, `FileTreeService` can trust any non-corrupt local cache file permanently.
- Tree-cache validation is about remote existence knowledge and cross-machine coordination, not blob staleness.

- Snapshots are the coordination point between local cache state and remote repository state.
- Snapshot comparisons determine whether the current machine can trust its local tree/chunk cache view or must refresh remote knowledge.

### Shared usage in features

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
