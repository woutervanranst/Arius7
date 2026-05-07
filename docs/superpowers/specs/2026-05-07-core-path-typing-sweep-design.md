# Core Path Typing Sweep Design

## Context

`Arius.Core` now has filesystem domain types, but several important cache, storage, and filetree paths still travel through Core as raw `string` values. The core path primitives are allowed to be public domain values, while archive-time and local-filesystem operational types remain internal. The current implementation is still in an inconsistent middle state:

- local cache roots such as chunk-index L2, filetree cache, and snapshot cache are sometimes typed and sometimes plain strings
- `IBlobContainerService` still accepts raw blob-name strings, so storage paths are not strongly typed at the boundary
- `FileTreePaths` still exposes string helpers in Core, unlike the newer `RepositoryPaths` plus `Arius.Tests.Shared/RepositoryCachePaths` split
- direct `File.*` and `Directory.*` calls in Core still frequently operate on raw strings that are really repository/cache paths

This design finishes that sweep inside `src/Arius.Core` and converts public command/query/result/event contracts to use `RelativePath` or `PathSegment` when those contracts carry Arius domain paths.

## Goals

- Make `Arius.Core` internally consistent about repository/cache/storage path usage.
- Replace path-like raw strings in Core with strongly typed path values where the path belongs to Arius domain logic.
- Treat `RelativePath` and `PathSegment` as public Arius domain primitives and actively convert eligible command/query/result/event contracts to use them.
- Remove Core service fields such as `_l2Dir`, `_diskCacheDir`, `_snapshotsDir`, and `_chunkIndexL2Dir` when they represent typed local roots.
- Use `RelativePath` for Arius blob names and logical blob-name prefixes throughout `IBlobContainerService` and Core callers.
- Keep strongly typed filetree path helpers in Core and move string convenience helpers needed only by tests to `Arius.Tests.Shared`.
- Bring `Arius.Core` local filesystem IO into line with the ADR by routing rooted Arius local filesystem operations through `RelativeFileSystem`.
- Keep archive-time and local-filesystem operational types internal: `BinaryFile`, `PointerFile`, `FilePair`, `LocalDirectory`, `RelativeFileSystem`, `LocalFileEntry`, and `LocalDirectoryEntry`.

## Non-Goals

- Do not leave genuinely Arius domain path or segment values stringly in public command/query/result/event contracts.
- Do not expose archive-time or local-filesystem operational types from public command/query/result/event contracts.
- Do not perform a repo-wide migration outside `src/Arius.Core`, except for the minimum `Arius.Core.Tests` and `Arius.Tests.Shared` support needed by this change.
- Do not introduce a large path taxonomy beyond what current misuse justifies.
- Do not wrap arbitrary OS temp paths or unrelated external strings that are not part of Arius.Core repository/cache/storage path semantics.
- Do not use broad `InternalsVisibleTo` expansion as the primary way to make stable domain primitives usable from tests.

## Scope

This refactor applies to `src/Arius.Core`.

The only non-Core updates allowed in this change are:

- `src/Arius.Core.Tests` updates required by the Core API changes
- `src/Arius.Tests.Shared` string helper moves that mirror the existing `RepositoryPaths` versus `RepositoryCachePaths` split

Integration tests, E2E tests, Explorer, CLI, AzureBlob, and other projects are out of scope for this pass.

## Design

### Public surface direction

The sweep follows ADR-0008's revised boundary:

- `RelativePath` is public.
- `PathSegment` is public.
- Pointer-path helpers remain internal unless an implementation step identifies a concrete public consumer.
- `BinaryFile`, `PointerFile`, `FilePair`, `LocalDirectory`, `RelativeFileSystem`, `LocalFileEntry`, `LocalDirectoryEntry`, and restore candidate models remain internal.

The purpose of making `RelativePath` and `PathSegment` public is to acknowledge that these are stable Arius domain primitives, comparable to the typed hash value objects, and to reduce conversion friction around a value that already coordinates archive, filetree, list, restore, blob-name, cache-path, and domain-adjacent public API logic.

Architecture tests must guard this boundary:

- public types in `Arius.Core.Shared.FileSystem` are allow-listed to `RelativePath` and `PathSegment` by default
- public command/query/result/event contracts should expose `RelativePath` and `PathSegment` when the value is genuinely an Arius domain path or path segment
- public command/query/result/event contracts must not expose archive-time or local-filesystem operational types
- archive-time and local-filesystem operational types must remain non-public

### Local filesystem paths

Core keeps using these filesystem types:

- public `RelativePath` for paths relative to a known root
- internal `LocalDirectory` for absolute local roots
- internal `RelativeFileSystem` as the concrete rooted filesystem boundary

Services that currently cache both a typed root and a string form must stop storing the redundant string form. They should instead store the typed root and route rooted local filesystem work through `RelativeFileSystem`.

This applies directly to:

- `ChunkIndexService`
- `FileTreeService`
- `SnapshotService`

When a service needs to enumerate, create, or delete entries under a cache root, the required shape is:

- keep the root as `LocalDirectory`
- use `RelativeFileSystem` for rooted local file operations under that root
- extend `RelativeFileSystem` when a needed Arius local filesystem operation is missing

Expected additions to `RelativeFileSystem` are small and concrete. Likely examples for this sweep include:

- enumerating file names under a relative directory
- deleting all files in a relative directory
- reading lines asynchronously from a relative file
- appending lines asynchronously to a relative file
- publishing a temp file atomically to a destination file within the rooted directory

### Blob storage paths

Use `RelativePath` for Arius blob names and logical blob-name prefixes inside `Arius.Core`.

This works because Arius blob names are already modeled as canonical slash-separated logical paths, and the current `BlobPaths` helpers already behave like strongly typed relative storage paths. Introducing a separate blob-name type would add ceremony without adding clear semantic separation for this pass.

Because `RelativePath` is public, storage abstractions can accept it without widening access to internal Core implementation details.

`IBlobContainerService` changes from `string` blob-name parameters to `RelativePath` for:

- `UploadAsync`
- `OpenWriteAsync`
- `DownloadAsync`
- `GetMetadataAsync`
- `ListAsync`
- `SetMetadataAsync`
- `SetTierAsync`
- `CopyAsync`
- `DeleteAsync`

`BlobPaths` becomes the authoritative builder for those `RelativePath` blob names and prefixes. Core callers stop calling `.ToString()` before invoking storage APIs.

Storage implementations convert `RelativePath` to raw backend strings only at the SDK boundary.

`ListAsync` should also return `IAsyncEnumerable<RelativePath>` rather than `IAsyncEnumerable<string>`. Blob listing inside Core should stay typed end-to-end; string conversion belongs only at the storage implementation boundary.

Services that currently expose string-based snapshot blob helpers may keep compatible public helpers when they are used by out-of-scope external test projects, but internal snapshot storage flow should still become `RelativePath`-based. If a helper has only `Arius.Core` or `Arius.Core.Tests` callers after the refactor, its visibility should be narrowed to `internal`.

Within `Arius.Core` public command/query/result/event contracts, compatibility is not the goal of this sweep. If a field or property is semantically an Arius relative path or path segment, it should be converted from `string` to `RelativePath` or `PathSegment` rather than left string-based for convenience.

### FileTree path helpers

`FileTreePaths` should match the newer repository-cache pattern:

- Core keeps strongly typed helpers only
- string-returning convenience helpers move to `Arius.Tests.Shared` when they exist only to make tests seed or inspect files directly

Inside Core, filetree cache and staging helpers should accept or return `LocalDirectory` and `RelativePath` values instead of raw strings wherever the path refers to typed local cache state. They should compose typed paths only; rooted filesystem IO belongs in `RelativeFileSystem`.

### Direct `File.*` and `Directory.*` usage in Core

This sweep should remove direct `File.*` and `Directory.*` calls in Core when those calls operate on Arius rooted local filesystem state.

In particular, the destination state for this refactor is:

- `RelativeFileSystem` is the place that performs rooted Arius local filesystem IO
- `RepositoryPaths` and `FileTreePaths` compose typed paths but do not perform IO
- services such as `ChunkIndexService`, `FileTreeService`, and `SnapshotService` stop calling `File.*` and `Directory.*` directly for rooted Arius cache operations

Examples:

- cache existence checks
- cache reads and writes
- cache marker creation
- deleting stale chunk-index shard files under the known L2 root

This does not ban all `System.IO` usage everywhere in Core. Path normalization and containment logic inside path value types such as `LocalDirectory` remain valid. The restriction applies to rooted Arius local filesystem IO, which should be centralized in `RelativeFileSystem` rather than left in service and helper classes.

## Responsibilities By Area

### `ChunkIndexService`

- Keep the L2 root as `LocalDirectory`.
- Remove `_l2Dir`.
- Use `RelativeFileSystem` plus typed relative shard paths for local shard reads, writes, and deletes.
- Keep storage shard names typed all the way through `IBlobContainerService`.

### `FileTreeService`

- Keep filetree cache root, snapshot cache root, and chunk-index cache root as typed local roots.
- Remove `_diskCacheDir`, `_snapshotsDir`, and `_chunkIndexL2Dir`.
- Use typed path composition helpers plus `RelativeFileSystem` for cache file reads, writes, marker files, and cleanup.
- Keep validation behavior unchanged: local snapshot comparison, remote listing, filetree marker materialization, and chunk-index invalidation remain the same.

### `SnapshotService`

- Remove redundant string cache-root storage.
- Use typed local roots and `RelativeFileSystem` for snapshot disk operations.
- Keep snapshot filename format and comparison semantics unchanged.

### Storage boundary

- `IBlobContainerService` accepts `RelativePath` blob names and prefixes rather than `string`.
- Concrete implementations convert to the raw string form only when calling the real backend SDK.
- Core code stops treating blob names as interchangeable with arbitrary strings.

## Compatibility

- Persisted on-disk file contents remain unchanged.
- Persisted blob names remain unchanged.
- Public command/query/result/event contracts should change from string to `RelativePath` or `PathSegment` when the value is genuinely an Arius domain path or path segment.
- Public path primitives become part of the Arius.Core API surface and should be kept stable.
- The change is a Core behavior and type-safety change, not a repository format change.

## Risks And Mitigations

- Risk: interface churn in storage abstractions touches many Core call sites.
  Mitigation: update `BlobPaths` first so callers convert mechanically from stringified blob names to `RelativePath` values.

- Risk: replacing string helpers with typed helpers makes tests awkward.
  Mitigation: make stable path primitives public, keep test-only string helpers in `Arius.Tests.Shared`, and keep operational filesystem/archive types internal.

- Risk: `RelativeFileSystem` may need a few additional operations to absorb existing service-level IO cleanly.
  Mitigation: add the smallest missing methods needed for Arius cache/snapshot/filetree operations rather than preserving service-level `System.IO` calls.

- Risk: operational filesystem/archive types drift into public command/query/result/event contracts.
  Mitigation: add architecture tests that allow-list public filesystem primitive types and prevent operational type exposure.

## Testing And Verification

Implementation should follow TDD and verify behavior through `Arius.Core.Tests`.

Required coverage areas:

- tests covering blob-path usage through `RelativePath` and `BlobPaths`
- architecture tests for the filesystem public surface allow-list
- architecture tests requiring eligible public feature DTOs to use `RelativePath` and `PathSegment` for filesystem-domain values, and allowing only those filesystem primitives in public contracts
- architecture tests keeping archive-time and local-filesystem operational types non-public
- updated tests for `FileTreePaths` typed helpers and any moved string helpers
- focused tests for `ChunkIndexService`, `FileTreeService`, and `SnapshotService` path behavior that would regress if stringly paths leak back in
- a targeted sweep of `src/Arius.Core` to ensure the named string fields are removed, storage calls use typed blob paths, and rooted Arius local filesystem IO no longer bypasses `RelativeFileSystem`

Verification commands after implementation:

- `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj`
- `slopwatch analyze`

## Approach Options Considered

### Recommended: targeted Core-only sweep

Refactor path-like strings in `Arius.Core`, make `RelativePath` and `PathSegment` public, convert eligible command/query/result/event contracts to use those primitives, change the storage interface to use `RelativePath` blob names, move test-only string helpers out of Core, and update only the affected tests and minimal shared test helpers.

This is the chosen approach because it fixes the current inconsistent boundary without turning the change into a repo-wide migration.

### Full repository migration

Convert all projects and tests to typed path values in one pass, including non-domain local filesystem strings and external/display-only path strings.

This was rejected because public `RelativePath` and `PathSegment` are domain primitives, not a mandate to convert every consumer-facing string. Local filesystem paths, external SDK strings, and display-only text should remain strings unless they gain a real Arius domain invariant.

### Local-only migration

Type only local filesystem paths and leave blob names as strings.

This was rejected because it preserves one of the most important stringly boundaries in Core: storage blob names flowing through the entire service layer.
