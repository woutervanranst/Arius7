# Repo-Wide Path Typing Sweep Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Strongly type the selected remaining path-like string concepts across E2E/tests/benchmarks, production internals, and filetree staging infrastructure using the existing path model.

**Architecture:** This change updates owning boundaries rather than wrapping individual string construction sites. Each task retypes one coherent area, updates its immediate consumers in the same change, verifies with focused tests first, and commits before moving on so compile fallout stays controlled.

**Tech Stack:** C# 13 / .NET 10, TUnit, Arius path domain types (`RelativePath`, `LocalRootPath`, `RootedPath`, `PathSegment`)

---

### Task 1: Docs Cleanup And E2E Typed State Boundary

**Files:**
- Modify: `docs/superpowers/specs/2026-05-05-repo-wide-path-typing-sweep-design.md`
- Delete: `docs/superpowers/specs/2026-05-05-synthetic-repository-state-relative-path-design.md`
- Modify: `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryState.cs`
- Modify: `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryMaterializer.cs`
- Modify: `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryStateAssertions.cs`
- Modify: `src/Arius.E2E.Tests/Fixtures/E2EFixture.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowState.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowRunner.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/Helpers.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/MaterializeVersionStep.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs`
- Test: `src/Arius.E2E.Tests/**/*.cs`

- [ ] **Step 1: Write the failing test**

Add or update focused E2E coverage so the typed synthetic repository state is exercised explicitly. Prefer extending an existing representative-workflow or dataset assertion path rather than adding a new isolated test-only helper.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/Canonical_Representative_Workflow_Runs_On_Supported_Backends"`

Expected: FAIL or compile failure caused by the new typed-path expectations not being implemented yet.

- [ ] **Step 3: Write minimal implementation**

Retype the E2E owning boundaries:

- `SyntheticRepositoryState.RootPath` -> `LocalRootPath`
- `SyntheticRepositoryState.Files` -> `IReadOnlyDictionary<RelativePath, ContentHash>`
- `E2EFixture.LocalRoot` / `RestoreRoot` -> `LocalRootPath`
- `RepresentativeWorkflowState.VersionedSourceRoot` -> `LocalRootPath`
- `ArchiveTierTargetChunk.TargetRelativePath` -> `RelativePath`

Update downstream joins/lookups to convert to strings only at final host-path boundaries.

- [ ] **Step 4: Run tests to verify they pass**

Run:
- `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/Canonical_Representative_Workflow_Runs_On_Supported_Backends"`
- `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/Hot_tier_pointer_file_probe"`

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-05-05-repo-wide-path-typing-sweep-design.md docs/superpowers/plans/2026-05-05-repo-wide-path-typing-sweep.md src/Arius.E2E.Tests
git commit -m "refactor: type e2e synthetic path state"
```

### Task 2: Test And Benchmark Path Carriers

**Files:**
- Modify: `src/Arius.Benchmarks/ArchiveStepBenchmarks.cs`
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`
- Modify: `src/Arius.Integration.Tests/Shared/FileTree/FileTreeBuilderIntegrationTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RoundtripTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RestorePointerTimestampTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs`
- Test: `src/Arius.Core.Tests/**/*.cs`
- Test: `src/Arius.Integration.Tests/**/*.cs`

- [ ] **Step 1: Write the failing test**

Replace one representative string-owned tuple/dictionary carrier with a typed-path expectation in an existing focused test so compile or behavioral failure proves the path carrier is still stringly.

- [ ] **Step 2: Run test to verify it fails**

Run:
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeBuilderTests/*"`
- `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RestorePointerTimestampTests/*"`

Expected: FAIL or compile failure caused by typed path carriers not yet implemented.

- [ ] **Step 3: Write minimal implementation**

Retype benchmark/test-owned path carriers to `RelativePath` or `LocalRootPath`, and expose typed root passthroughs from `PipelineFixture` where it currently drops back to strings.

- [ ] **Step 4: Run tests to verify they pass**

Run:
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeBuilderTests/*"`
- `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/FileTreeBuilderIntegrationTests/*"`
- `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RestorePointerTimestampTests/*"`
- `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RoundtripTests/*"`

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Benchmarks src/Arius.Core.Tests src/Arius.Integration.Tests
git commit -m "test: type benchmark and test path carriers"
```

### Task 3: Production Internal Path Contracts

**Files:**
- Modify: `src/Arius.Core/Features/ArchiveCommand/Events.cs`
- Modify: `src/Arius.Core/Shared/LocalFile/LocalFileEnumerator.cs`
- Modify: `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`
- Modify: dependent consumers in `src/Arius.Cli/` or tests if compile fallout requires it
- Test: `src/Arius.Core.Tests/**/*.cs`
- Test: `src/Arius.Cli.Tests/**/*.cs`

- [ ] **Step 1: Write the failing test**

Add or update focused tests so the production contracts expect `RelativePath` / `RootedPath` at the owning helper boundary. Prefer existing archive-event and list/local-file tests over new synthetic tests.

- [ ] **Step 2: Run test to verify it fails**

Run:
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/*"`
- `dotnet test --project "src/Arius.Cli.Tests/Arius.Cli.Tests.csproj"`

Expected: FAIL or compile failure because the production contract is still stringly.

- [ ] **Step 3: Write minimal implementation**

Retype:

- archive in-process file events to `RelativePath`
- pointer-file helper parameters in `LocalFileEnumerator` to `RootedPath` + `RelativePath`
- matching helper parameters/state in `ListQueryHandler` to `RootedPath` + `RelativePath`

Convert to strings only for logs or final display output.

- [ ] **Step 4: Run tests to verify they pass**

Run:
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/*"`
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/LocalFileEnumeratorTests/*"`
- `dotnet test --project "src/Arius.Cli.Tests/Arius.Cli.Tests.csproj"`

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core src/Arius.Cli src/Arius.Core.Tests src/Arius.Cli.Tests
git commit -m "refactor: type internal path contracts"
```

### Task 4: Filetree Staging Roots

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeStagingSession.cs`
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`
- Modify: affected tests in `src/Arius.Core.Tests/Shared/FileTree/*.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/*.cs`

- [ ] **Step 1: Write the failing test**

Update a focused filetree staging/builder test so the staging root is treated as a typed local root instead of raw string state.

- [ ] **Step 2: Run test to verify it fails**

Run:
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeBuilderTests/*"`
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeServiceTests/*"`

Expected: FAIL or compile failure because staging roots are still stringly.

- [ ] **Step 3: Write minimal implementation**

Retype staging/cache roots to `LocalRootPath`, keep final OS calls string-based, and update helper methods accordingly.

- [ ] **Step 4: Run tests to verify they pass**

Run:
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeBuilderTests/*"`
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeServiceTests/*"`
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeSerializerTests/*"`

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Shared/FileTree src/Arius.Core.Tests/Shared/FileTree
git commit -m "refactor: type filetree staging roots"
```

### Task 5: Broad Verification And Final Review

**Files:**
- Modify: any small fallout files needed from previous tasks only
- Test: `src/Arius.Core.Tests/Arius.Core.Tests.csproj`
- Test: `src/Arius.Integration.Tests/Arius.Integration.Tests.csproj`
- Test: `src/Arius.E2E.Tests/Arius.E2E.Tests.csproj`

- [ ] **Step 1: Run broader verification**

Run sequentially:

- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj"`
- `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj"`
- `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj"`

Expected: PASS

- [ ] **Step 2: Run slopwatch if available**

Run: `dotnet tool run slopwatch analyze`

Expected: `0 issue(s) found` or a clear tool-not-found note if the manifest still lacks the command.

- [ ] **Step 3: Commit final fallout if any**

```bash
git add src docs
git commit -m "test: verify repo-wide path typing sweep"
```
