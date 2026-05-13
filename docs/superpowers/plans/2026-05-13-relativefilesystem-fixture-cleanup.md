# RelativeFileSystem Fixture Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace recent fixture-level path helper code with the existing `LocalDirectory` and `RelativeFileSystem` abstractions, and migrate broad test/workflow call sites away from redundant wrapper methods and manual rooted path assembly.

**Architecture:** Keep the current separation already used in Core: `RelativeFileSystem` handles rooted `RelativePath` I/O, while `LocalDirectory.Resolve(...)` handles the smaller set of cases that need an absolute host path string. Refactor the test fixtures to expose typed roots and rooted filesystems, then migrate callers by intent rather than preserving string helper wrappers.

**Tech Stack:** C#/.NET 9, TUnit, existing Arius filesystem value objects (`RelativePath`, `LocalDirectory`, `RelativeFileSystem`)

---

## File map

**Primary fixture files**
- Modify: `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`
  - Expose typed `LocalDirectory`/`RelativeFileSystem` properties for source and restore roots.
  - Remove `CombineValidatedRelativePath(...)`.
  - Remove or shrink wrapper helpers that duplicate rooted filesystem operations.
- Modify: `src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs`
  - Surface typed fixture members from `RepositoryTestFixture`.
  - Remove redundant wrapper helpers such as `WriteFile`, `ReadRestored`, `RestoredExists`, and probably `WriteRandomFile`.
- Modify: `src/Arius.E2E.Tests/Fixtures/E2EFixture.cs`
  - Surface typed roots/filesystems from `_repository`.
  - Remove `CombineValidatedRelativePath(...)`.
  - Remove redundant wrappers and update internal materialization/reset code to use typed roots.

**Representative tests and helpers to migrate**
- Modify: `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`
- Modify: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveTestEnvironment.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RestorePointerTimestampTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RoundtripTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RestoreDispositionTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/CrashRecoveryTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/GcmIntegrationTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/ListQueryIntegrationTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RecoveryScriptTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RehydrationStateTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/ContainerCreationTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RestoreCostModelTests.cs`
- Modify: `src/Arius.E2E.Tests/E2ETests.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/Helpers.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs`

**Likely verification targets**
- Test: `src/Arius.Core.Tests/Arius.Core.Tests.csproj`
- Test: `src/Arius.Integration.Tests/Arius.Integration.Tests.csproj`
- Test: `src/Arius.E2E.Tests/Arius.E2E.Tests.csproj`

---

### Task 1: Add typed roots and rooted filesystems to shared fixtures

**Files:**
- Modify: `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`
- Test: `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`
- Test: `src/Arius.Integration.Tests/Pipeline/RestorePointerTimestampTests.cs`

- [ ] **Step 1: Write the failing test**

Add focused assertions in an existing test file to lock the intended fixture API shape. Prefer a small test in `RestoreCommandHandlerTests.cs` or a new focused fixture test beside existing shared fixture coverage.

```csharp
[Test]
public async Task RepositoryFixture_Exposes_TypedRoots_And_FileSystems()
{
    await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync();

    fixture.LocalDirectory.ShouldNotBe(default);
    fixture.RestoreDirectory.ShouldNotBe(default);

    fixture.LocalFileSystem.ShouldNotBeNull();
    fixture.RestoreFileSystem.ShouldNotBeNull();

    var relativePath = RelativePath.Parse("nested/file.bin");
    await fixture.LocalFileSystem.WriteAllBytesAsync(relativePath, [1, 2, 3], CancellationToken.None);

    fixture.LocalFileSystem.FileExists(relativePath).ShouldBeTrue();
    File.Exists(fixture.LocalDirectory.Resolve(relativePath)).ShouldBeTrue();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/*/RepositoryFixture_Exposes_TypedRoots_And_FileSystems" -p:UseAppHost=false
```

Expected: FAIL because `RepositoryTestFixture` does not expose `LocalDirectory`, `RestoreDirectory`, `LocalFileSystem`, and `RestoreFileSystem`.

- [ ] **Step 3: Write minimal implementation**

Update `RepositoryTestFixture` to store and expose typed roots/filesystems without changing repository wiring.

Target shape:

```csharp
public LocalDirectory LocalDirectory { get; }
public LocalDirectory RestoreDirectory { get; }

public RelativeFileSystem LocalFileSystem { get; }
public RelativeFileSystem RestoreFileSystem { get; }
```

Constructor initialization should derive these once:

```csharp
LocalDirectory = LocalDirectory.Parse(localRoot);
RestoreDirectory = LocalDirectory.Parse(restoreRoot);
LocalFileSystem = new RelativeFileSystem(LocalDirectory);
RestoreFileSystem = new RelativeFileSystem(RestoreDirectory);
```

Keep existing `LocalRoot` and `RestoreRoot` string properties for command-option boundaries.

- [ ] **Step 4: Run test to verify it passes**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/*/RepositoryFixture_Exposes_TypedRoots_And_FileSystems" -p:UseAppHost=false
```

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add "src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs" "src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs"
git commit -m "refactor: expose typed fixture filesystems"
```

### Task 2: Remove shared fixture helper duplication

**Files:**
- Modify: `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`
- Test: `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`
- Test: `src/Arius.Integration.Tests/Pipeline/RestorePointerTimestampTests.cs`

- [ ] **Step 1: Write the failing test**

Add or update one test to stop using wrapper methods and instead use the typed fixture API.

```csharp
[Test]
public async Task Fixture_FileSystem_Replaces_WrapperHelpers()
{
    await using var fixture = await RepositoryTestFixture.CreateInMemoryAsync();

    var path = RelativePath.Parse("photos/pic.jpg");
    await fixture.LocalFileSystem.WriteAllBytesAsync(path, [1, 2, 3], CancellationToken.None);

    var restoredPath = path;
    await fixture.RestoreFileSystem.WriteAllBytesAsync(restoredPath, [4, 5, 6], CancellationToken.None);

    fixture.RestoreFileSystem.FileExists(restoredPath).ShouldBeTrue();
    fixture.RestoreFileSystem.ReadAllBytes(restoredPath).ShouldBe([4, 5, 6]);
}
```

- [ ] **Step 2: Run test to verify it fails correctly**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/*/Fixture_FileSystem_Replaces_WrapperHelpers" -p:UseAppHost=false
```

Expected: FAIL if the test references APIs not yet wired or if wrappers are still the only viable route.

- [ ] **Step 3: Write minimal implementation**

In `RepositoryTestFixture.cs`:

- delete `CombineValidatedRelativePath(...)`
- delete wrapper methods that are direct one-line duplicates of rooted filesystem operations:
  - `ReadRestored(RelativePath relativePath)`
  - `RestoredExists(RelativePath relativePath)`
- strongly consider deleting:
  - `WriteFile(RelativePath relativePath, byte[] content)`
- keep only helpers that add value beyond raw I/O:
  - `WriteFile(RelativePath relativePath, byte[] content, DateTime created, DateTime modified)` can either remain temporarily or be rewritten to use `LocalFileSystem` + `SetTimestamps`
- update any internal helper usage to rely on:
  - `LocalFileSystem.WriteAllBytesAsync(...)`
  - `RestoreFileSystem.ReadAllBytes(...)`
  - `RestoreFileSystem.FileExists(...)`
  - `LocalDirectory.Resolve(...)` / `RestoreDirectory.Resolve(...)`

- [ ] **Step 4: Run targeted tests**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/*" -p:UseAppHost=false
```

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add "src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs" "src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs"
git commit -m "refactor: remove redundant repository fixture path helpers"
```

### Task 3: Surface typed filesystem access from integration and E2E fixtures

**Files:**
- Modify: `src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs`
- Modify: `src/Arius.E2E.Tests/Fixtures/E2EFixture.cs`
- Test: `src/Arius.Integration.Tests/Pipeline/RestorePointerTimestampTests.cs`
- Test: `src/Arius.E2E.Tests/E2ETests.cs`

- [ ] **Step 1: Write the failing test**

Update one integration test and one E2E test to consume typed fixture members rather than wrapper helpers.

Example integration usage:

```csharp
var relPath = RelativePath.Parse("docs/readme.txt");
await fix.LocalFileSystem.WriteAllBytesAsync(relPath, "hello"u8.ToArray(), CancellationToken.None);
var sourcePath = fix.LocalDirectory.Resolve(relPath);
File.SetLastWriteTimeUtc(sourcePath, expectedModified);
```

Example E2E usage:

```csharp
var relPath = RelativePath.Parse("hot.bin");
await fixture.LocalFileSystem.WriteAllBytesAsync(relPath, content, CancellationToken.None);
fixture.RestoreFileSystem.FileExists(relPath).ShouldBeTrue();
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```bash
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RestorePointerTimestampTests/*" -p:UseAppHost=false
dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/E2ETests/*" -p:UseAppHost=false
```

Expected: FAIL because `PipelineFixture`/`E2EFixture` do not yet expose the typed members.

- [ ] **Step 3: Write minimal implementation**

Update `PipelineFixture.cs` to expose pass-through members:

```csharp
public LocalDirectory LocalDirectory => _repository.LocalDirectory;
public LocalDirectory RestoreDirectory => _repository.RestoreDirectory;
public RelativeFileSystem LocalFileSystem => _repository.LocalFileSystem;
public RelativeFileSystem RestoreFileSystem => _repository.RestoreFileSystem;
```

Delete redundant wrappers:
- `WriteFile(...)`
- `ReadRestored(...)`
- `RestoredExists(...)`
- `WriteRandomFile(...)` unless still clearly useful; if retained, implement it in terms of `LocalFileSystem`

Update `E2EFixture.cs` similarly:
- expose `LocalDirectory`, `RestoreDirectory`, `LocalFileSystem`, `RestoreFileSystem`
- remove `CombineValidatedRelativePath(...)`
- remove wrapper methods that duplicate rooted filesystem operations
- keep `LocalRoot`/`RestoreRoot` string properties for command option boundaries and `SyntheticRepositoryMaterializer.MaterializeV1Async(...)`

- [ ] **Step 4: Run targeted tests**

Run:
```bash
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RestorePointerTimestampTests/*" -p:UseAppHost=false
dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/E2ETests/*" -p:UseAppHost=false
```

Expected: Integration tests PASS. E2E tests may still be environment-gated; compile should succeed.

- [ ] **Step 5: Commit**

```bash
git add "src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs" "src/Arius.E2E.Tests/Fixtures/E2EFixture.cs" "src/Arius.Integration.Tests/Pipeline/RestorePointerTimestampTests.cs" "src/Arius.E2E.Tests/E2ETests.cs"
git commit -m "refactor: expose typed filesystem access from test fixtures"
```

### Task 4: Migrate Core and integration test callers by intent

**Files:**
- Modify: `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`
- Modify: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveTestEnvironment.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RestorePointerTimestampTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RestoreDispositionTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RoundtripTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/CrashRecoveryTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/GcmIntegrationTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/ListQueryIntegrationTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RecoveryScriptTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RehydrationStateTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/ContainerCreationTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RestoreCostModelTests.cs`

- [ ] **Step 1: Write the failing test**

Start with one representative failing caller migration, for example `RestorePointerTimestampTests`:
- replace `fix.WriteFile(...)` with `fix.LocalFileSystem.WriteAllBytesAsync(...)`
- replace `fix.ReadRestored(...)` with `fix.RestoreFileSystem.ReadAllBytes(...)`
- replace `fix.RestoredExists(...)` with `fix.RestoreFileSystem.FileExists(...)`
- replace `Path.Combine(fix.RestoreRoot, relPath.Replace('/', ...))` with `fix.RestoreDirectory.Resolve(RelativePath.Parse(relPath))`

Representative code:

```csharp
var relativePath = RelativePath.Parse(relPath);
await fix.LocalFileSystem.WriteAllBytesAsync(relativePath, content, CancellationToken.None);
var sourcePath = fix.LocalDirectory.Resolve(relativePath);

var restoredPath = fix.RestoreDirectory.Resolve(relativePath);
var pointerPath = fix.RestoreDirectory.Resolve(relativePath.AppendSuffix(".pointer.arius"));
```

- [ ] **Step 2: Run the targeted test to verify it fails first**

Run:
```bash
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RestorePointerTimestampTests/*" -p:UseAppHost=false
```

Expected: FAIL before the fixture and caller changes are complete.

- [ ] **Step 3: Write minimal implementation across the representative call pattern**

Apply the same migration pattern to the listed Core/integration files:

- use `LocalFileSystem.WriteAllBytesAsync(...)` for setup writes
- use `RestoreFileSystem.ReadAllBytes(...)` and `FileExists(...)` for restore assertions
- use `LocalDirectory.Resolve(...)` / `RestoreDirectory.Resolve(...)` for host-path-only APIs such as:
  - `File.SetCreationTimeUtc(...)`
  - `File.SetLastWriteTimeUtc(...)`
  - `File.Delete(...)`
  - `File.Exists(...)` when the check is naturally against an absolute path string
- in `ArchiveTestEnvironment`, replace manual `Path.Combine(...Replace('/', ...))` with:
  - cached `LocalDirectory`
  - cached `RelativeFileSystem`
  - optional direct `RootDirectoryInfo.Resolve(relativePath)` pattern for full path returns

- [ ] **Step 4: Run focused test suites**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/*" -p:UseAppHost=false
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RestorePointerTimestampTests/*" -p:UseAppHost=false
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RoundtripTests/*" -p:UseAppHost=false
```

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add "src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs" "src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveTestEnvironment.cs" "src/Arius.Integration.Tests/Pipeline/RestorePointerTimestampTests.cs" "src/Arius.Integration.Tests/Pipeline/RestoreDispositionTests.cs" "src/Arius.Integration.Tests/Pipeline/RoundtripTests.cs" "src/Arius.Integration.Tests/Pipeline/CrashRecoveryTests.cs" "src/Arius.Integration.Tests/Pipeline/GcmIntegrationTests.cs" "src/Arius.Integration.Tests/Pipeline/ListQueryIntegrationTests.cs" "src/Arius.Integration.Tests/Pipeline/RecoveryScriptTests.cs" "src/Arius.Integration.Tests/Pipeline/RehydrationStateTests.cs" "src/Arius.Integration.Tests/Pipeline/ContainerCreationTests.cs" "src/Arius.Integration.Tests/Pipeline/RestoreCostModelTests.cs"
git commit -m "refactor: use typed filesystem access in core and integration tests"
```

### Task 5: Migrate E2E workflow helpers and remove manual rooted-path assembly

**Files:**
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/Helpers.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs`
- Modify: `src/Arius.E2E.Tests/E2ETests.cs`

- [ ] **Step 1: Write the failing test**

Pick one existing E2E workflow test that exercises the helper paths and migrate it to the new usage.

Representative changes:
- `E2EFixture.CombineValidatedRelativePath(fixture.LocalRoot, targetPath)` becomes `fixture.LocalDirectory.Resolve(targetPath)`
- `Path.Combine(fixture.RestoreRoot, conflictPath.Replace('/', ...))` becomes `fixture.RestoreDirectory.Resolve(RelativePath.Parse(conflictPath))`
- pointer paths become `fixture.RestoreDirectory.Resolve(relativePath.AppendSuffix(".pointer.arius"))`

Example helper usage:

```csharp
var restoredPath = fixture.RestoreDirectory.Resolve(RelativePath.Parse(conflictPath));
var targetRoot = fixture.LocalDirectory.Resolve(targetPath);
var fullPath = state.Fixture.LocalDirectory.Resolve(relativePath);
```

- [ ] **Step 2: Run the targeted E2E test to verify it fails first**

Run:
```bash
dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/Representative*" -p:UseAppHost=false
```

Expected: FAIL to compile or fail at old helper references before migration is complete.

- [ ] **Step 3: Write minimal implementation**

Update the E2E helper files to remove manual string rooted path building:
- `Helpers.cs`
- `ArchiveTierLifecycleStep.cs`
- `E2ETests.cs`

Specific replacements:
- `Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))` -> `LocalDirectory.Resolve(RelativePath.Parse(...))` when starting from string relative paths
- `relativePath + ".pointer.arius"` -> `RelativePath.Parse(relativePath).AppendSuffix(".pointer.arius")` or direct typed `RelativePath` flow if already typed
- `E2EFixture.CombineValidatedRelativePath(...)` -> `LocalDirectory.Resolve(...)`

Keep plain string roots only where required by existing APIs such as:
- `RestoreOptions.RootDirectory`
- `ArchiveCommandOptions.RootDirectory`
- `SyntheticRepositoryStateAssertions.AssertMatchesDiskTreeAsync(..., fixture.RestoreRoot, ...)`
- `Directory.EnumerateFiles(string root, ...)`

- [ ] **Step 4: Run targeted verification**

Run:
```bash
dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/E2ETests/*" -p:UseAppHost=false
dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/Representative*" -p:UseAppHost=false
```

Expected: compile succeeds; runtime may still depend on Azure env/certs.

- [ ] **Step 5: Commit**

```bash
git add "src/Arius.E2E.Tests/Workflows/Steps/Helpers.cs" "src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs" "src/Arius.E2E.Tests/E2ETests.cs"
git commit -m "refactor: remove manual rooted path assembly in e2e tests"
```

### Task 6: Remove remaining redundant wrappers and verify broad coverage

**Files:**
- Modify: any remaining files still referencing removed fixture helpers
- Test: `src/Arius.Core.Tests/Arius.Core.Tests.csproj`
- Test: `src/Arius.Integration.Tests/Arius.Integration.Tests.csproj`
- Test: `src/Arius.E2E.Tests/Arius.E2E.Tests.csproj`

- [ ] **Step 1: Write the failing test/search gate**

Before cleanup, identify any remaining references to the old helper APIs and manual join helper.

Search targets:

```text
WriteFile(
ReadRestored(
RestoredExists(
WriteRandomFile(
CombineValidatedRelativePath(
Path.Combine(... LocalRoot ...)
Path.Combine(... RestoreRoot ...)
```

Use repo search and convert remaining intended call sites.

- [ ] **Step 2: Verify the old APIs are still referenced before removing them completely**

Run read-only searches:

```bash
rg "WriteFile\(|ReadRestored\(|RestoredExists\(|WriteRandomFile\(|CombineValidatedRelativePath" src
```

Expected: remaining references are visible and must be migrated or intentionally kept.

- [ ] **Step 3: Write minimal implementation**

- remove the old fixture wrapper methods entirely once all callers are migrated
- if `WriteRandomFile` still exists only in one place, inline it there instead of preserving the abstraction
- ensure no remaining custom root/path combine helper exists in fixtures
- keep string `LocalRoot` / `RestoreRoot` only where command/config/foreign APIs require strings

- [ ] **Step 4: Run full relevant verification**

Run:
```bash
dotnet build "src/Arius.Core.Tests/Arius.Core.Tests.csproj" -p:UseAppHost=false
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" -p:UseAppHost=false
dotnet build "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" -p:UseAppHost=false
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" -p:UseAppHost=false
dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" -p:UseAppHost=false
dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" -p:UseAppHost=false
```

Expected:
- Core build/test PASS
- Integration build/test PASS
- E2E build PASS
- E2E runtime may fail only for known environment/auth/certificate issues, not for compile or path-helper regressions

- [ ] **Step 5: Commit**

```bash
git add src
git commit -m "refactor: align test fixtures with RelativeFileSystem"
```

## Notes for execution

- Prefer `RelativeFileSystem` for behavior and `LocalDirectory.Resolve(...)` for absolute path conversion. Do not add a new `Resolve(...)` method to `RelativeFileSystem`; the existing Core split is already the pattern to follow.
- Keep changes surgical. The goal is to remove redundant helper code added in the earlier `RelativePath` refactor, not to redesign test infrastructure.
- When converting string relative paths in tests, parse them once to `RelativePath` if the path is used more than once in the same block.
- Preserve existing `LocalRoot` / `RestoreRoot` string properties where command options or non-Arius APIs still require strings.
- `SyntheticRepositoryMaterializer.MaterializeV1Async(...)` and some disk-tree assertion helpers still take raw root strings. Do not widen this cleanup beyond the fixture/rooted path boundary unless a follow-up task is created.
- TDD applies per task: migrate a representative test first, watch it fail, then implement the shared change.
