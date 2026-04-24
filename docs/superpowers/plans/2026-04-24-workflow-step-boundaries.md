# Workflow Step Boundary Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `RepresentativeWorkflowRunner` orchestration-only by moving step-specific archive, restore, conflict, and archive-tier helper logic into step-local or step-adjacent helpers under `src/Arius.E2E.Tests/Workflows/Steps/`.

**Architecture:** Keep the workflow runner responsible only for context creation, fixture lifetime, workflow state initialization, and step sequencing. Move behavior that exists to support a specific step into that step or into focused helper classes in `Workflows/Steps/` when shared by multiple step types. Preserve workflow behavior and verification commands unchanged.

**Tech Stack:** .NET 10, TUnit, Arius E2E fixtures, restore/archive command handlers, Azure Blob adapter, Azurite

---

## File Structure

**Create**
- `src/Arius.E2E.Tests/Workflows/Steps/ArchiveStepSupport.cs`
  - Shared archive invocation/options helper for `ArchiveStep` and archive-tier setup when needed.
- `src/Arius.E2E.Tests/Workflows/Steps/RestoreStepSupport.cs`
  - Shared restore invocation, conflict setup, expected-state assertion, and small local helper methods used by `RestoreStep` and `AssertConflictBehaviorStep`.
- `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierStepSupport.cs`
  - Archive-tier-specific restore handler creation, polling, sideload, blob cleanup, and expected restore assertions.

**Modify**
- `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowRunner.cs`
  - Delete step-specific static helpers and keep only orchestration responsibilities.
- `src/Arius.E2E.Tests/Workflows/Steps/ArchiveStep.cs`
  - Call `ArchiveStepSupport` instead of `RepresentativeWorkflowRunner` for archive behavior.
- `src/Arius.E2E.Tests/Workflows/Steps/RestoreStep.cs`
  - Call `RestoreStepSupport` for restore execution and outcome assertions.
- `src/Arius.E2E.Tests/Workflows/Steps/AssertConflictBehaviorStep.cs`
  - Call `RestoreStepSupport` for conflict setup and restore outcome assertions.
- `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs`
  - Call `ArchiveTierStepSupport` and any shared archive helper instead of `RepresentativeWorkflowRunner`.

**Test/Verify During Implementation**
- `src/Arius.E2E.Tests/Arius.E2E.Tests.csproj`
- `src/Arius.E2E.Tests/RepresentativeArchiveRestoreTests.cs`

## Tasks

### Task 1: Move archive and restore helper behavior out of the runner

**Files:**
- Create: `src/Arius.E2E.Tests/Workflows/Steps/ArchiveStepSupport.cs`
- Create: `src/Arius.E2E.Tests/Workflows/Steps/RestoreStepSupport.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/ArchiveStep.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/RestoreStep.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/AssertConflictBehaviorStep.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowRunner.cs`

- [ ] Move archive option creation, archive execution, snapshot-version formatting, restore execution, conflict file setup, and restore-outcome assertions into step support types under `Workflows/Steps/`.
- [ ] Update `ArchiveStep`, `RestoreStep`, and `AssertConflictBehaviorStep` to depend on those helpers instead of calling static methods on the runner.
- [ ] Remove the now-unused archive/restore/conflict helper methods from `RepresentativeWorkflowRunner`.

### Task 2: Move archive-tier-specific behavior beside the archive-tier step

**Files:**
- Create: `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierStepSupport.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowRunner.cs`

- [ ] Move archive-tier restore-handler creation, blob polling, content-byte reading, sideloading, blob deletion, and expected archive-tier restore assertions into `ArchiveTierStepSupport`.
- [ ] Update `ArchiveTierLifecycleStep` to call the new support class and keep its own file focused on workflow intent.
- [ ] Remove the now-unused archive-tier helper methods from `RepresentativeWorkflowRunner`.

### Task 3: Verify runner-only orchestration behavior remains intact

**Files:**
- Modify: `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowRunner.cs`

- [ ] Confirm `RepresentativeWorkflowRunner` is left with workflow bootstrapping, state construction, step sequencing, and fixture disposal only.
- [ ] Run `dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj"`.
- [ ] Run `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/RepresentativeArchiveRestoreTests/*"`.
