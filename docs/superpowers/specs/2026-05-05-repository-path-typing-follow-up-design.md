# RepositoryPaths Typing Follow-Up Design

## Context

`RepositoryPaths` is the shared helper that defines Arius' on-disk repository layout under the user's `.arius` home. It currently returns raw strings for repository, chunk-index, filetree, snapshot, and logs directories. That leaves string path plumbing spread across chunk-index, snapshots, filetree, CLI logging, fixtures, and tests.

The recent `FileTreePaths` follow-up established the preferred direction for filesystem helpers: typed arguments and typed return values, with string conversion only at true host-library boundaries.

## Goals

- Make every filesystem-returning `RepositoryPaths` helper return `LocalRootPath`.
- Update production and test callers to carry `LocalRootPath` until real string-only boundaries.
- Remove `GetRepoDirectoryName(...)` from the public surface.
- Keep the current on-disk layout and repository directory naming behavior unchanged.

## Non-Goals

- Do not introduce a new repository identity value object for `accountName` + `containerName`.
- Do not change the naming scheme for repository directories.
- Do not refactor unrelated storage, logging, or snapshot behavior beyond typed-path fallout.

## Design

### Public Surface

`RepositoryPaths` will expose only typed directory helpers:

- `GetRepositoryDirectory(string accountName, string containerName) -> LocalRootPath`
- `GetChunkIndexCacheDirectory(string accountName, string containerName) -> LocalRootPath`
- `GetFileTreeCacheDirectory(string accountName, string containerName) -> LocalRootPath`
- `GetSnapshotCacheDirectory(string accountName, string containerName) -> LocalRootPath`
- `GetLogsDirectory(string accountName, string containerName) -> LocalRootPath`

`GetRepoDirectoryName(...)` will be removed from the public surface. If a tiny internal helper still improves readability inside `RepositoryPaths`, it can remain private. Its behavior stays covered indirectly through repository-directory path assertions.

### Caller Rules

Production and test callers should keep repository paths typed for as long as possible.

Examples:

- `Directory.CreateDirectory(...)` should become `localRoot.CreateDirectory()` when the target is a directory root.
- filetree/chunk-index/snapshot services should store typed repository roots rather than raw strings when the field represents a directory.
- CLI logging should keep the logs directory typed, then cross to string only when constructing the final log file path text for the Serilog sink.
- tests should assert `LocalRootPath` equality directly where possible and only convert to string when asserting against APIs that still expose strings.

### Boundary Rules

- Repository directory names are observable but not first-class path values; they do not need their own public helper.
- Directory-returning helpers should not offer string convenience overloads unless a real boundary cannot reasonably be typed.
- Use `LocalRootPath` for directory roots; do not broaden this slice into typed file paths returned from `RepositoryPaths` because it only models directory locations.

## Expected Caller Areas

This follow-up is expected to touch at least these areas:

- `ChunkIndexService`
- `SnapshotService`
- `FileTreeService`
- `ArchiveCommandHandler`
- `CliBuilder`
- repository and pipeline fixtures/tests that consume repository cache directories

The exact set should stay limited to direct `RepositoryPaths` callers or immediate typed fallout.

## Verification

Focused verification should cover:

- `RepositoryPathsTests`
- chunk-index tests that exercise L2 cache directory behavior
- filetree/snapshot tests that consume repository cache roots
- CLI tests if logging-directory typing reaches CLI-visible behavior

If multiple shared services move from string roots to `LocalRootPath`, run a broader `Arius.Core.Tests` pass after the focused suites.
