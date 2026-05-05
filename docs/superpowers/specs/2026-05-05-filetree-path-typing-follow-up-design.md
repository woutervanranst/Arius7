# FileTree Path Typing Follow-Up Design

## Context

The path-helper work already typed broad filesystem boundaries across archive, restore, list, test infrastructure, copy helpers, and file enumeration. The remaining `FileTreePaths` area still mixes typed path values with raw string-based filesystem helpers and frequent `.ToString()` round-trips.

This follow-up removes that string plumbing from the filetree path helper boundary and updates production/test call sites to consume typed paths directly.

## Goals

- Make `FileTreePaths` filesystem helpers strongly typed in both arguments and return values.
- Update filetree production call sites to use typed paths directly.
- Update affected tests to use typed filetree paths directly.
- Keep deterministic non-filesystem identifiers distinct from filesystem path types.

## Non-Goals

- Do not introduce separate `FilePath` or `DirectoryPath` types.
- Do not change the persisted staging directory id format.
- Do not add a dedicated wrapper type for the hashed staging directory id unless implementation proves it is necessary.
- Do not refactor unrelated filetree serialization or storage behavior.

## Design

### Typed Helper Boundary

`FileTreePaths` will stop accepting raw path strings for filesystem helpers.

The helper surface will become:

- `GetCachePath(LocalRootPath fileTreeCacheDirectory, FileTreeHash hash) -> RootedPath`
- `GetCachePath(LocalRootPath fileTreeCacheDirectory, string hashText) -> RootedPath`
- `GetStagingNodePath(LocalRootPath stagingRoot, string directoryId) -> RootedPath`
- `GetStagingRootDirectory(LocalRootPath fileTreeCacheDirectory) -> LocalRootPath`
- `GetStagingLockPath(LocalRootPath fileTreeCacheDirectory) -> RootedPath`

`GetStagingDirectoryId(RelativePath directoryPath) -> string` remains string-based because it is a deterministic hashed identifier used as file content/name material, not a filesystem path.

### Call Site Changes

Production code should carry `LocalRootPath` / `RootedPath` through filetree operations instead of converting to strings and back.

Expected main updates:

- `FileTreeStagingSession`
  - parse the cache directory once into `LocalRootPath`
  - use typed lock path and typed staging root helpers
  - use typed directory create/delete/exists operations where available
- `FileTreeStagingWriter`
  - request typed staging node paths from `FileTreePaths`
  - append lines and compute lock stripes using `RootedPath` rather than raw path strings until the final host API boundary if needed
- `FileTreeBuilder`
  - read typed staging node paths directly
- `FileTreeService`
  - carry `_diskCacheDir` as `LocalRootPath`
  - use typed cache paths for read/write/existence checks

Tests should be updated to assert against typed paths directly and only cross into strings where the underlying BCL API still requires it.

## Boundary Rules

- Filesystem location inputs should be typed before entering `FileTreePaths`.
- Filesystem location outputs from `FileTreePaths` should stay typed until a true string-only boundary.
- Hash text and staging directory ids remain plain strings because they are not paths.
- Avoid convenience overloads that accept raw strings unless a call site is a true boundary that cannot reasonably be typed.

## Verification

Focused verification for this slice should cover:

- `FileTreeStagingWriterTests`
- `FileTreeBuilderTests`
- `FileTreeServiceTests`
- any additional filetree tests that fail from typed-boundary fallout

If the production slice touches broader filetree flows, rerun `Arius.Core.Tests` afterward.
