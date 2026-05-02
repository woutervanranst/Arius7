# Stryker Hardening Command Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a reusable `/stryker-harden <domain-label>` slash command that runs the repo's mutation-hardening workflow for a supported domain.

**Architecture:** Keep the command wrapper thin in `.opencode/commands/` and put the real workflow in one repo-local skill. The skill owns the supported domain mapping, Stryker-driven iteration rules, high-value test-hardening bias, commit discipline, and stopping rules.

**Tech Stack:** OpenCode slash commands, repo-local skills, markdown workflow docs, git, Stryker.NET, TUnit, Slopwatch

---

## File Map

- Create: `.opencode/commands/stryker-harden.md`
  Purpose: provide the slash-command entrypoint and forward execution to the workflow.
- Create: `.agents/skills/stryker-hardening/SKILL.md`
  Purpose: define the reusable mutation-hardening workflow, domain mappings, verification, and stopping rules.
- Modify if needed: `AGENTS.md`
  Purpose: document the new slash command or workflow only if repo-level agent guidance should reference it explicitly.

### Task 1: Add The Slash Command Wrapper

**Files:**
- Create: `.opencode/commands/stryker-harden.md`
- Test: manual command-doc review

- [ ] **Step 1: Write the command file**

Create `.opencode/commands/stryker-harden.md` with this content:

```md
---
description: Harden tests and fix high-value bugs for a supported domain using Stryker
---

Run mutation-driven hardening for a supported domain label.

**Input**: A required domain label such as `/stryker-harden filetree`.

Use the repo-local `stryker-hardening` skill for the workflow.

If the domain label is missing or unsupported, stop and ask the user to choose from the supported labels.
```

- [ ] **Step 2: Verify the command file exists and reads clearly**

Read:

```text
.opencode/commands/stryker-harden.md
```

Expected: frontmatter is present, the command name is clear, and the wrapper stays thin.

- [ ] **Step 3: Commit the command wrapper**

```bash
git add .opencode/commands/stryker-harden.md
git commit -m "feat: add stryker hardening slash command"
```

### Task 2: Add The Repo-Local Hardening Skill

**Files:**
- Create: `.agents/skills/stryker-hardening/SKILL.md`
- Test: skill document review

- [ ] **Step 1: Write the skill frontmatter and overview**

Create `.agents/skills/stryker-hardening/SKILL.md` with frontmatter like:

```md
---
name: stryker-hardening
description: Use when running mutation-driven hardening for a supported Arius domain such as filetree, chunk-storage, or snapshot.
invocable: true
---

# Stryker Hardening

## Overview

Use Stryker as the driver for mutation hardening in one supported domain at a time. Prefer high-value behavior-focused test hardening, allow small production bug fixes only when mutants reveal real issues, and make one commit per logical hardening change.
```

- [ ] **Step 2: Add explicit supported domain mapping**

Include a section like:

```md
## Supported Domains

| Label | Production Scope | Test Scope |
|---|---|---|
| `filetree` | `src/Arius.Core/Shared/FileTree/*` | `src/Arius.Core.Tests/Shared/FileTree/*` |
| `chunk-storage` | `src/Arius.Core/Shared/ChunkStorage/*` | `src/Arius.Core.Tests/Shared/ChunkStorage/*` |
| `snapshot` | `src/Arius.Core/Shared/Snapshot/*` | `src/Arius.Core.Tests/Shared/Snapshot/*` |
```

And a guardrail:

```md
If the user gives an unsupported label, stop and ask them to choose from the supported labels above.
```

- [ ] **Step 3: Add the mutation-hardening workflow**

Include a concrete workflow section covering:

```md
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
```

- [ ] **Step 4: Add value filters and stopping rules**

Include explicit guidance like:

```md
## High-Value Filter

Prefer tests that lock down observable product behavior, invariants, ordering guarantees, validation rules, or failure propagation.

Avoid trivial hardening such as:
- asserting exception message text when the throw itself is the real behavior
- pinning impossible internal states only to satisfy a mutant
- tests that only prove Stryker can be manipulated rather than product behavior

## Commit Rule

Make one git commit per logical hardening change.

## Stopping Rule

Stop when the selected domain has no meaningful survivors left and the remaining mutants are equivalent or low-value.
Always summarize what remains and why it was not pursued.
```

- [ ] **Step 5: Commit the skill**

```bash
git add .agents/skills/stryker-hardening/SKILL.md
git commit -m "feat: add stryker hardening skill"
```

### Task 3: Wire The Command To Repo Guidance

**Files:**
- Modify if needed: `AGENTS.md`
- Test: document review

- [ ] **Step 1: Decide if AGENTS guidance needs an explicit reference**

If existing guidance is already sufficient, make no change.

If an explicit note would help future agents discover the command, add one small line under a relevant section, for example:

```md
- Use `/stryker-harden <domain-label>` for mutation-driven hardening in supported Arius domains.
```

- [ ] **Step 2: Verify the guidance remains minimal**

Read `AGENTS.md` after the change.

Expected: no duplication of the skill body, only a discoverability note if needed.

- [ ] **Step 3: Commit the guidance change only if one was made**

```bash
git add AGENTS.md
git commit -m "docs: note stryker hardening command"
```

Skip this commit if `AGENTS.md` was not changed.

### Task 4: Verify The Command Artifacts

**Files:**
- Test: `.opencode/commands/stryker-harden.md`
- Test: `.agents/skills/stryker-hardening/SKILL.md`

- [ ] **Step 1: Read both artifacts together**

Read:

```text
.opencode/commands/stryker-harden.md
.agents/skills/stryker-hardening/SKILL.md
```

Expected: the command is a thin wrapper and the skill contains the workflow logic.

- [ ] **Step 2: Check the worktree state**

```bash
git status --short
```

Expected: clean after the final commit, or only intentional edits if a repo-guidance note is still pending.

- [ ] **Step 3: Summarize supported labels and behavior**

Confirm the final implementation supports at least:

```text
filetree
chunk-storage
snapshot
```

And summarize that it:

```text
runs Stryker,
focuses on the selected domain,
prefers high-value tests,
allows small real bug fixes,
commits one logical change at a time,
and stops at equivalent/low-value remainder.
```
