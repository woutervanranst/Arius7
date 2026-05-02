---
name: stryker-hardening
description: Use when running mutation-driven hardening for a supported Arius domain such as filetree, chunk-storage, or snapshot.
invocable: true
---

# Stryker Hardening

## Overview

Use Stryker as the driver for mutation hardening in one supported domain at a time. Prefer high-value behavior-focused test hardening, allow small production bug fixes only when mutants reveal real issues, and make one commit per logical hardening change.

## Supported Domains

| Label | Production Scope | Test Scope |
|---|---|---|
| `filetree` | `src/Arius.Core/Shared/FileTree/*` | `src/Arius.Core.Tests/Shared/FileTree/*` |
| `chunk-storage` | `src/Arius.Core/Shared/ChunkStorage/*` | `src/Arius.Core.Tests/Shared/ChunkStorage/*` |
| `snapshot` | `src/Arius.Core/Shared/Snapshot/*` | `src/Arius.Core.Tests/Shared/Snapshot/*` |

If the user gives an unsupported label, stop and ask them to choose from the supported labels above.

## Workflow

1. Resolve the requested domain label to the production and test scope.
2. Run `dotnet stryker --config-file stryker-config.json`.
3. Inspect only the selected domain's survivors and timeouts.
4. Pick the next highest-value mutant cluster.
5. Write or strengthen tests first when they express intended behavior.
6. If a mutant reveals a real bug or weak invariant, make the smallest production fix needed.
7. Run `slopwatch analyze` after edits.
8. Run `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj`.
9. Re-run Stryker and repeat.
10. Commit each logical hardening change separately.
11. Stop when only equivalent or low-value domain mutants remain.

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

Stop when the selected domain has no meaningful survivors left and the remaining mutants are equivalent or low-value.

Always summarize what remains and why it was not pursued.
