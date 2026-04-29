# Stryker Coverage Improvement Design

## Context

`Arius.Core` now runs under Stryker.NET using the preview Microsoft Testing Platform runner. The current mutation score is 75.33%, with surviving, timeout, no-coverage, and compile-error mutants reported across multiple files.

The next change should be a small, focused mutation-testing improvement pass rather than a broad test rewrite. Existing dedicated test files already cover likely target areas such as `LocalFileEnumerator`, `SnapshotService`, and `ChunkStorageService`. Those areas are better candidates for a first pass than broader archive orchestration code because they are easier to isolate, reason about, and verify.

## Goals

- Improve `Arius.Core` mutation coverage with a deliberately small first pass.
- Target one or two existing unit-test areas with the clearest surviving mutants.
- Add behavior-focused tests rather than refactoring production code unless testability requires a minimal change.
- Verify progress with targeted unit tests and a fresh Stryker rerun.
- Commit regularly during implementation, ideally once each focused batch of tests is green.

## Non-Goals

- Do not attempt a repository-wide mutation score push in one change.
- Do not broaden the first pass into integration-heavy archive or restore orchestration unless a surviving mutant clearly forces it.
- Do not chase compile-error mutants as the first priority; prefer survivors and meaningful no-coverage gaps first.
- Do not introduce thresholds or CI gates as part of this change.

## Design

Use the existing Stryker report as the selector for where to work next, but keep the implementation scope narrow. The preferred first-pass targets are `LocalFileEnumerator` and `ChunkStorageService` because both already have focused unit test files and are more likely to yield clean, behavior-level mutant kills without broad setup costs.

The implementation should inspect the surviving mutants in those areas, identify the missing asserted behavior, and add the smallest tests that make those behaviors explicit. If one area turns out to be dominated by compile-error or low-value mutants, skip to the next likely target instead of forcing coverage improvements in unproductive code.

Commits should happen regularly during implementation. A good unit of work is one focused mutant-killing batch: add the failing test, make it pass, run the relevant unit tests, and commit before moving to the next batch.

## Testing And Verification

Verification for this change is:

- The newly added targeted unit tests pass.
- The relevant existing test project still passes.
- A fresh `dotnet stryker --config-file stryker-config.json` run completes successfully.
- The rerun shows improvement in the targeted areas, even if the overall score change is modest.

## Future Work

After this first focused pass, consider:

- A second pass for `SnapshotService` if it still shows meaningful survivors.
- Targeted work on timeouts if they represent real untested behavior rather than low-value mutation artifacts.
- Thresholds or CI enforcement only after mutation score trends and runtime are understood.
