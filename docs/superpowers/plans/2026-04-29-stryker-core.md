# Stryker Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a local, Core-only Stryker.NET setup that mutation-tests `Arius.Core` through `Arius.Core.Tests` and documents how to run it.

**Architecture:** Keep Stryker configuration at the repository root so the command is stable from the workspace root. Scope mutation testing to `src/Arius.Core/Arius.Core.csproj` and test execution to `src/Arius.Core.Tests/Arius.Core.Tests.csproj`; do not add CI enforcement until a baseline score and runtime are known.

**Tech Stack:** .NET 10, Stryker.NET, TUnit, central package management, Markdown documentation.

---

## File Structure

- Create: `stryker-config.json` — repository-level Stryker.NET configuration for Core-only mutation testing.
- Modify: `README.md` — add a short development subsection explaining how to install and run Stryker locally.
- Existing reference: `src/Arius.Core/Arius.Core.csproj` — mutation target project.
- Existing reference: `src/Arius.Core.Tests/Arius.Core.Tests.csproj` — test project used by Stryker.
- Existing reference: `docs/superpowers/specs/2026-04-29-stryker-core-design.md` — approved design.

## Implementation Tasks

### Task 1: Add Core-Only Stryker Configuration

**Files:**
- Create: `stryker-config.json`

- [ ] **Step 1: Create the Stryker config file**

Create `stryker-config.json` with this exact content:

```json
{
  "stryker-config": {
    "project": "src/Arius.Core/Arius.Core.csproj",
    "test-projects": [
      "src/Arius.Core.Tests/Arius.Core.Tests.csproj"
    ],
    "reporters": [
      "html",
      "progress"
    ]
  }
}
```

- [ ] **Step 2: Verify the JSON is parseable**

Run:

```bash
python3 -m json.tool stryker-config.json
```

Expected: command exits successfully and prints formatted JSON containing `stryker-config`, `project`, `test-projects`, and `reporters`.

- [ ] **Step 3: Commit**

Only commit if the user explicitly asked for commits in the current session. If commits are approved, run:

```bash
git add stryker-config.json
git commit -m "test: add core mutation testing config"
```

Expected: a commit is created containing only `stryker-config.json`.

### Task 2: Document Local Stryker Usage

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Add a mutation testing subsection**

In `README.md`, under the `## Development` section and before `### Test Suite Architecture`, add this subsection:

```markdown
### Mutation Testing

Stryker.NET is configured for local mutation testing of `Arius.Core` through `Arius.Core.Tests`.

Install the tool once:

```bash
dotnet tool install --global dotnet-stryker
```

Run the Core mutation test pass from the repository root:

```bash
dotnet stryker --config-file stryker-config.json
```

The initial Stryker setup is intentionally local-only. Use the HTML report to inspect surviving mutants and establish a baseline before adding CI thresholds or expanding the scope beyond Core.
```

- [ ] **Step 2: Verify the README section renders correctly**

Read `README.md` around the Development section and confirm:

- `### Mutation Testing` appears directly under `## Development`.
- Both command blocks are fenced as `bash`.
- The section says the setup is local-only and Core-scoped.

- [ ] **Step 3: Commit**

Only commit if the user explicitly asked for commits in the current session. If commits are approved, run:

```bash
git add README.md
git commit -m "docs: document core mutation testing"
```

Expected: a commit is created containing only the README documentation change.

### Task 3: Verify Core Tests Still Pass

**Files:**
- Existing: `src/Arius.Core.Tests/Arius.Core.Tests.csproj`

- [ ] **Step 1: Run the Core test project**

Run:

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj
```

Expected: `Arius.Core.Tests` builds and all tests pass. If tests fail before Stryker is run, stop and diagnose that failure before continuing because Stryker needs a passing test project.

- [ ] **Step 2: Commit**

No commit is expected for this task because it only verifies the existing Core test suite.

### Task 4: Verify Stryker Starts And Produces A Report

**Files:**
- Existing: `stryker-config.json`
- Existing: `src/Arius.Core/Arius.Core.csproj`
- Existing: `src/Arius.Core.Tests/Arius.Core.Tests.csproj`

- [ ] **Step 1: Ensure Stryker is available**

Run:

```bash
dotnet stryker --version
```

Expected: command exits successfully and prints a Stryker.NET version. If the command is not found, install it:

```bash
dotnet tool install --global dotnet-stryker
```

Expected: command exits successfully and reports that `dotnet-stryker` was installed. If the tool is already installed, use the installed version.

- [ ] **Step 2: Run Stryker with the repository config**

Run:

```bash
dotnet stryker --config-file stryker-config.json
```

Expected: Stryker starts, targets `src/Arius.Core/Arius.Core.csproj`, uses `src/Arius.Core.Tests/Arius.Core.Tests.csproj`, runs mutants, and writes an HTML report under the Stryker output directory.

- [ ] **Step 3: If TUnit or SDK compatibility fails, apply the smallest config fix**

If Stryker fails before mutation testing starts because it cannot discover or run the TUnit test project, record the exact error message. Update only `stryker-config.json` with the minimum Stryker-supported setting required by the error, then rerun:

```bash
dotnet stryker --config-file stryker-config.json
```

Expected: Stryker starts successfully or reaches a clear project/test compatibility failure with the exact error captured for follow-up.

- [ ] **Step 4: Commit**

Only commit if the user explicitly asked for commits in the current session. If commits are approved and `stryker-config.json` changed during compatibility fixing, run:

```bash
git add stryker-config.json
git commit -m "test: tune core mutation testing config"
```

Expected: a commit is created containing only the minimal Stryker config fix.

### Task 5: Final Review

**Files:**
- Existing: `stryker-config.json`
- Existing: `README.md`
- Existing: `docs/superpowers/specs/2026-04-29-stryker-core-design.md`

- [ ] **Step 1: Check the working tree**

Run:

```bash
git status --short
```

Expected: only intentional files are modified or untracked: `stryker-config.json`, `README.md`, `docs/superpowers/specs/2026-04-29-stryker-core-design.md`, and `docs/superpowers/plans/2026-04-29-stryker-core.md`, unless the user has unrelated concurrent changes.

- [ ] **Step 2: Check the final diff**

Run:

```bash
git diff -- stryker-config.json README.md docs/superpowers/specs/2026-04-29-stryker-core-design.md docs/superpowers/plans/2026-04-29-stryker-core.md
```

Expected: the diff contains only the Stryker config, README documentation, approved design spec, and this implementation plan.

- [ ] **Step 3: Summarize verification results**

Report these exact items to the user:

- Whether `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj` passed.
- Whether `dotnet stryker --config-file stryker-config.json` started and produced a report.
- The report path if Stryker produced one.
- Any compatibility issue or skipped verification.

## Self-Review

- Spec coverage: Tasks cover a Core-only Stryker config, README developer guidance, Core test verification, and local Stryker report verification. CI thresholds and broader project scope are intentionally excluded as non-goals.
- Placeholder scan: No placeholder steps remain. Each file change includes exact content or exact verification commands.
- Type and path consistency: Paths consistently use `src/Arius.Core/Arius.Core.csproj`, `src/Arius.Core.Tests/Arius.Core.Tests.csproj`, `stryker-config.json`, and `README.md`.
