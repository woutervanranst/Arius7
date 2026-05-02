# Filetree Mutation Hardening Design

## Goal

Improve mutation resistance in the filetree domain by running Stryker against `Arius.Core`, focusing on `src/Arius.Core/Shared/FileTree/*`, and iterating until the remaining survivors in that area are equivalent mutants or low-value checks that do not justify further production or test complexity.

## Scope

- Production code:
  - `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`
  - `src/Arius.Core/Shared/FileTree/FileTreeModels.cs`
  - `src/Arius.Core/Shared/FileTree/FileTreePaths.cs`
  - `src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs`
  - `src/Arius.Core/Shared/FileTree/FileTreeService.cs`
  - `src/Arius.Core/Shared/FileTree/FileTreeStagingSession.cs`
  - `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`
- Tests:
  - `src/Arius.Core.Tests/Shared/FileTree/*`

Out of scope:

- Unrelated refactors outside the filetree domain
- Broad repository-wide mutation score work
- New abstractions added only to satisfy mutation tooling

## Recommended Approach

Use a mutation-driven loop instead of pre-emptive broad test expansion.

Why:

- It keeps the work evidence-driven.
- It favors the smallest change that kills a real survivor.
- It allows small production hardening only when a mutant reveals a real behavioral gap.

Alternatives considered:

1. Manual audit first, then Stryker.
   - Faster to start, but weaker signal and more likely to miss subtle survivors.
2. Add broad invariant/property-style tests first.
   - Can kill many mutants at once, but adds more upfront design cost and a higher risk of overbuilding the test surface.

## Execution Loop

1. Run baseline Stryker with `dotnet stryker --config-file stryker-config.json`.
2. Inspect surviving mutants in the filetree domain only.
3. Group survivors by shared behavior gap when they clearly point at the same missing assertion or guard.
4. For the next survivor group, write a failing TUnit test first.
5. Run the targeted `Arius.Core.Tests` test command and confirm the new test fails for the expected reason.
6. Make the smallest production or test change needed to satisfy the test.
7. Re-run the relevant tests and confirm they pass.
8. Commit that single logical change.
9. Re-run Stryker and repeat.

## Change Rules

- One logical change per commit.
- Prefer test-only changes when they express an already-intended invariant.
- Allow production hardening when a survivor exposes a real bug, weak invariant, or missing validation.
- Do not add speculative extensibility, helpers, or infrastructure.
- Do not weaken filetree durability or correctness semantics for easier testing.

## Test Strategy

Target missing assertions around:

- canonical serialization and parsing behavior
- filetree hash determinism
- staged directory deduplication and conflict handling
- empty-directory and null-root behavior
- cache and remote-existence semantics in `FileTreeService`
- concurrent read or upload coordination where mutants survive because tests only assert coarse outcomes

Tests should remain behavior-focused and avoid coupling to private implementation details beyond what is already exposed by the public or internal API surface.

## Verification

Required evidence before claiming progress in each cycle:

- `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj`
- `dotnet stryker --config-file stryker-config.json`

When useful during a red-green loop, narrower test runs are acceptable for fast feedback, but no cycle is considered complete until a fresh full `Arius.Core.Tests` run and a fresh Stryker run confirm the effect.

## Stopping Rule

Stop only when the remaining filetree-domain survivors are equivalent mutants or low-value survivors whose elimination would require disproportionate complexity or tests that no longer express meaningful product behavior.

At that point, summarize the remaining survivors explicitly so the stopping decision is inspectable.
