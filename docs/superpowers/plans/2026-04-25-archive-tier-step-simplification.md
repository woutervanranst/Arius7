# Archive Tier Step Simplification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rewrite `ArchiveTierLifecycleStep` into a simpler two-pass archive-tier workflow that proves pending rehydration behavior and ready restore behavior without the separate duplicate-copy rerun phase.

**Architecture:** Keep `ArchiveTierLifecycleStep` as one self-contained workflow step that starts from the preserved versioned source subtree, moves the relevant tar chunks to archive tier, runs one pending restore that verifies the prompt and staged rehydrated blobs, then sideloads ready rehydrated blobs and runs one successful restore that also verifies cleanup. Remove dead tracking code and helper logic that only existed for the dropped rerun phase.

**Tech Stack:** .NET 10, TUnit, Azure Blob archive tier behavior, Arius restore pipeline, TestContainers/Azurite and live Azure E2E backends

---

## File Structure

**Modify**
- `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs`
  - Simplify the step flow to pending restore -> ready restore, keep high-level comments, remove duplicate-copy rerun logic, and keep staging-blob and cleanup assertions.

**Delete if unused**
- `src/Arius.E2E.Tests/Services/CopyTrackingBlobService.cs`
  - Remove if no remaining test code depends on copy-call tracking.

**Verify**
- `src/Arius.E2E.Tests/Arius.E2E.Tests.csproj`
- `src/Arius.E2E.Tests/RepresentativeArchiveRestoreTests.cs`

## Tasks

### Task 1: Remove the duplicate-copy rerun phase

**Files:**
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs`

- [ ] Remove the second pending restore pass and the `CopyTrackingBlobService` usage from the step.
- [ ] Keep the first pending restore assertions: rehydration prompt captured, pending chunk count > 0, no files restored, and `chunks-rehydrated/` staging blobs created.
- [ ] Keep the final ready restore assertions: restore success, no pending chunks left, restored subtree matches expected source, and cleanup callback deletes staged rehydrated blobs.

### Task 2: Remove dead archive-tier tracking code

**Files:**
- Delete if unused: `src/Arius.E2E.Tests/Services/CopyTrackingBlobService.cs`
- Modify if needed: `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs`

- [ ] Remove `CopyTrackingBlobService` if it has no remaining call sites.
- [ ] Remove any helper/local variables in `ArchiveTierLifecycleStep` that existed only for the dropped rerun phase.
- [ ] Tighten comments so the file explains the simpler two-pass lifecycle clearly.

### Task 3: Verify the simplified flow

**Files:**
- Modify if needed: `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs`

- [ ] Run `dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj"`.
- [ ] Run `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/RepresentativeArchiveRestoreTests/*"`.
- [ ] If both pass, keep only the simplified pending/ready flow and do not reintroduce the dropped rerun phase.
