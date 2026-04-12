<!-- https://dev.to/webdeveloperhyper/how-to-make-ai-follow-your-instructions-more-for-free-openspec-2c85 -->

# AGENTS.md

## Way of Working

- Work in small steps. Work Test-Driven: first, write a failing test. Then, implement.
- Avoid coupling the test to the implementation - test the behavior.
- When making code changes, always run ALL the tests (on OSX you can skip Arius.Explorer.Tests since they are Windows-only).
- When the tests pass, make a conventional git commit.

## Session Rules

- Always update `README.md` (for humans) and `AGENTS.md` (for AI coding agents) to reflect the current state of the project
- The bundled `ast-grep` skill under `.agents/skills/ast-grep/` is C#-first. Use `--lang csharp`, prefer real declaration context with `pattern.context` and `selector`, and do not document generic multi-language examples there.

## Testing

This project uses **TUnit** (not xUnit/NUnit). Key differences:

- **Run tests**: `dotnet test --project <path-to-csproj>`
- **Filter by class**: use `--treenode-filter "/*/*/<ClassName>/*"` (NOT `--filter`)
- **Filter by test name**: use `--treenode-filter "/*/*/*/<TestMethodName>"`
- **List tests**: `dotnet test --project <path-to-csproj> --list-tests`
- The standard `--filter` flag does NOT work with TUnit; it silently runs zero tests.

- Use `FakeLogger<T>` from `Microsoft.Extensions.Diagnostics.Testing` instead of `NullLogger<T>` in test projects.
- Test projects should mirror the structure of the project they exercise so intent stays obvious.
- Prefer one top-level test class per file, with the filename matching the class name.
- If a test file contains multiple classes, keep them together only when they share one tight theme or when the extra types are local test support.
- Place tests in folders that mirror the production folders they target. Keep cross-feature scenario tests in an explicit scenario folder such as `Pipeline/`.
- Put reusable test doubles in `Fakes/`. Put scenario-specific test doubles in a separate subfolder `Fakes/` rather than duplicating them.
- In `src/Arius.Cli.Tests`, put verb-specific tests under `Commands/<Verb>/` when they primarily exercise archive/restore/ls behavior, even if they touch shared CLI state such as `ProgressState`.
- For truly top-level production files such as `src/Arius.Cli/DisplayHelpers.cs` or `src/Arius.Cli/CliBuilder.cs`, keep the matching tests at the test-project root rather than inventing extra folders.
- When refactoring test layout mechanically, prefer same-project folder moves first. Only move tests between `*.Tests` projects when the primary production target clearly lives in another assembly, such as DI registration tests for `src/Arius.Core/ServiceCollectionExtensions.cs`.

## Code Style Preference

- Make non-test classes `internal`. Only make them `public` when they must be consumed by another non-test assembly; for test access, prefer InternalsVisibleTo.
- Prefer **local methods** over private static methods for helper functionality that is only used within a single method

## Domain language

- **binary file**: a file on disk that Arius archives and restores.
- **pointer file**: a file on disk containing the content hash.
- **FilePair**: the local archive-time view of one path, combining the binary file and its optional pointer file. A `FilePair` can be binary-only, pointer-only, or have both present.
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

### Feature-specific exceptions and constraints

- Feature handlers and queries should not depend directly on `IBlobContainerService`, `IBlobService`, or `IBlobServiceFactory`.
- Current approved exceptions are `ArchiveCommandHandler` for container creation and `ContainerNamesQueryHandler` for repository-external container enumeration.
- `restore` is a read-only repository operation and must not create blob containers.
- `ls` is a read-only repository operation and must not create blob containers.
- Remove stale direct blob/encryption dependencies from feature handlers when the handler no longer uses them.

### DI expectations

- Register shared services once per repository/session in DI.
- Feature handlers should consume those shared instances through constructor injection.
- Helper types such as `FileTreeBuilder` should accept already-constructed shared services rather than creating fresh `ChunkIndexService`, `FileTreeService`, or `SnapshotService` instances internally.
- Avoid duplicate service graphs for the same repository because that can split cache state and validation state.
