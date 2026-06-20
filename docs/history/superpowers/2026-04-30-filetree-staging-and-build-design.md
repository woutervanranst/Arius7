# Filetree Staging And Build Design

## Context

The previous archive tail wrote a temporary manifest file, sorted it in memory, then built filetree blobs by loading all directory entries into memory. That did not scale to repositories with many files spread across many directories.

The staged filetree implementation fixed that whole-repository manifest scalability problem, but it also clarified three design boundaries that needed to be explicit in the final design:

- `FileTreeBuilder` should own canonical node construction and filetree hash calculation.
- `FileTreeService` should own immutable filetree persistence, cache coordination, and remote-existence knowledge.
- filetree calculation and filetree upload should overlap so upload latency does not sit on the recursive build critical path.

The target scale for this design is many files spread across many directories. A single directory containing millions of direct children remains bounded by the current filetree blob model and is accepted as an existing limitation.

This document is the authoritative design for the filetree staging and build model used by this PR. It supersedes the previous separate staging and decoupled build/upload design documents that this PR originally evolved through.

## Goals

- Remove the all-record in-memory manifest sort from the archive tail.
- Remove the all-directory-entry in-memory build from the old manifest-driven filetree build path.
- Replace manifest staging with filetree-shaped staging under the repository filetree cache.
- Move filetree hash calculation into `FileTreeBuilder`.
- Change `FileTreeService` to store a node when the caller already knows the `FileTreeHash`.
- Remove `FileTreeBlob` and work directly with `IReadOnlyList<FileTreeEntry>`.
- Reuse one serializer/parser surface for staged and persisted filetree lines.
- Allow filetree calculation to enqueue completed nodes for concurrent upload while parent nodes continue building.
- Keep persisted filetree bytes, filetree hashes, snapshot semantics, and empty-directory behavior unchanged.
- Keep archive durability: no snapshot is published until all referenced chunks, chunk-index data, and filetrees are durable.
- Design staging paths so they do not mirror source paths directly and therefore avoid unnecessary Windows path-length pressure.

## Non-Goals

- Do not represent empty directories in snapshots.
- Do not change the persisted line format for final filetree blobs.
- Do not add sharded filetree blobs for huge single-directory workloads.
- Do not remove per-directory materialization entirely; canonical sorting and hashing still require seeing the full node.
- Do not add special handling for directory-id collisions.
- Do not add a second whole-repository enumeration pass.
- Do not support concurrent archive runs for the same repository cache in one shared staging directory.

## Responsibilities

`ArchiveCommandHandler` remains the archive workflow orchestrator. It decides which files belong in the snapshot, stages filetree entries only after a file has been deduplicated or uploaded successfully, and preserves snapshot safety by waiting for chunk-index flush and filetree upload completion before creating the snapshot.

`FileTreeStagingSession` owns repository-local staging session lifecycle. It acquires the local staging lock, deletes stale `.staging` content from a previous crashed run when no lock is held, creates a fresh staging root, and cleans up best-effort on dispose.

`FileTreeStagingWriter` owns staged node-file writes. It computes deterministic staging directory ids, appends file-entry lines to the parent node file, appends staged directory-entry lines for parent-child edges, and serializes same-node appends with local striped locking.

`FileTreeBuilder` owns:

- reading staged node files
- validating staged duplicates and corruption
- resolving staged child directory ids into final child `FileTreeHash` values
- building the canonical sorted `IReadOnlyList<FileTreeEntry>` for each directory
- computing each directory `FileTreeHash`
- publishing completed node payloads to a bounded upload channel
- waiting for upload completion before returning the root hash

`FileTreeService` owns:

- validating local knowledge of remote filetrees
- checking whether a filetree already exists remotely or in the cache
- uploading a filetree when missing
- writing the local plaintext cache entry
- reading a cached or remote filetree back as `IReadOnlyList<FileTreeEntry>`

## Data Model

Persisted and staged filetree work uses `FileTreeEntry`-shaped records directly.

- `FileEntry : FileTreeEntry` remains the persisted and staged file record shape.
- `DirectoryEntry : FileTreeEntry` remains the persisted directory reference shape, identified by child `FileTreeHash`.
- `StagedDirectoryEntry : FileTreeEntry` is an internal staged-only subtype, identified by child staging directory id plus `Name`.
- `FileTreeBlob` is removed.

This keeps the type system explicit about the difference between a persisted directory reference and a staged directory reference without changing the persisted on-disk filetree format.

## Staging Location And Identity

Use one repository-scoped staging directory:

```text
~/.arius/{account}-{container}/filetrees/.staging/
```

There is no run-id directory. At archive start, the staging session acquires a repository-local staging lock and deletes any existing `.staging` directory before creating a fresh one. If another archive run already holds the staging lock for the same repository cache, the new run fails fast instead of deleting active staging data.

Every archive directory maps to a fixed-length staging directory id:

```text
dirId = SHA256(canonicalRelativeDirectoryPath)
```

Canonical relative directory path rules:

- root directory is the empty string `""`
- use `/` separators
- no leading slash
- no trailing slash
- ordinal, case-sensitive semantics matching existing filetree serialization

Each staged directory node becomes one file directly under the staging root:

```text
~/.arius/{account}-{container}/filetrees/.staging/{directory-id}
```

The root node file path is:

```text
~/.arius/{account}-{container}/filetrees/.staging/{SHA256("")}
```

No path metadata file is stored for collision detection. SHA-256 collision handling remains out of scope.

## Staging Format

Each staging file contains newline-delimited `FileTreeEntry`-shaped records in append order.

File lines are identical to the persisted file-entry line format:

```text
<content-hash> F <created:O> <modified:O> <leaf-file-name>
```

Staged directory lines keep the same overall grammar, but the leading hash-shaped field is a staged directory id rather than a child `FileTreeHash`:

```text
<child-directory-id> D <child-directory-name>/
```

This keeps staging and persistence on one line-oriented serializer surface while still making the staged directory type explicit.

For this source tree:

```text
readme.txt
photos/a.jpg
photos/2024/b.jpg
docs/manual.pdf
```

The staging graph contains four directory ids:

```text
root        = SHA256("")
photos      = SHA256("photos")
photos2024  = SHA256("photos/2024")
docs        = SHA256("docs")
```

Root node file contents:

```text
<hash-readme> F 2026-04-29T10:00:00.0000000+00:00 2026-04-29T10:00:00.0000000+00:00 readme.txt
<photos-dir-id> D photos/
<docs-dir-id> D docs/
```

`photos` node file contents:

```text
<hash-a> F 2026-04-29T10:00:00.0000000+00:00 2026-04-29T10:00:00.0000000+00:00 a.jpg
<photos-2024-dir-id> D 2024/
```

`photos/2024` node file contents:

```text
<hash-b> F 2026-04-29T10:00:00.0000000+00:00 2026-04-29T10:00:00.0000000+00:00 b.jpg
```

`docs` node file contents:

```text
<hash-manual> F 2026-04-29T10:00:00.0000000+00:00 2026-04-29T10:00:00.0000000+00:00 manual.pdf
```

## Duplicate Rules

When reading one staged directory node:

- duplicate file names are an error and must throw
- duplicate staged directory entries with the same `Name` and the same directory id collapse to one logical child edge
- duplicate staged directory entries with the same `Name` but a different directory id are corrupt staging and must throw

Duplicate staged directory lines are allowed during append-only staging because multiple archived files in the same subtree can race to ensure the same parent-child edge.

Empty directories continue to collapse away. If a child directory resolves to `null`, its staged directory reference is omitted from the parent's final node. If the root resolves to no file entries and no non-empty child directories, the archive root hash is `null`.

## Serializer Design

Use `FileTreeSerializer` as the single serializer/parser surface for staged and persisted filetree lines.

`FileTreeSerializer` operates on `IReadOnlyList<FileTreeEntry>` rather than `FileTreeBlob`.

Required responsibilities:

- serialize persisted filetree entries to canonical UTF-8 bytes
- deserialize persisted filetree entries from plaintext bytes
- parse one staged or persisted line into the correct `FileTreeEntry` subtype for that context
- provide shared file-entry line formatting and parsing
- provide persisted directory-entry line formatting and parsing
- support storage read and write paths that preserve the existing persisted bytes and hash semantics

The serializer surface keeps staged and persisted directory parsing explicit at the call site.

## Build And Upload Pipeline

`FileTreeBuilder.SynchronizeAsync` starts from the root staged directory id.

For one staged directory id:

1. Read the staged node file.
2. Parse file lines into `FileEntry` values.
3. Parse directory lines into `StagedDirectoryEntry` values.
4. Accumulate file entries by `Name`, throwing on duplicates.
5. Accumulate staged directory entries by `Name`, collapsing identical duplicates and throwing on conflicting ids.
6. Build child directories recursively.
7. Replace each non-empty `StagedDirectoryEntry` with a final `DirectoryEntry` containing the child `FileTreeHash`.
8. Combine file entries and final directory entries.
9. If the node is empty, return `null`.
10. Canonically serialize the final entries.
11. Compute the `FileTreeHash` from the canonical serialized bytes.
12. Enqueue the completed node payload for upload.
13. Return the `FileTreeHash` without waiting for the upload queue to drain.

`FileTreeBuilder` owns a bounded upload channel of completed node payloads. Upload workers consume that channel and ask `FileTreeService` to ensure the node is durably stored. The builder does not return from `SynchronizeAsync` until all upload workers have completed, so `ArchiveCommandHandler` only receives a root hash after all referenced filetrees are durably stored.

This decoupling allows subtree calculation and filetree upload to overlap while preserving snapshot durability.

## Concurrency

Staging writes are parallelized by node file. Archive workers can append to unrelated staged node files concurrently. Same-node appends are serialized locally by striped locks inside `FileTreeStagingWriter`.

Tree building is parallelized across independent child subtrees with bounded sibling parallelism so a directory with many children does not create unbounded tasks.

Upload workers run independently from subtree calculation. Duplicate uploads of identical filetree hashes are acceptable; `FileTreeService` remains responsible for idempotent content-addressed storage behavior and cache publication.

No additional global deduplication structure is required in the builder for correctness.

## API Shape

`FileTreeService` stores filetrees when the caller already knows the hash and provides reads as `IReadOnlyList<FileTreeEntry>`.

`FileTreeBuilder.SynchronizeAsync(...)` keeps the same public role, but internally operates as a producer-plus-upload-workers pipeline instead of a recursive upload call chain.

`FileTreePaths` exposes one staged node-file path helper:

```csharp
GetStagingNodePath(string stagingRoot, string directoryId)
```

## Error Handling And Cleanup

- malformed staged lines throw `FormatException`
- duplicate file names in one staged node throw `InvalidOperationException`
- conflicting staged directory ids for the same child name throw `InvalidOperationException`
- cancellation must stop staging writes, staged reads, recursive builds, channel writes, upload workers, and cleanup where possible
- upload failures must fault synchronization and prevent snapshot creation
- any failure while staging, building, uploading filetrees, flushing chunk-index data, or creating the snapshot causes archive failure

Cleanup of `.staging` is best-effort after failure because stale staging is safe to delete at the next run. Cleanup after success should normally remove the staging directory before returning.

## Testing And Verification

Unit coverage should verify:

- staging writer stores direct file entries under the expected staged node path
- staging writer stores parent-child links for nested paths
- staging session clears stale `.staging` at session start when no lock is held
- concurrent writes to different directories succeed and produce parseable staged entries
- `FileTreeSerializer` round-trips persisted entries without `FileTreeBlob`
- staged directory lines parse to `StagedDirectoryEntry`
- `FileTreeBuilder` throws on duplicate file names in one staged node
- `FileTreeBuilder` collapses identical duplicate staged directory entries
- `FileTreeBuilder` throws on conflicting staged directory ids for the same child name
- `FileTreeBuilder` produces the same root hash for the same logical repository regardless of staging append order
- `FileTreeBuilder` does not wait for each node upload before calculating unrelated parent or sibling nodes
- `FileTreeBuilder` does not return until all referenced filetree uploads have completed
- `FileTreeService` stores and reads `IReadOnlyList<FileTreeEntry>` nodes directly

Integration coverage should verify a complete archive still restores and lists the same repository structure, and a second unchanged archive reuses existing filetrees.

Relevant commands:

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeSerializerTests/*"
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeStagingWriterTests/*"
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderTests/*"
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeServiceTests/*"
dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderIntegrationTests/*"
```

## Consequences

Good:

- filetree staging scales with many files across many directories without a whole-repository in-memory manifest sort
- one staged node file format replaces the previous split staging layout
- one serializer surface handles staged and persisted filetree lines
- responsibility boundaries between staging, building, and immutable storage are clearer
- filetree upload latency no longer blocks all parent-node calculation

Tradeoffs:

- staged directory lines still require a staged-only subtype because the leading id is not yet a `FileTreeHash`
- each directory must still be fully materialized and sorted before hashing
- single-directory workloads with extremely high fan-out remain bounded by the persisted filetree node model
- builder completion logic is more complex because it manages upload workers and fault propagation explicitly
