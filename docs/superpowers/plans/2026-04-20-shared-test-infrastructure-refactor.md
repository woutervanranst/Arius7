# Shared Test Infrastructure Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract reusable Docker-backed and repository-fixture test infrastructure into `Arius.Tests.Shared`, remove the `Arius.E2E.Tests -> Arius.Integration.Tests` dependency, and then remove the temporary CI workaround.

**Architecture:** Move `AzuriteFixture` and a shared repository fixture base into a new non-test class library. Keep thin wrappers in `Arius.Integration.Tests` and `Arius.E2E.Tests` for project-specific behavior, then restore the CI discovery logic to direct dependency inspection only.

**Tech Stack:** .NET 10, TUnit, Testcontainers.Azurite, Azure Blob SDK, Microsoft.Extensions.Diagnostics.Testing

---

### Task 1: Create Shared Test Library

**Files:**
- Create: `src/Arius.Tests.Shared/Arius.Tests.Shared.csproj`
- Modify: `README.md`
- Modify: `AGENTS.md`

- [ ] **Step 1: Add the new class library project**

Create `src/Arius.Tests.Shared/Arius.Tests.Shared.csproj` as a normal class library with the dependencies needed by shared fixtures.

- [ ] **Step 2: Verify the new project builds**

Run: `dotnet build src/Arius.Tests.Shared/Arius.Tests.Shared.csproj`
Expected: build succeeds

- [ ] **Step 3: Commit**

```bash
git add src/Arius.Tests.Shared/Arius.Tests.Shared.csproj README.md AGENTS.md
git commit -m "test: add shared test infrastructure project"
```

### Task 2: Move Azurite Fixture

**Files:**
- Create: `src/Arius.Tests.Shared/Storage/AzuriteFixture.cs`
- Modify: `src/Arius.Integration.Tests/Storage/AzuriteFixture.cs`
- Modify: `src/Arius.E2E.Tests/Fixtures/AzuriteE2EBackendFixture.cs`
- Test: `src/Arius.E2E.Tests/Fixtures/E2EStorageBackendFixtureTests.cs`

- [ ] **Step 1: Write a failing compatibility test if needed**

Use existing fixture tests to prove the moved fixture still supports Azurite context creation.

- [ ] **Step 2: Move `AzuriteFixture` into the shared project**

Keep it non-test, public where needed, and retain existing behavior.

- [ ] **Step 3: Replace old integration-test location with a forwarding type or update references directly**

Prefer direct namespace updates if the churn is small.

- [ ] **Step 4: Run focused fixture tests**

Run: `dotnet test --project src/Arius.E2E.Tests/Arius.E2E.Tests.csproj --treenode-filter "/*/*/E2EStorageBackendFixtureTests/*"`
Expected: fixture tests pass

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Tests.Shared/Storage/AzuriteFixture.cs src/Arius.Integration.Tests/Storage/AzuriteFixture.cs src/Arius.E2E.Tests/Fixtures/AzuriteE2EBackendFixture.cs src/Arius.E2E.Tests/Fixtures/E2EStorageBackendFixtureTests.cs
git commit -m "test: move Azurite fixture to shared library"
```

### Task 3: Extract Shared Repository Fixture Base

**Files:**
- Create: `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs`
- Modify: `src/Arius.E2E.Tests/Fixtures/E2EFixture.cs`

- [ ] **Step 1: Write or update focused fixture tests around current behavior**

Use existing `E2EFixture` cache-state and path tests as the safety net. Do not weaken coverage.

- [ ] **Step 2: Extract common repository wiring into `RepositoryTestFixture`**

Move shared construction of encryption, core shared services, temp roots, and archive/restore handler creation.

- [ ] **Step 3: Rework `PipelineFixture` to wrap the shared base**

Keep list-query helper behavior and integration-specific conveniences in `PipelineFixture`.

- [ ] **Step 4: Rework `E2EFixture` to wrap the shared base**

Keep E2E-specific cache preservation and disposal coordination in `E2EFixture`.

- [ ] **Step 5: Run focused tests**

Run:
- `dotnet test --project src/Arius.E2E.Tests/Arius.E2E.Tests.csproj --treenode-filter "/*/*/E2EFixture*/*"`
- `dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj`

Expected: no regressions in fixture behavior

- [ ] **Step 6: Commit**

```bash
git add src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs src/Arius.E2E.Tests/Fixtures/E2EFixture.cs
git commit -m "test: share repository fixture infrastructure"
```

### Task 4: Remove Test Project Coupling

**Files:**
- Modify: `src/Arius.E2E.Tests/Arius.E2E.Tests.csproj`
- Modify: `src/Arius.Integration.Tests/Arius.Integration.Tests.csproj`
- Modify: `src/Arius.Tests.Shared/Arius.Tests.Shared.csproj`

- [ ] **Step 1: Replace project references**

Remove `Arius.E2E.Tests -> Arius.Integration.Tests` and add `Arius.Tests.Shared` where needed.

- [ ] **Step 2: Verify build graph**

Run:
- `dotnet build src/Arius.E2E.Tests/Arius.E2E.Tests.csproj`
- `dotnet build src/Arius.Integration.Tests/Arius.Integration.Tests.csproj`

Expected: both projects build without referencing each other

- [ ] **Step 3: Commit**

```bash
git add src/Arius.E2E.Tests/Arius.E2E.Tests.csproj src/Arius.Integration.Tests/Arius.Integration.Tests.csproj src/Arius.Tests.Shared/Arius.Tests.Shared.csproj
git commit -m "test: remove E2E dependency on integration tests"
```

### Task 5: Revert CI Workaround

**Files:**
- Modify: `.github/scripts/Get-DotNetProjectMatrix.ps1`

- [ ] **Step 1: Remove the temporary special case for `Arius.Integration.Tests.csproj` references**

Restore the script to direct dependency inspection only.

- [ ] **Step 2: Verify the script logic by inspection and test selection behavior**

If PowerShell is available, run the script for `macos` and confirm `Arius.E2E.Tests` is no longer selected. If PowerShell is unavailable locally, verify through project graph inspection and CI.

- [ ] **Step 3: Commit**

```bash
git add .github/scripts/Get-DotNetProjectMatrix.ps1
git commit -m "ci: remove temporary Docker test discovery workaround"
```

### Task 6: Final Verification

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`

- [ ] **Step 1: Update docs to describe `Arius.Tests.Shared` ownership of shared test infrastructure**

- [ ] **Step 2: Run verification**

Run:
- `dotnet test --project src/Arius.E2E.Tests/Arius.E2E.Tests.csproj --treenode-filter "/*/*/E2EStorageBackendFixtureTests/*"`
- `dotnet test --project src/Arius.E2E.Tests/Arius.E2E.Tests.csproj --treenode-filter "/*/*/RepresentativeScenarioRunnerTests/*"`
- `dotnet test --project src/Arius.E2E.Tests/Arius.E2E.Tests.csproj --treenode-filter "/*/*/RepresentativeArchiveRestoreTests/*"`
- `dotnet test --project src/Arius.E2E.Tests/Arius.E2E.Tests.csproj --treenode-filter "/*/*/ArchiveTierRepresentativeTests/*"`
- `dotnet test --project src/Arius.E2E.Tests/Arius.E2E.Tests.csproj --treenode-filter "/*/*/E2ETests/*"`
- `dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj`
- `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj`

Expected: all pass, with the existing Azure cold-restore skips still in place

- [ ] **Step 3: Commit**

```bash
git add README.md AGENTS.md
git commit -m "docs: describe shared test infrastructure"
```
