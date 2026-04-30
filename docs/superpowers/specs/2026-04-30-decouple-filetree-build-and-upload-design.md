# Decouple Filetree Build And Upload Design

## Context

The current staged filetree implementation fixed the old manifest scalability problem, but it still leaves three design issues in the hot path:

- `FileTreeService.EnsureStoredAsync` calculates the filetree hash and persists the node, which mixes Merkle-node construction with immutable storage.
- staging uses two files per directory node (`entries` and `directories`) and a different intermediate shape than the final persisted filetree node, so the builder maintains separate parsing and merge paths.
- `FileTreeBuilder` calculates a directory node and immediately awaits `FileTreeService.EnsureStoredAsync`, which keeps filetree upload latency on the recursive build critical path.

This design keeps the staged filetree approach, but sharpens the boundaries so the builder owns canonical node construction and hashing, the service owns immutable persistence and cache coordination, and node calculation can overlap with upload.

## Goals

- Move filetree hash calculation into `FileTreeBuilder`.
- Change `FileTreeService` to store a node when the caller already knows the `FileTreeHash`.
- Remove `FileTreeBlob` and work directly with `IReadOnlyList<FileTreeEntry>`.
- Replace the two-file staging node layout with one staging file per directory id.
- Reuse one serializer/parser surface for staged and persisted filetree lines.
- Allow filetree calculation to enqueue completed nodes for concurrent upload while parent nodes continue building.
- Keep persisted filetree bytes, hashes, snapshot semantics, and empty-directory behavior unchanged.

## Non-Goals

- Do not represent empty directories in snapshots.
- Do not change the persisted line format for final filetree blobs.
- Do not remove per-directory materialization entirely; canonical sorting and hashing still require seeing the full node.
- Do not add special handling for directory-id collisions.
- Do not add a second whole-repository enumeration pass.

## Approved Decisions

### Responsibilities

`FileTreeBuilder` owns:

- reading staged node files
- validating staged duplicates and corruption
- resolving staged child directory ids into final child `FileTreeHash` values
- building the canonical sorted `IReadOnlyList<FileTreeEntry>` for each directory
- computing each directory `FileTreeHash`
- publishing completed nodes to an upload channel

`FileTreeService` owns:

- validating local knowledge of remote filetrees
- checking whether a filetree already exists remotely or in the cache
- uploading a filetree when missing
- writing the local plaintext cache entry
- reading a cached or remote filetree back as `IReadOnlyList<FileTreeEntry>`

`ArchiveCommandHandler` continues to own archive orchestration and snapshot safety. It must still wait for filetree upload completion before creating the snapshot.

### Data Model

Persisted and staged filetree work will use `FileTreeEntry`-shaped records directly.

- Keep `FileEntry : FileTreeEntry`.
- Keep `DirectoryEntry : FileTreeEntry` for persisted filetree nodes.
- Introduce `StagedDirectoryEntry : FileTreeEntry` as an internal staged-only subtype containing `DirectoryId` plus `Name`.
- Delete `FileTreeBlob`.

This keeps the type system explicit about the subtle but important difference between:

- a persisted directory reference identified by child `FileTreeHash`, and
- a staged directory reference identified by child staging `DirectoryId`.

### Staging Layout

Each staged directory node becomes one file directly under the staging root:

```text
~/.arius/{account}-{container}/filetrees/.staging/{directory-id}
```

There is no per-node directory and no `entries` filename. The root node file path is:

```text
~/.arius/{account}-{container}/filetrees/.staging/{SHA256("")}
```

The existing staging lock and stale-staging cleanup behavior remain unchanged.

### Staging Format

Each staging file contains newline-delimited `FileTreeEntry`-shaped records in append order.

File lines are already identical to the persisted format:

```text
<content-hash> F <created:O> <modified:O> <leaf-file-name>
```

Staged directory lines keep the same overall grammar, but the leading hash-shaped field is a staged directory id:

```text
<child-directory-id> D <child-directory-name>/
```

This means staging and persistence share the same line structure and the same sort key (`Name`), while still keeping separate parsed entry types.

### Duplicate Rules

When reading one staged directory node:

- duplicate file names are an error and must throw
- duplicate staged directory entries with the same `Name` and the same `DirectoryId` collapse to one logical child edge
- duplicate staged directory entries with the same `Name` but a different `DirectoryId` are corrupt staging and must throw

Empty directories continue to collapse away. If a child directory resolves to `null`, its staged directory reference is omitted from the parent's final node.

## Serializer Design

Rename `FileTreeBlobSerializer` to `FileTreeSerializer`.

`FileTreeSerializer` should operate on `IReadOnlyList<FileTreeEntry>` rather than `FileTreeBlob`.

Required responsibilities:

- serialize persisted filetree entries to canonical UTF-8 bytes
- serialize persisted filetree entries to storage bytes (gzip plus optional encryption)
- deserialize persisted filetree entries from plaintext bytes
- deserialize persisted filetree entries from storage streams
- parse one staged or persisted line into the right `FileTreeEntry` subtype for that context
- compute a `FileTreeHash` from canonical persisted bytes

The serializer should expose separate entry parsing for the two directory contexts so call sites stay explicit:

- persisted read: `ParsePersistedEntryLine(...)`
- staged read: `ParseStagedEntryLine(...)`

File-entry line parsing and formatting should remain shared.

## Build And Upload Pipeline

### Builder Input

`FileTreeBuilder.SynchronizeAsync` still starts from the root staged directory id.

For each staged node file, the builder reads lines asynchronously, for example through `File.ReadLinesAsync(...)` or an equivalent `IAsyncEnumerable<string>` helper. Async streaming is useful for I/O, but each directory node is still materialized into dictionaries before hash calculation because canonical ordering requires the full node.

### Per-Directory Build Algorithm

For one staged directory id:

1. Read staged lines as an async stream.
2. Parse file lines into `FileEntry` values.
3. Parse directory lines into `StagedDirectoryEntry` values.
4. Accumulate file entries by `Name`, throwing on duplicates.
5. Accumulate staged directory entries by `Name`, collapsing identical duplicates and throwing on conflicting ids.
6. Build child directories recursively.
7. Replace each non-empty `StagedDirectoryEntry` with a final `DirectoryEntry` containing the child `FileTreeHash`.
8. Combine file entries and final directory entries.
9. If the node is empty, return `null`.
10. Sort once by `FileTreeEntry.Name` using ordinal comparison.
11. Compute the `FileTreeHash` from the canonical serialized bytes.
12. Enqueue `(hash, entries)` to the upload channel.
13. Return the `FileTreeHash` immediately without waiting for the upload channel to drain.

### Upload Workers

`FileTreeBuilder` owns a bounded channel of completed nodes:

```text
Channel<(FileTreeHash Hash, IReadOnlyList<FileTreeEntry> Entries)>
```

Upload workers read from the channel and call:

```csharp
FileTreeService.EnsureStoredAsync(FileTreeHash hash, IReadOnlyList<FileTreeEntry> entries, CancellationToken cancellationToken)
```

The builder must wait for all upload workers before returning from `SynchronizeAsync`, so `ArchiveCommandHandler` still receives a root hash only after all referenced filetrees are durably stored.

This decoupling allows subtree calculation and filetree upload to overlap, while preserving snapshot durability.

### Concurrency

- sibling subtree calculation can remain bounded and parallel
- upload workers can run independently from subtree calculation
- duplicate uploads of identical filetree hashes are acceptable; `FileTreeService` remains responsible for idempotent storage behavior and race-safe cache writes

No additional global deduplication structure is required in the builder for correctness.

## API Changes

### `FileTreeService`

Replace:

```csharp
Task<FileTreeHash> EnsureStoredAsync(FileTreeBlob tree, CancellationToken cancellationToken = default)
Task WriteAsync(FileTreeHash hash, FileTreeBlob tree, CancellationToken cancellationToken = default)
Task<FileTreeBlob> ReadAsync(FileTreeHash hash, CancellationToken cancellationToken = default)
```

With:

```csharp
Task<FileTreeHash> EnsureStoredAsync(FileTreeHash hash, IReadOnlyList<FileTreeEntry> entries, CancellationToken cancellationToken = default)
Task WriteAsync(FileTreeHash hash, IReadOnlyList<FileTreeEntry> entries, CancellationToken cancellationToken = default)
Task<IReadOnlyList<FileTreeEntry>> ReadAsync(FileTreeHash hash, CancellationToken cancellationToken = default)
```

`EnsureStoredAsync` no longer computes the hash; it returns the provided `hash` after ensuring durability.

### `FileTreeBuilder`

`SynchronizeAsync` keeps the same public shape, but internally becomes a producer-plus-upload-workers pipeline rather than a recursive upload call chain.

### `FileTreeStagingPaths`

Remove node-directory helpers and expose one node-file path helper:

```csharp
GetNodePath(string stagingRoot, string directoryId)
```

## Error Handling

- malformed staged file lines throw `FormatException`
- duplicate file names in one directory throw `InvalidOperationException`
- conflicting staged directory ids for the same child name throw `InvalidOperationException`
- cancellation must stop staged reads, recursive builds, channel writes, and upload workers
- upload failures must fault the synchronization operation and prevent snapshot creation

## Testing And Verification

Unit coverage should verify:

- `FileTreeSerializer` round-trips persisted entries without `FileTreeBlob`
- staged directory lines parse to `StagedDirectoryEntry`
- `FileTreeStagingWriter` writes one flat staging node file per directory id
- `FileTreeBuilder` throws on duplicate file names in one staged node
- `FileTreeBuilder` collapses identical duplicate staged directory entries
- `FileTreeBuilder` throws on conflicting staged directory ids for the same child name
- `FileTreeBuilder` still produces the same root hash for the same logical repository regardless of staging append order
- `FileTreeBuilder` does not wait for each node upload before calculating unrelated parent or sibling nodes
- `FileTreeService` stores and reads `IReadOnlyList<FileTreeEntry>` nodes directly

Integration coverage should verify the archive pipeline still produces correct snapshots, and unchanged archives still reuse existing filetrees.

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

- cleaner responsibility split between build and storage
- one staged node file format instead of separate file and directory staging files
- one serializer surface for staged and persisted line handling
- filetree upload latency no longer blocks all parent-node calculation

Tradeoff:

- staged directory lines still require a staged-only subtype because the leading id is not yet a `FileTreeHash`
- each directory must still be fully materialized and sorted before hashing
- builder completion logic becomes slightly more complex because it now manages upload workers and fault propagation explicitly

This design supersedes the 2026-04-29 staged filetree design where it described `FileTreeBlob`, `entries` plus `directories` staging files, and `FileTreeService` hash calculation.
