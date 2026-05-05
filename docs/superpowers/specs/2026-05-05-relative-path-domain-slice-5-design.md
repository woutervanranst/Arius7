# Relative Path Domain Slice 5 Design

## Context

The original relative-path domain design reserved the final slice for tests, fixtures, and E2E dataset helpers. After slices 1 through 4, the production path model is largely in place:

- `PathSegment`
- `RelativePath`
- `LocalRootPath`
- `RootedPath`
- typed filetree entry names
- bare-segment filetree persistence

What remains is mostly test and test-adjacent infrastructure. Much of that code still carries semantic repository paths and local roots as raw strings, then reparses or normalizes them repeatedly.

Examples still present today:

- `RepositoryTestFixture.WriteFile(string relativePath, ...)`
- `RepositoryTestFixture.ReadRestored(string relativePath)`
- `RepositoryTestFixture.RestoredExists(string relativePath)`
- helper-local `Path.Combine(root, relative.Replace('/', ...))`
- E2E materializer code that keeps dataset paths as strings until the final OS join
- test-local helpers that trim `/` or parse `PathSegment` / `RelativePath` ad hoc instead of accepting typed values at the owning boundary

That causes three problems:

- typed path semantics stop at test infrastructure boundaries and then restart repeatedly downstream
- test helpers keep doing parsing/normalization work that should already be settled by the time a path reaches them
- string signatures obscure whether a value is repository-relative identity, a local root, or a rooted local address

Slice 5 should finish the path-domain migration by broadly retyping shared test, fixture, and E2E helper APIs where the values have real path semantics.

## Goals

- Replace string-based repository-path helper APIs in tests and fixtures with typed `RelativePath` / `PathSegment` boundaries.
- Replace string-based local-root helper APIs in tests and fixtures with typed `LocalRootPath` / `RootedPath` boundaries where those helpers own local-path semantics.
- Reduce repeated `RelativePath.Parse(...)`, `PathSegment.Parse(...)`, slash replacement, and slash trimming in shared test infrastructure.
- Keep persisted dataset definitions and other intentionally textual test inputs as strings only until they cross into a semantic path boundary.
- Preserve safe local join and root-containment behavior at the local-path boundary.

## Non-Goals

- Do not broaden into unrelated production feature refactors.
- Do not redesign production CLI or Explorer boundaries.
- Do not force every raw string in every test to become typed if the value is genuinely test text rather than path state.
- Do not invent additional path abstractions beyond the existing `PathSegment`, `RelativePath`, `LocalRootPath`, and `RootedPath` model.

## Decision

Slice 5 should broadly retype shared test and E2E helper APIs at their owning boundaries.

The key rule is:

- if a helper or fixture owns a semantic path boundary, its API should become typed
- if a helper is only a pure OS/file API boundary, it may remain string-based

This means the slice is not merely an internal cleanup pass. It is an API cleanup pass for test infrastructure.

## Architecture

### 1. Shared Test Fixtures

`RepositoryTestFixture` currently owns real repository and local-root semantics, so its path-bearing APIs should become typed.

Expected direction:

- methods that conceptually take repository-relative paths should accept `RelativePath`
- fixture state that is conceptually a local root should be exposed through `LocalRootPath`
- file writes and reads should use rooted composition instead of repeated string validation and slash replacement

Representative examples:

- `WriteFile(RelativePath relativePath, byte[] content)`
- `ReadRestored(RelativePath relativePath)`
- `RestoredExists(RelativePath relativePath)`

If string overloads remain temporarily, they should be explicit compatibility shims and not the preferred API.

### 2. E2E Dataset Materialization

Dataset definitions may remain textual if that is the natural persisted/test-data representation. But once a dataset path is being materialized into a repository tree, it should become typed.

For `SyntheticRepositoryMaterializer` and similar helpers:

- dataset file paths should parse to `RelativePath` at the materializer boundary
- root directories should be modeled as `LocalRootPath`
- materialization and mutation steps should use rooted composition to reach OS paths

Representative operations that should become typed internally:

- create file content under a repository-relative path
- rename one repository-relative path to another
- compute hashes for repository-relative paths under a typed local root

### 3. Shared Test IO Helpers

`FileSystemHelper` should only be retyped if it truly owns local-root semantics. If it is just a general-purpose OS copy helper, it can stay string-based.

The deciding question is ownership:

- if it is just copying one OS directory tree to another, string OS-path inputs are fine
- if it starts interpreting repository-relative paths or enforcing root semantics, it should be typed

For this slice, the design prefers keeping such helpers narrow and only retyping them when they actually own path semantics.

### 4. Test Factories And Helper Methods

Small helper methods in tests should stop hiding path semantics behind string parameters when they already know the values are path concepts.

Examples:

- `DirectoryEntryOf(string name, ...)` should stop trimming `/` and either accept `PathSegment` directly or accept only canonical text without hidden normalization
- helper-local `RelativePath.Parse(...)` / `PathSegment.Parse(...)` calls should move outward to the helper boundary where feasible

The goal is not to eliminate all parsing in tests. The goal is to stop repeatedly reparsing the same semantic value inside helper internals.

## Scope

### Included

- shared test fixtures in `src/Arius.Tests.Shared/`
- shared test path helpers in `src/Arius.Tests.Shared/Paths/`
- E2E dataset/materializer helpers in `src/Arius.E2E.Tests/Datasets/`
- test-local helper methods whose main job is path normalization/parsing for path-semantic values
- broad test call-site cleanup that naturally follows from retyping those helper APIs

### Included Call-Site Fallout

- Core tests
- integration tests
- E2E tests
- benchmark/test-adjacent setup code if it consumes the retyped helper APIs

### Excluded

- unrelated production path refactors outside test-infrastructure ownership
- reworking every test assertion that already receives typed values correctly
- changing persisted dataset file formats unless a specific dataset helper owns that boundary and the change is justified there

## File Guidance

Likely primary files:

- `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`
- `src/Arius.Tests.Shared/Paths/PathsHelper.cs`
- `src/Arius.Tests.Shared/IO/FileSystemHelper.cs`
- `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryMaterializer.cs`

Likely affected tests/callers:

- Core tests using `RepositoryTestFixture`
- integration tests using fixture-owned source/restore helpers
- E2E dataset/materializer call sites
- local helper methods like `DirectoryEntryOf(...)`, `FileEntryOf(...)`, and dataset/path setup helpers that still normalize strings internally

## Boundary Rules

### Repository-relative values

Use `RelativePath` once a value means “a path inside the Arius repository.”

Keep plain strings only when the value is still truly textual, such as:

- inline dataset declaration text
- test-case arguments meant to exercise parsing itself
- persisted fixture text/wire formats under test

### Local roots and rooted local paths

Use `LocalRootPath` for owned root boundaries and `RootedPath` for local filesystem addresses under a known root.

Keep strings only at true OS/file boundaries such as:

- `File.*`, `Directory.*`, and path APIs that still require full path strings
- generic filesystem helpers that do not own repository semantics

### Test helper ergonomics

Test-only construction helpers such as:

- `PathOf(...)`
- `RootOf(...)`
- `SegmentOf(...)`

remain appropriate at textual test-input boundaries, but slice 5 should prefer typed helper signatures after that point rather than repeatedly calling them inside lower-level helper implementations.

## Migration Shape

Slice 5 should proceed in broad but explicit steps:

1. retype shared fixture/helper method signatures that semantically carry repository-relative paths or local roots
2. update their internal implementations to use rooted composition and typed path operations
3. clean up broad test call sites to pass typed values directly
4. remove now-obsolete helper-local normalization/parsing logic that existed only to compensate for string APIs

## Success Criteria

This slice is complete when:

- shared test and E2E helper APIs accept typed paths where the values have real path semantics
- repeated parsing and slash normalization in shared test infrastructure is significantly reduced
- repository-relative values remain typed through fixture and materializer flows
- local-root values remain typed until true OS boundaries
- remaining raw strings in tests are clearly textual boundary values, not hidden domain state

## Risks

- churn can be high because shared test helpers have many callers
- some helpers may look generic but actually hide repository semantics, requiring careful ownership decisions
- if the slice is not scoped carefully, it can turn into a vague “test cleanup” refactor instead of a path-boundary migration

## Out Of Scope Follow-Up

If any production helper still owns a real path boundary after slice 5, that should be handled as a separate production-focused change rather than folded into this test-infrastructure slice.
