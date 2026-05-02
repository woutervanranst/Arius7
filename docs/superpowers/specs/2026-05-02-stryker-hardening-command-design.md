# Stryker Hardening Command Design

## Goal

Add a reusable slash command that performs mutation-driven hardening for a named domain label, with the same overall behavior as the recent filetree pass: run Stryker, focus on the selected domain, prefer high-value test hardening, allow small bug fixes when mutants expose real issues, make one commit per logical hardening change, and stop when only equivalent or low-value mutants remain for that domain.

## Scope

In scope:

- A new slash command under `.opencode/commands/`
- A repo-local skill or companion workflow definition under `.agents/skills/` or `.opencode/skills/`
- Explicit domain-label mapping for supported hardening targets
- Documentation for how to invoke the command and what behavior to expect

Out of scope:

- Full custom plugin or parser automation beyond the existing OpenCode + skill workflow
- Arbitrary path/glob input from the slash command
- Repository-wide mutation orchestration in one command invocation

## User-Facing Behavior

The command surface should be simple:

```text
/stryker-harden <domain-label>
```

Examples:

```text
/stryker-harden filetree
/stryker-harden chunk-storage
```

The command should:

1. Resolve the domain label to a known set of production and test files.
2. Run mutation analysis using the repository's existing Stryker setup.
3. Focus only on survivors and timeouts relevant to that domain.
4. Harden tests first when that captures intended behavior.
5. Apply a small production fix only when a mutant reveals a real bug or weak invariant.
6. Avoid trivial, silly, or low-value tests whose only purpose is gaming the tool.
7. Create one commit per logical hardening change.
8. Continue until only equivalent or low-value mutants remain in the selected domain.
9. End with a concise summary of what changed and what remains.

## Recommended Structure

Use two artifacts:

1. A thin slash command in `.opencode/commands/stryker-harden.md`
2. A dedicated repo-local skill that contains the actual mutation-hardening workflow and domain mapping

Why this split:

- The slash command stays small and easy to invoke.
- The hardening workflow is easier to evolve in one place.
- The domain mapping and stopping rules can be documented clearly.
- The skill can be tested and refined independently of the command wrapper.

## Domain Label Mapping

The workflow should use explicit label mapping rather than guessing from arbitrary input.

Initial mapping should be a small curated table, for example:

- `filetree` → `src/Arius.Core/Shared/FileTree/*`, matching tests under `src/Arius.Core.Tests/Shared/FileTree/*`
- `chunk-storage` → `src/Arius.Core/Shared/ChunkStorage/*`, matching tests under `src/Arius.Core.Tests/Shared/ChunkStorage/*`
- `snapshot` → `src/Arius.Core/Shared/Snapshot/*`, matching tests under `src/Arius.Core.Tests/Shared/Snapshot/*`

If a requested label is unknown, the command should stop and ask the user to choose from supported labels.

## Workflow Rules

The workflow should follow these rules:

- Use Stryker as the driver, not manual coverage guessing.
- Narrow the decision-making to the selected domain after each Stryker run.
- Favor behavior-focused tests over implementation-coupled assertions.
- Reject trivial tests that only pin exception message text, impossible states, or tool artifacts unless those are real product behavior.
- Allow production-code changes only when mutants reveal a real bug, weak validation rule, or observable contract gap.
- Keep each change minimal and commit each logical hardening step separately.
- Re-run relevant tests and Stryker before claiming progress.

## Verification

Each hardening cycle should require fresh evidence:

- `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj`
- `dotnet stryker --config-file stryker-config.json`
- `slopwatch analyze` after code or test edits

The command should not claim the domain is hardened without a fresh Stryker report for that invocation.

## Stopping Rule

Stop when the selected domain has no meaningful survivors left and any remaining timeouts or uncovered mutants are judged equivalent or low-value relative to product behavior.

The final output should explicitly list any remaining domain mutants that were intentionally not pursued, with a short reason.

## Why Not A Heavier Automation Layer

A custom script or plugin could automate more of the report parsing, but that adds tool maintenance and failure modes that are not necessary yet. The current repository already supports command-plus-skill workflows cleanly, so the smallest durable solution is to codify the process there first.
