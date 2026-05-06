# Core Path Typing Sweep Design

## Context

`Arius.Core` now has internal filesystem domain types, but several important cache, storage, and filetree paths still travel through Core as raw `string` values. That leaves the code in an inconsistent middle state:

- local cache roots such as chunk-index L2, filetree cache, and snapshot cache are sometimes typed and sometimes plain strings
- `IBlobContainerService` still accepts raw blob-name strings, so storage paths are not strongly typed at the boundary
- `FileTreePaths` still exposes string helpers in Core, unlike the newer `RepositoryPaths` plus `Arius.Tests.Shared/RepositoryCachePaths` split
- direct `File.*` and `Directory.*` calls in Core still frequently operate on raw strings that are really repository/cache paths

This design finishes that sweep inside `src/Arius.Core` without turning the whole repository into a public typed-path migration.

## Goals

- Make `Arius.Core` internally consistent about repository/cache/storage path usage.
- Replace path-like raw strings in Core with internal strongly typed path values where the path belongs to Arius domain logic.
- Remove Core service fields such as `_l2Dir`, `_diskCacheDir`, `_snapshotsDir`, and `_chunkIndexL2Dir` when they represent typed local roots.
- Use `RelativePath` for Arius blob names and logical blob-name prefixes throughout `IBlobContainerService` and Core callers.
- Keep strongly typed filetree path helpers in Core and move string convenience helpers needed only by tests to `Arius.Tests.Shared`.
- Bring `Arius.Core` local filesystem IO into line with the ADR by routing rooted Arius local filesystem operations through `RelativeFileSystem`.

## Non-Goals

- Do not change public command/query/result/event contracts.
- Do not perform a repo-wide migration outside `src/Arius.Core`, except for the minimum `Arius.Core.Tests` and `Arius.Tests.Shared` support needed by this change.
- Do not introduce a large path taxonomy beyond what current misuse justifies.
- Do not wrap arbitrary OS temp paths or unrelated external strings that are not part of Arius.Core repository/cache/storage path semantics.

## Scope

This refactor applies to `src/Arius.Core`.

The only non-Core updates allowed in this change are:

- `src/Arius.Core.Tests` updates required by the Core API changes
- `src/Arius.Tests.Shared` string helper moves that mirror the existing `RepositoryPaths` versus `RepositoryCachePaths` split

Integration tests, E2E tests, Explorer, CLI, AzureBlob, and other projects are out of scope for this pass.

## Design

### Local filesystem paths

Core keeps using the existing internal filesystem types:

- `LocalDirectory` for absolute local roots
- `RelativePath` for paths relative to a known root
- `RelativeFileSystem` as the concrete rooted filesystem boundary

Services that currently cache both a typed root and a string form must stop storing the redundant string form. They should instead store the typed root and route rooted local filesystem work through `RelativeFileSystem`.

This applies directly to:

- `ChunkIndexService`
- `FileTreeService`
- `SnapshotService`

When a service needs to enumerate, create, or delete entries under a cache root, the required shape is:

- keep the root as `LocalDirectory`
- use `RelativeFileSystem` for rooted local file operations under that root
- extend `RelativeFileSystem` when a needed Arius local filesystem operation is missing

### Blob storage paths

Use `RelativePath` for Arius blob names and logical blob-name prefixes inside `Arius.Core`.

This works because Arius blob names are already modeled as canonical slash-separated logical paths, and the current `BlobPaths` helpers already behave like strongly typed relative storage paths. Introducing a separate blob-name type would add ceremony without adding clear semantic separation for this pass.

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
- Public contracts remain unchanged.
- The change is internal to Core behavior and type safety, not repository format.

## Risks And Mitigations

- Risk: interface churn in storage abstractions touches many Core call sites.
  Mitigation: update `BlobPaths` first so callers convert mechanically from stringified blob names to `RelativePath` values.

- Risk: replacing string helpers with typed helpers makes tests awkward.
  Mitigation: keep test-only string helpers in `Arius.Tests.Shared`, not in Core.

- Risk: `RelativeFileSystem` may need a few additional operations to absorb existing service-level IO cleanly.
  Mitigation: add the smallest missing methods needed for Arius cache/snapshot/filetree operations rather than preserving service-level `System.IO` calls.

## Testing And Verification

Implementation should follow TDD and verify behavior through `Arius.Core.Tests`.

Required coverage areas:

- tests covering blob-path usage through `RelativePath` and `BlobPaths`
- updated tests for `FileTreePaths` typed helpers and any moved string helpers
- focused tests for `ChunkIndexService`, `FileTreeService`, and `SnapshotService` path behavior that would regress if stringly paths leak back in
- a targeted sweep of `src/Arius.Core` to ensure the named string fields are removed, storage calls use typed blob paths, and rooted Arius local filesystem IO no longer bypasses `RelativeFileSystem`

Verification commands after implementation:

- `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj`
- `slopwatch analyze`

## Approach Options Considered

### Recommended: targeted Core-only sweep

Refactor path-like strings in `Arius.Core`, change the storage interface to use `RelativePath` blob names, move test-only string helpers out of Core, and update only `Arius.Core.Tests` plus minimal shared test helpers.

This is the chosen approach because it fixes the current inconsistent boundary without turning the change into a repo-wide migration.

### Full repository migration

Convert all projects and tests to typed path values in one pass.

This was rejected because the filesystem types are internal, so a repository-wide sweep would add churn without architectural value for this step.

### Local-only migration

Type only local filesystem paths and leave blob names as strings.

This was rejected because it preserves one of the most important stringly boundaries in Core: storage blob names flowing through the entire service layer.
