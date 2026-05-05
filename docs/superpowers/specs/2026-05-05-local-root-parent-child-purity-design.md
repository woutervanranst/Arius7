# LocalRootPath Parent/Child Purity Design

## Context

The typed-path follow-up still has a small set of impure root-path patterns where code reconstructs a `LocalRootPath` by converting through strings:

- `Path.GetDirectoryName(root.ToString())`
- `LocalRootPath.Parse(Path.Combine(root.ToString(), "child"))`
- `RootOf(rootedPath.FullPath)` when the value is already a typed local root logically

These patterns show up in representative workflow code and tests. They work, but they weaken the typed-path model and conflict with the repo preference to move shared boundaries to strong types instead of adding feature-local conversion helpers.

## Goals

- Add first-class parent/child root operations to `LocalRootPath`.
- Replace nearby string round-trips with typed root operations.
- Keep the change small and limited to immediate production, E2E, and test fallout.
- Use the same purity rules to define the next remaining scope slice.

## Non-Goals

- Do not introduce `FilePath` or `DirectoryPath` types.
- Do not add implicit conversions between typed paths and strings.
- Do not broaden this slice into Explorer/config/CLI cleanup beyond documenting the remaining scope.

## Design

### LocalRootPath API

`LocalRootPath` gains two capabilities:

- `Parent : LocalRootPath?`
- `static LocalRootPath operator /(LocalRootPath left, PathSegment right)`

This keeps root-to-root composition at the `LocalRootPath` boundary while preserving the existing `LocalRootPath / RelativePath -> RootedPath` operator for arbitrary rooted entries.

### Usage Rules

- Use `root / PathSegment.Parse("child")` when the result is still a directory root.
- Use `root / relativePath` when the result is a rooted filesystem entry.
- Use `root.Parent` instead of `Path.GetDirectoryName(root.ToString())`.
- Do not rebuild a typed root through `RootOf(rootedPath.FullPath)` or `LocalRootPath.Parse(Path.Combine(root.ToString(), ...))` when the same result can be expressed by typed APIs.

### Expected Cleanup Areas

This slice is intentionally narrow:

- `src/Arius.Core/Shared/Paths/LocalRootPath.cs`
- `src/Arius.Core/Shared/RepositoryPaths.cs`
- `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowRunner.cs`
- `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs`
- `src/Arius.E2E.Tests/Workflows/Steps/MaterializeVersionStep.cs`
- `src/Arius.Integration.Tests/Pipeline/RoundtripTests.cs`
- `src/Arius.Core.Tests/Shared/LocalRootPathTests.cs`
- `src/Arius.Core.Tests/Shared/RepositoryPathsTests.cs`

Only immediate compile or behavior fallout in directly related files should be included.

## Verification

Focused verification should cover:

- `LocalRootPathTests`
- `RepositoryPathsTests`
- `RoundtripTests`
- `Arius.E2E.Tests` build

Then run the broader project suites already used for the path-typing follow-up.

## Remaining Slice 3 Scope

After this purity slice, the next remaining typed-path cleanup should cover:

- CLI/update/install temp-root composition still built from raw strings
- config/settings/viewmodel code that carries local-root values as strings deeper than the true persistence or UI boundary
- remaining test or fixture callers that rebuild typed roots from `root.ToString()` instead of using typed root APIs
