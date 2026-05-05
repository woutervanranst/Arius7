# Slice 3 Typed Path Cleanup Design

## Context

The earlier typed-path work completed the main production path model and a follow-up purity slice for `LocalRootPath.Parent` plus `LocalRootPath / PathSegment`. The remaining string-path cleanup is no longer one homogeneous task. It falls into three distinct boundary types with different risk profiles:

- shared fixture temp-root APIs
- Explorer/settings persisted and UI-facing repository paths
- residual test-only callers that still set up typed roots through raw strings

Trying to clean all of these in one undifferentiated change would mix infrastructure boundaries, persistence/UI boundaries, and test-only setup code. That increases ambiguity and verification cost.

## Goals

- Define one umbrella slice that explicitly decomposes the remaining work into `3A`, `3B`, and `3C`.
- Keep each sub-slice coherent around one path-boundary rule.
- Prefer typed-path APIs where the code models local roots internally.
- Keep string values only at true boundaries such as persisted settings, dialog text, and host OS/file-system entry points.

## Non-Goals

- Do not eliminate all string paths from the codebase.
- Do not replace legitimate `FullPath` assertions in tests that intentionally verify host-path rendering.
- Do not redesign Explorer UX or settings storage format beyond typed-boundary cleanup.

## Decomposition

### 3A: Fixture Temp-Root Typing

This slice covers shared test infrastructure that still accepts and forwards temp roots as raw strings.

Primary files:

- `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`
- `src/Arius.E2E.Tests/Fixtures/E2EFixture.cs`
- `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowRunner.cs`

Design rules:

- Use `LocalRootPath? tempRoot` instead of `string? tempRoot` in fixture factory APIs.
- Use a typed delete callback such as `Action<LocalRootPath>? deleteTempRoot`.
- Keep temp-root creation and child-root composition typed with `LocalRootPath` plus `PathSegment`.
- Convert to raw strings only at the actual BCL boundary that performs deletion or external interop.

Why separate:

- This is infrastructure shared by E2E and integration tests.
- It has low ambiguity and high leverage.
- It is the safest first execution slice under the umbrella.

### 3B: Explorer/Settings Typed Boundary

This slice covers persisted and UI-facing repository paths in Arius Explorer.

Primary files:

- `src/Arius.Explorer/Settings/RepositoryOptions.cs`
- `src/Arius.Explorer/Settings/ApplicationSettings.cs`
- `src/Arius.Explorer/RepositoryExplorer/RepositoryExplorerViewModel.cs`
- related Explorer tests in `src/Arius.Explorer.Tests/`

Design rules:

- Persist repository local-directory values as strings in settings.
- Add one typed boundary surface close to the owning model, for example a `RepositoryOptions.LocalRoot` property or equivalent parse/validation helper.
- Stop ad hoc `LocalRootPath.Parse(Repository.LocalDirectoryPath)` calls in consumers when the owning type can provide the typed root.
- Keep strings for display text, serialization, and settings persistence.

Why separate:

- This slice touches persisted state and WPF/ViewModel boundaries.
- It needs a precise rule for where parsing happens and where strings remain appropriate.
- It is more semantic than the fixture cleanup.

### 3C: Residual Test Cleanup

This slice covers the remaining test-only callers that still build typed roots through raw string setup even though they are not persistence or UI boundaries.

Representative files:

- `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`
- `src/Arius.Integration.Tests/Pipeline/ContainerCreationTests.cs`
- selected filesystem helper or rooted-path tests where the setup root should be typed earlier

Design rules:

- Prefer typed root setup when the test models a repository root or temp root as a domain value.
- Keep raw strings where the test is intentionally verifying path text rendering or raw BCL interop.
- Avoid broad churn in test files that already use raw strings only as incidental fixtures with no typed boundary under test.

Why separate:

- These changes are lower leverage and easier to overreach.
- They should only follow once the shared infrastructure and Explorer ownership boundaries are clear.

## Recommended Execution Order

1. `3A` fixture temp-root typing
2. `3B` Explorer/settings typed boundary
3. `3C` residual test cleanup

This order minimizes churn because later slices can build on typed fixture APIs and the clarified Explorer ownership boundary.

## Verification

### 3A

- targeted fixture/E2E/integration tests
- `Arius.E2E.Tests` build

### 3B

- Explorer settings tests
- Explorer ViewModel tests affected by repository loading or restore/list commands

### 3C

- focused test suites for only the files being cleaned up

After each executed slice, run the standard broader verification gates already used for typed-path work when the touched surface justifies it.
