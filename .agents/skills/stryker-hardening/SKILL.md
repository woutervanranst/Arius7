---
name: stryker-hardening
description: Use when running mutation-driven hardening for any Arius domain, feature, or code area described by the user.
invocable: true
---

# Stryker Hardening

## Overview

Use Stryker as the driver for mutation hardening in one user-described scope at a time. Infer the relevant code and tests from the user's wording, prefer high-value behavior-focused test hardening, allow small production bug fixes only when mutants reveal real issues, and make one commit per logical hardening change.

## Scope Resolution

Resolve the user's phrase into a concrete scope before running Stryker.

Use repo evidence to infer the scope:

- search production code for matching feature, slice, service, or concept names
- search tests for matching test areas and fixture names
- use docs, specs, and recent conversation context as hints when they narrow the intended area
- prefer the smallest coherent scope that matches the user's wording

Examples of valid user-described scopes:

- `filetree`
- `restore pipeline`
- `progress display`
- `snapshot validation`

If the inferred scope is ambiguous or too broad, stop and ask the user to clarify before running Stryker.

## Workflow

1. Resolve the requested scope description to the production and test scope.
2. Summarize the inferred production and test scope before mutation work starts.
3. Run `dotnet stryker --config-file stryker-config.json`.
4. Inspect only the inferred scope's survivors and timeouts.
5. Pick the next highest-value mutant cluster.
6. Write or strengthen tests first when they express intended behavior.
7. If a mutant reveals a real bug or weak invariant, make the smallest production fix needed.
8. Run `slopwatch analyze` after edits.
9. Run `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj`.
10. Re-run Stryker and repeat.
11. Commit each logical hardening change separately.
12. Stop when only equivalent or low-value mutants remain in the inferred scope.

## High-Value Filter

Prefer tests that lock down observable product behavior, invariants, ordering guarantees, validation rules, or failure propagation.

Avoid trivial hardening such as:

- asserting exception message text when the throw itself is the real behavior
- pinning impossible internal states only to satisfy a mutant
- tests that only prove Stryker can be manipulated rather than product behavior

## Verification

Use fresh evidence before claiming progress:

- `slopwatch analyze`
- `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj`
- `dotnet stryker --config-file stryker-config.json`

Do not claim a domain is hardened without a fresh Stryker report from the current invocation.

## Commit Rule

Make one git commit per logical hardening change.

## Stopping Rule

Stop when the inferred scope has no meaningful survivors left and the remaining mutants are equivalent or low-value.

Always summarize what remains and why it was not pursued.
