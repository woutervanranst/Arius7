# RelativePath Overload Removal Design

## Goal

Remove `string`-based APIs that represent Arius repository-relative paths when a strong `RelativePath` contract already exists or should exist.

The result should leave repository-relative path handling typed throughout test helpers, test fixtures, and related support code, while preserving legitimate raw-string boundaries such as account names, container names, passphrases, command names, serializer text, and host filesystem roots.

## Scope

In scope:

- Delete duplicate `string` overloads where an equivalent `RelativePath` overload already exists for the same semantic input.
- Convert string-only helper APIs to `RelativePath` when the parameter semantically means an Arius repository-relative path.
- Update all callers to pass `RelativePath` directly instead of strings.
- Remove now-obsolete string helper wrappers such as `RepositoryPathStrings` where direct typed APIs are available.

Out of scope:

- Changing APIs whose strings are not repository-relative paths.
- Reworking host filesystem boundary types such as temp roots or other absolute/local OS paths unless they only need an internal helper adjustment to accept `RelativePath` for the repository-relative portion.
- Broad refactors unrelated to repository-relative path typing.

## Candidate Approaches

### 1. Repository-relative only

Convert only APIs that semantically accept Arius repository-relative paths, and delete matching duplicate string overloads.

Pros:

- Matches the domain guidance already in the repo.
- Keeps churn focused and explainable.
- Avoids breaking legitimate raw-string boundaries.

Cons:

- Leaves some string-based path handling in place where the value is truly a host path.

### 2. Aggressive path typing sweep

Convert both repository-relative paths and nearby host-path helpers in one pass.

Pros:

- Maximizes consistency quickly.

Cons:

- Much higher churn.
- Greater risk of forcing Arius-specific value objects across real foreign boundaries.

### 3. Keep adapters temporarily

Convert callers but retain string overloads as forwarding shims.

Pros:

- Lowest immediate breakage risk.

Cons:

- Leaves the duplicate API surface the user wants removed.
- Extends mixed typing unnecessarily.

## Chosen Approach

Approach 1.

The change should be strict about Arius repository-relative paths and conservative everywhere else.

## Target API Categories

### Duplicate overloads to remove

- `src/Arius.Tests.Shared/Storage/FakeInMemoryBlobContainerService.cs`
  - `ThrowAlreadyExistsOnOpenWrite(string, ...)`
  - `SeedLargeBlobAsync(string, ...)`
  - `SeedTarBlobAsync(string, ...)`
- `src/Arius.Core.Tests/Fakes/FakeSeededBlobContainerService.cs`
  - `AddBlob(string, ...)`
- `src/Arius.Core/Shared/Snapshot/SnapshotService.cs`
  - `ParseTimestamp(string)`

These all describe Arius blob names or snapshot paths and already have, or should rely on, typed `RelativePath` APIs.

### String-only repository-relative helpers to convert

- `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`
  - `WriteFile`
  - `ReadRestored`
  - `RestoredExists`
  - internal root-combining helper for repository-relative paths
- `src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs`
  - `WriteFile`
  - `WriteRandomFile`
  - `ReadRestored`
  - `RestoredExists`
- `src/Arius.E2E.Tests/Fixtures/E2EFixture.cs`
  - `WriteFile`
  - `ReadRestored`
  - `RestoredExists`
  - internal root-combining helper for repository-relative paths
- `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveTestEnvironment.cs`
  - `WriteRandomFile`
- `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryMaterializer.cs`
  - helpers that accept a repository-relative path under a separate host root
- `src/Arius.E2E.Tests/Workflows/Steps/Helpers.cs`
  - helpers that resolve repository-relative files under `LocalRoot`
- `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs`
  - subtree selectors that are semantically repository-relative prefixes

### String helpers to remove entirely

- `src/Arius.Tests.Shared/RepositoryPathStrings.cs`

Callers should use typed `RepositoryPaths` results directly and convert to host strings only at the final filesystem boundary.

## Design Details

### API shape

- Public or internal helper APIs that mean "repository-relative path" should accept `RelativePath`.
- Helpers that combine a host root with a repository-relative path should accept `(string root, RelativePath relativePath)`.
- Internal storage in fakes may remain keyed by canonical string form where appropriate, but only as an implementation detail behind typed APIs.

### Boundary rule

Use `RelativePath` until a real foreign boundary is reached.

Examples of valid final conversions to `string`:

- Passing a resolved host path to `File.*`, `Directory.*`, or `Path.*`
- Looking up a dictionary keyed by canonical blob-name text inside a fake
- Producing serialized text or user-facing output

Examples that should remain typed:

- Blob names
- Snapshot blob paths
- Repository subtree selectors
- Fixture-local source/restore relative paths

### Call-site migration

- Update tests and fixtures to construct `RelativePath` at the call site rather than relying on implicit string convenience.
- Prefer existing helpers like `BlobPaths.*Path(...)` and `RelativePath.Parse(...)`.
- Keep changes minimal: do not introduce new abstractions unless a local helper is needed to keep tests readable.

## Error Handling

- Continue failing fast through `RelativePath.Parse(...)` or existing validation when callers provide invalid repository-relative paths.
- Do not add compatibility overloads or fallback parsing paths.

## Testing Strategy

Follow TDD for each changed API group:

1. Update or add tests so the typed API usage is exercised first.
2. Run the relevant tests and observe failures caused by removed or converted string APIs.
3. Implement the minimal production and test-helper changes to make the tests pass.

Expected verification scope:

- `src/Arius.Core.Tests/Arius.Core.Tests.csproj`
- `src/Arius.Integration.Tests/Arius.Integration.Tests.csproj` if fixture signature changes affect integration tests
- `src/Arius.E2E.Tests/Arius.E2E.Tests.csproj` only if touched code requires it and runtime cost is justified

## Success Criteria

- No remaining duplicate `string` and `RelativePath` overload pairs for repository-relative path semantics in the targeted areas.
- Repository-relative helper APIs in scope accept `RelativePath` instead of `string`.
- `RepositoryPathStrings` is removed and callers use typed `RepositoryPaths` directly.
- Relevant tests pass after the migration.

## Risks And Mitigations

- Risk: accidentally converting a legitimate raw-string boundary.
  - Mitigation: only convert APIs whose parameter semantics are repository-relative, not host-path or arbitrary-text values.
- Risk: broad test churn from fixture signature changes.
  - Mitigation: keep migration mechanical and local, using existing `RelativePath.Parse(...)` calls where needed.
- Risk: E2E-related churn becomes too large for this branch.
  - Mitigation: stop if a candidate API turns out to be a real host boundary rather than a repository-relative one.
