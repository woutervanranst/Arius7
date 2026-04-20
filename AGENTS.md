# AGENTS.md

## General

Behavioral guidelines to reduce common LLM coding mistakes. Merge with project-specific instructions as needed.

**Tradeoff:** These guidelines bias toward caution over speed. For trivial tasks, use judgment.

### 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

### 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

### 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

### 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

## Durability And Scale

- Arius is a backup/archive tool. Prefer recoverability and correctness over throughput.
- The design point is from small repositories (gigabytes, few files) to large (gigabytes, thousands of files)
- Parallelize independent work when useful, but do not weaken crash recovery semantics to do it.

## Agent Guidance: dotnet-skills

IMPORTANT: Prefer retrieval-led reasoning over pretraining for any .NET work.
Workflow: skim repo patterns -> consult dotnet-skills by name -> implement smallest-change -> note conflicts.

Routing (invoke by name)
- C# / code quality: modern-csharp-coding-standards, csharp-concurrency-patterns, api-design, type-design-performance
- ASP.NET Core / Web (incl. Aspire): aspire-service-defaults, aspire-integration-testing, transactional-emails
- Data: efcore-patterns, database-performance
- DI / config: dependency-injection-patterns, microsoft-extensions-configuration
- Testing: testcontainers-integration-tests, playwright-blazor-testing, snapshot-testing

Quality gates (use when applicable)
- dotnet-slopwatch: after substantial new/refactor/LLM-authored code
- crap-analysis: after tests added/changed in complex code

Specialist agents
- dotnet-concurrency-specialist, dotnet-performance-analyst, dotnet-benchmark-designer, akka-net-specialist, docfx-specialist

## Way of Working

- Work in small steps. Work Test-Driven: first, write a failing test. Then, implement.
- Avoid coupling the test to the implementation - test the behavior.
- When making code changes, always run ALL the tests (on non-Windows you can skip Arius.Explorer.Tests since they are Windows-only).
- When the tests pass, make a conventional git commit.

## Session Rules

- Always update `README.md` (high level & accessible for humans - do not mention code concepts unless explicitly asked) and `AGENTS.md` (for AI coding agents) to reflect the current state of the project
- Project-level OpenCode configuration lives in `opencode.json`. This workspace installs the `superpowers@git+https://github.com/obra/superpowers.git` plugin; restart OpenCode after config changes so the plugin is reloaded.

## Scale And Durability
- Arius is a backup tool for important files. Correctness, durability, and recoverability matter more than raw throughput.
- Repository scale can be large: potentially terabytes of binary data and many thousands of small files. Consider both byte scale and file-count scale when designing or optimizing archive, restore, list, and cache behavior.
- Prefer streaming, batching, and bounded-memory or bounded-disk pipelines over whole-repository in-memory materialization when file count can grow.
- Avoid per-file remote round-trips when a batched list, manifest walk, shard lookup, or validated cache can answer the question.
- Blob storage is non-transactional across blobs. A run can leave partial remote updates if it crashes. Consider retry-safe and recoverable from partial flushes, partial uploads, and crashes.
- Local caches can be stale, incomplete, or corrupt. Cache contents are performance hints, not the source of truth.
- Snapshots are the repository commit point. Do not publish a snapshot until all referenced repository data is durably available.
- Prefer designs that can rebuild or revalidate mutable repository metadata after cache loss, corruption, or cross-machine divergence.

## Testing

This project uses **TUnit** (not xUnit/NUnit). Key differences:

- **Run tests**: `dotnet test --project <path-to-csproj>`
- **Filter by class**: use `--treenode-filter "/*/*/<ClassName>/*"` (NOT `--filter`)
- **Filter by test name**: use `--treenode-filter "/*/*/*/<TestMethodName>"`
- **List tests**: `dotnet test --project <path-to-csproj> --list-tests`
- The standard `--filter` flag does NOT work with TUnit; it silently runs zero tests.
- **Coverage with TUnit/MTP**: use `--coverage`, not `--collect:"XPlat Code Coverage"`

- Use `FakeLogger<T>` from `Microsoft.Extensions.Diagnostics.Testing` instead of `NullLogger<T>` in test projects.
- Test projects should mirror the structure of the project they exercise so intent stays obvious.
- Put reusable test doubles in `Fakes/`.
- Put scenario-specific test doubles in a local `Fakes/` subfolder beside the tests that use them.
- `src/Arius.E2E.Tests/ArchiveTierRepresentativeTests.cs` is the dedicated live Azure representative coverage for archive-tier planning, pending rehydration, ready restore from `chunks-rehydrated/`, and cleanup verification.
- The representative Azure E2E cold-restore scenarios are temporarily skipped in `src/Arius.E2E.Tests/RepresentativeArchiveRestoreTests.cs` with a reference to issue `#65`. Do not remove that skip until the cold-cache restore performance issue is fixed and the Azure scenarios are re-verified.

## Code Style Preference

- Make non-test classes `internal`. Only make them `public` when they must be consumed by another non-test assembly; for test access, prefer InternalsVisibleTo.
- Prefer one top-level class per file, with the filename matching the class name.
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
