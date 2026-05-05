# Typed Filesystem Extension Members Design

## Goal

Add a typed local-filesystem API on top of the existing Arius path model using C# 14 extension members, so production and test code can perform common file and directory operations without dropping back to raw `string` paths at every call site.

## Scope

This design covers:

- typed filesystem extension members for `RootedPath`, `LocalRootPath`, and `PathSegment`
- async-only typed copy operations for single files and whole directories
- migration of high-value production, shared-test, benchmark, integration, and E2E call sites to the new typed API

This is one coordinated sweep, not a parallel experiment with multiple competing filesystem abstractions.

## In Scope

### Typed filesystem surface

- `src/Arius.Core/Shared/Paths/RootedPath.cs`
- `src/Arius.Core/Shared/Paths/LocalRootPath.cs`
- new extension-member files under `src/Arius.Core/Shared/Paths/`
- potential project-level language-version enablement if required by the installed .NET 10 SDK

### Typed recursive copy helper

- `src/Arius.Tests.Shared/IO/FileSystemHelper.cs`

### Production migration targets

- `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`
- `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`
- `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs`
- `src/Arius.Core/Shared/LocalFile/LocalFileEnumerator.cs`
- directly affected tests in `src/Arius.Core.Tests/` and `src/Arius.Cli.Tests/`

### Shared test, integration, E2E, and benchmark migration targets

- `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`
- `src/Arius.Benchmarks/ArchiveStepBenchmarks.cs`
- relevant files in `src/Arius.Integration.Tests/`
- relevant files in `src/Arius.E2E.Tests/`

## Out of Scope

- introducing distinct `FilePath` and `DirectoryPath` types
- changing `RelativePath`, `LocalRootPath`, or `RootedPath` from pure path/domain value objects into IO-owning types
- changing true string boundaries such as CLI arguments, logs, UI text, blob names, or persisted settings payloads
- adding synchronous typed copy APIs alongside async ones

## Design Rules

### Keep the existing path algebra

The Arius path model remains:

- repository-relative paths -> `RelativePath`
- local root directories -> `LocalRootPath`
- rooted local filesystem paths -> `RootedPath`
- path segments -> `PathSegment`

The design adds typed filesystem behavior on top of these types. It does not replace them.

### Keep path types pure

`RelativePath`, `LocalRootPath`, `RootedPath`, and `PathSegment` remain value/domain types. The new filesystem behavior is expressed as extension members, not as instance members on the value objects themselves.

### Prefer explicit file-vs-directory semantics where the receiver is ambiguous

`RootedPath` intentionally does not encode whether a path points to a file or directory. For that reason:

- use explicit names such as `ExistsFile` and `ExistsDirectory`
- do not add a generic `Exists` member on `RootedPath`
- do not add `Delete` members that silently guess file vs directory semantics

### Allow generic entry metadata when file and directory semantics both make sense

Some metadata applies naturally to both files and directories.

For `RootedPath`, the new typed API may expose read/write properties such as:

- `CreationTimeUtc`
- `LastWriteTimeUtc`

These should resolve against the existing filesystem entry at the target path. If no entry exists, they should fail fast instead of guessing.

### Prefer native async operations when the platform provides them

When the BCL has a native async API for the requested filesystem operation, use it.

When it does not, expose an async Arius API built from async stream operations rather than inventing fake async wrappers over synchronous bulk work.

This rule is especially important for copy operations.

## Target API Shape

### RootedPath extension members

The typed filesystem surface on `RootedPath` should include:

- `ExistsFile`
- `ExistsDirectory`
- `Extension`
- `Length`
- `CreationTimeUtc { get; set; }`
- `LastWriteTimeUtc { get; set; }`
- `OpenRead()`
- `OpenWrite()`
- `ReadAllText()`
- `ReadAllTextAsync(CancellationToken = default)`
- `ReadAllBytesAsync(CancellationToken = default)`
- `WriteAllTextAsync(string content, CancellationToken = default)`
- `DeleteFile()`
- `CreateDirectory()`
- `DeleteDirectory(bool recursive = false)`
- `CopyToAsync(RootedPath destination, bool overwrite = false, CancellationToken cancellationToken = default)`

The design does not add a synchronous `CopyTo`.

### LocalRootPath extension members

The typed root-directory surface should include:

- `ExistsDirectory`
- `CreateDirectory()`
- `DeleteDirectory(bool recursive = false)`

### PathSegment extension members

Pure path-derived metadata may live on `PathSegment` via extension members, specifically:

- `Extension`

This remains string-derived path metadata rather than filesystem IO behavior.

## Typed Recursive Copy

`FileSystemHelper.CopyDirectory` should be retyped and made async-only:

- `CopyDirectoryAsync(LocalRootPath sourceRoot, LocalRootPath targetRoot, CancellationToken cancellationToken = default)`

This helper should:

- delete the target directory if it already exists
- create the target directory
- recreate the source directory tree beneath the target
- copy files asynchronously
- preserve file creation and last-write timestamps

The design does not keep a synchronous `CopyDirectory` twin.

## Why Not FilePath And DirectoryPath

The Arius codebase usually learns file-vs-directory intent from either:

- the surrounding domain model such as `FilePair`, restore targets, or list-directory state
- the actual IO operation being performed

The underlying path values are intentionally generic in many places, especially prefixes and rooted local paths assembled before the final operation is chosen.

Introducing `FilePath` and `DirectoryPath` now would create large conversion churn without removing much real ambiguity from the current domain model.

## Expected Benefits

- fewer `.FullPath` and `.ToString()` escapes just to call `File.*` or `Directory.*`
- clearer file and directory semantics at call sites
- smaller blast radius for future path-typing work because filesystem behavior moves behind a typed boundary
- async copy semantics available for both file-to-file and directory-tree copy flows

## Risks And Mitigations

### Risk: C# 14 extension-member support needs explicit enablement

Mitigation:

- verify the installed .NET 10 SDK and compiler accept the required syntax in a focused test/build step first
- if the repo needs explicit language-version configuration, make that a narrow, deliberate change

### Risk: ambiguous metadata behavior on non-existent paths

Mitigation:

- only expose generic metadata properties where the behavior is well-defined for existing entries
- throw when metadata properties target a path that does not currently exist

### Risk: async-only copy forces signature churn

Mitigation:

- migrate current sync call sites in one coherent sweep
- prefer promoting existing helper methods and test steps to `async` instead of introducing sync wrappers

### Risk: timestamp behavior varies across platforms

Mitigation:

- preserve the project’s current Linux caveat around `CreationTimeUtc`
- keep tests capability-aware where the filesystem does not guarantee birth time support

## Testing Strategy

Use TDD in small steps.

1. Add a focused failing test for the first extension-member surface.
2. Verify the test fails for the expected reason.
3. Implement the minimal extension-member support.
4. Re-run the focused tests.
5. Expand to async copy and the caller migrations in focused slices.
6. Run broader affected suites sequentially at the end.

## Verification Targets

At minimum, expect focused and broader verification covering:

- `Arius.Core.Tests`
- `Arius.Integration.Tests`
- `Arius.E2E.Tests`
- `Arius.Cli.Tests` if production fallout touches CLI-adjacent code
- benchmark build verification if the benchmark project is touched

Run verification sequentially to avoid the repo’s known parallel MSBuild file-lock problems.

## Non-Goals

- broad redesign of restore/list/archive semantics beyond the typed filesystem boundary
- replacing all raw `System.IO` usage everywhere in one pass if a site is a true outer boundary
- adding duplicate sync and async copy APIs
