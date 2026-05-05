# Repo-Wide Path Typing Sweep Design

## Goal

Strongly type the remaining high-value path-like string concepts across the repository in one coordinated pass, using the existing path model (`RelativePath`, `LocalRootPath`, `RootedPath`, `PathSegment`) instead of raw strings at owning boundaries.

## Scope

This sweep covers the candidate groups the user explicitly selected:

- Best First
- High-Confidence Production Candidates
- Good Internal Infrastructure Candidates
- Test / Integration / Benchmark Candidates

This is one implementation effort, not separate follow-up slices.

## In Scope

### E2E, tests, and benchmarks

- `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryState.cs`
- `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryMaterializer.cs`
- `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryStateAssertions.cs`
- `src/Arius.E2E.Tests/Fixtures/E2EFixture.cs`
- `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowState.cs`
- `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowRunner.cs`
- `src/Arius.E2E.Tests/Workflows/Steps/Helpers.cs`
- `src/Arius.E2E.Tests/Workflows/Steps/MaterializeVersionStep.cs`
- `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs`
- `src/Arius.Benchmarks/ArchiveStepBenchmarks.cs`
- test helper tuples/dictionaries in the targeted Core and Integration test files where repository-relative strings are owned as path identity instead of raw boundary text

### High-confidence production internals

- `src/Arius.Core/Features/ArchiveCommand/Events.cs`
- `src/Arius.Core/Shared/LocalFile/LocalFileEnumerator.cs`
- `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`

### Internal infrastructure roots

- `src/Arius.Core/Shared/FileTree/FileTreeStagingSession.cs`
- `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`
- `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`

## Out of Scope

The following remain string boundaries in this sweep:

- CLI argument/input text
- persisted settings models
- synthetic dataset declaration text
- logs, console output, display labels, and UI-facing progress text
- storage/blob names and other persisted wire-format strings

The sweep also does not introduce brand-new path types unless an existing one clearly cannot model the concept.

## Design Rules

### Use existing path types only

- repository-relative paths -> `RelativePath`
- local root directories -> `LocalRootPath`
- rooted local filesystem paths -> `RootedPath`
- path segments -> `PathSegment`

### Type the owning boundary, not just the construction site

If a dictionary, record, state bag, or helper signature owns path identity, that API should become typed. Do not leave the owner stringly and only wrap its internal `new Dictionary<string, ...>` construction.

### Keep text only at true boundaries

Path strings may still appear when:

- parsing CLI or dataset input
- serializing or deserializing persisted formats
- rendering logs, error messages, UI, or progress output
- joining with APIs that inherently require host-path text at the final OS boundary

## Targeted Type Changes

### E2E synthetic repository flow

- `SyntheticRepositoryState.RootPath` -> `LocalRootPath`
- `SyntheticRepositoryState.Files` -> `IReadOnlyDictionary<RelativePath, ContentHash>`
- `SyntheticRepositoryMaterializer` internal file maps -> `Dictionary<RelativePath, ContentHash>`
- `SyntheticRepositoryStateAssertions.AssertMatchesDiskTreeAsync(..., string rootPath, ...)` -> typed root input and typed actual-path map
- `E2EFixture.LocalRoot` / `RestoreRoot` -> `LocalRootPath`
- `RepresentativeWorkflowState.VersionedSourceRoot` -> `LocalRootPath`
- workflow helper records like `ArchiveTierTargetChunk.TargetRelativePath` -> `RelativePath`

### Production internal flow

- `ArchiveCommand` in-process events carrying repository-relative file paths use `RelativePath`
- `LocalFileEnumerator` helper surfaces stop mixing local full paths and repository-relative paths as raw strings
- `ListQueryHandler` equivalent helper surfaces use `RootedPath` and `RelativePath` at the right ownership points

### Filetree staging infrastructure

- `FileTreeStagingSession` local staging/cache roots -> `LocalRootPath`
- `FileTreeStagingWriter` staging root -> `LocalRootPath`
- `FileTreeBuilder` staging root parameters/state -> `LocalRootPath`

### Tests and benchmarks

- benchmark fixture/source roots -> `LocalRootPath`
- test tuples and dictionaries that own repository-relative path identity -> `RelativePath`
- avoid changing textual fixture declarations that are intentionally raw string input

## Expected Benefits

- fewer reparses of path strings already known to be typed values
- fewer ad hoc `Path.Combine(...)`, `Path.GetRelativePath(...)`, and slash-normalization helpers
- clearer distinction between local filesystem roots, rooted paths, and repository-relative paths
- lower risk of mixing OS paths with repository-relative paths in internal code

## Risks And Mitigations

### Risk: broad compile fallout

This sweep changes several internal contracts at once.

Mitigation:

- prefer the smallest direct signature changes
- update downstream call sites immediately in the same pass
- avoid opportunistic cleanup outside the selected candidate set

### Risk: event/progress contract confusion

Some production events are consumed by CLI code that ultimately renders text.

Mitigation:

- type the Core-owned event contract where the event semantically carries repository-relative path identity
- keep CLI/UI rendering surfaces string-based and convert at that final presentation boundary

### Risk: infrastructure path helper mismatch

Filetree staging helpers may still call host APIs that require strings.

Mitigation:

- type only the owned roots and convert to `string` at the final OS call boundary
- do not invent new abstractions if typed roots plus `RootedPath` are sufficient

## Testing Strategy

Use TDD where contract or behavior fallout is involved.

1. Add or update focused tests around the first affected owning boundary in each area before implementation.
2. Verify the new/updated test fails for the expected reason.
3. Implement the minimal typed-path change for that area.
4. Re-run the focused tests.
5. After all areas are updated, run broader affected suites sequentially.

Sequential verification matters here because this repo has already shown MSBuild file-lock issues under parallel `dotnet test` runs.

## Verification Targets

At minimum, expect focused and/or broader runs covering:

- `Arius.Core.Tests`
- `Arius.Integration.Tests`
- `Arius.E2E.Tests`
- relevant benchmark compile/build verification if touched

If a touched area has a narrow focused test target, prefer that first, then escalate only as needed.

## Non-Goals

- changing CLI boundary parsing from strings
- changing persisted settings or dataset declaration models to typed paths
- redesigning public-facing display/progress payloads that are intentionally string-rendered
- introducing new path domain types beyond the current model
