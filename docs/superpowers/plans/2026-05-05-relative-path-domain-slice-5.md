# Relative Path Domain Slice 5 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Retype shared test, fixture, and E2E helper APIs so repository-relative values and local roots stay typed through test infrastructure instead of repeatedly reparsing strings.

**Architecture:** This slice moves path semantics to the owning shared test boundaries. `RepositoryTestFixture` becomes the main typed repository/local-root helper for tests, `SyntheticRepositoryMaterializer` parses dataset paths into typed values at the materializer boundary, and repeated test-local string normalization is replaced by typed helper APIs centered on `src/Arius.Tests.Shared/Paths/PathsHelper.cs`.

**Tech Stack:** C# / .NET / TUnit

---

### File Structure

**Modify**
- `src/Arius.Tests.Shared/Paths/PathsHelper.cs`
- `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`
- `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryMaterializer.cs`
- `src/Arius.E2E.Tests/Fixtures/E2EFixture.cs`
- `src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs`
- `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`
- `src/Arius.Integration.Tests/Pipeline/*.cs` callers of `WriteFile(...)`, `ReadRestored(...)`, `RestoredExists(...)`
- `src/Arius.E2E.Tests/Workflows/Steps/*.cs` callers of `SyntheticRepositoryMaterializer`
- `src/Arius.Benchmarks/ArchiveStepBenchmarks.cs`
- `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`

**Potentially modify if compile fallout requires it**
- other tests that call `RepositoryTestFixture` helpers
- shared test helpers in `src/Arius.Tests.Shared/IO/` if a typed boundary is justified there

**Do not modify in this slice**
- unrelated production feature code
- production CLI / Explorer boundaries
- filetree persistence format

Reason: this slice finishes the path-domain migration in test infrastructure, not in unrelated production code.

---

### Task 1: Centralize Additional Test Path Ergonomics In `PathsHelper`

**Files:**
- Modify: `src/Arius.Tests.Shared/Paths/PathsHelper.cs`
- Test: one or more existing test files that currently need repeated manual typed path setup

- [ ] **Step 1: Write failing tests for the helper ergonomics you need next**

Prefer a narrow RED that proves a repeated test pattern can become typed without local helper duplication.

Representative shape if adding a rooted helper is justified:

```csharp
[Test]
public void RootedOf_ComposesRelativePathUnderTypedRoot()
{
    var rooted = RootOf("C:/repo") / PathOf("docs/readme.txt");

    rooted.RelativePath.ShouldBe(PathOf("docs/readme.txt"));
}
```

Do not add helpers speculatively. Only add focused helpers that slice 5 actually needs for repeated typed construction.

- [ ] **Step 2: Run tests to verify RED**

Run a focused command for the touched test file, for example:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/Path*Tests/*"
```

Expected:
- FAIL or compile break because the helper does not exist yet

- [ ] **Step 3: Add the minimal helper(s) to `PathsHelper.cs`**

Keep the file focused. Example shape if justified:

```csharp
public static RootedPath RootedOf(string root, string relativePath)
    => RootOf(root) / PathOf(relativePath);
```

Only add helpers the rest of this slice will actively use.

- [ ] **Step 4: Run tests to verify GREEN**

Run the same focused command used for RED.

Expected:
- PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Tests.Shared/Paths/PathsHelper.cs <touched-test-files>
git commit -m "test: extend shared path helper ergonomics"
```

---

### Task 2: Retype `RepositoryTestFixture` Repository-Relative Helper APIs

**Files:**
- Modify: `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`
- Test: `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`

- [ ] **Step 1: Write failing tests against typed fixture helper usage**

Update a focused restore test to use typed paths at the fixture boundary.

Representative updates:

```csharp
fixture.WriteFile(PathOf("docs/readme.txt"), content);
fixture.RestoredExists(PathOf("docs/readme.txt")).ShouldBeTrue();
fixture.ReadRestored(PathOf("docs/readme.txt")).ShouldBe(content);
```

If needed, add one narrow test that proves the fixture helper still rejects paths that escape the root after the API becomes typed.

- [ ] **Step 2: Run tests to verify RED**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/*"
```

Expected:
- FAIL or compile break because `RepositoryTestFixture` still expects string repository-relative inputs

- [ ] **Step 3: Change fixture helper signatures to typed repository-relative values**

Expected shape:

```csharp
public string WriteFile(RelativePath relativePath, byte[] content)
{
    var full = relativePath.RootedAt(LocalRootPath.Parse(LocalRoot)).FullPath;
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllBytes(full, content);
    return full;
}

public byte[] ReadRestored(RelativePath relativePath)
    => File.ReadAllBytes(relativePath.RootedAt(LocalRootPath.Parse(RestoreRoot)).FullPath);

public bool RestoredExists(RelativePath relativePath)
    => File.Exists(relativePath.RootedAt(LocalRootPath.Parse(RestoreRoot)).FullPath);
```

Prefer storing/using typed `LocalRootPath` fixture state instead of reparsing roots on every call if that can be done cleanly in this task.

- [ ] **Step 4: Run tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/*"
```

Expected:
- PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs
git commit -m "refactor: type repository test fixture path helpers"
```

---

### Task 3: Propagate Typed Fixture Helper Usage Through Integration And E2E Facades

**Files:**
- Modify: `src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs`
- Modify: `src/Arius.E2E.Tests/Fixtures/E2EFixture.cs`
- Modify: direct integration/E2E callers of fixture path helpers

- [ ] **Step 1: Write failing tests or compile-driven red step for typed facade usage**

Update a narrow integration test and/or E2E fixture caller to use typed `PathOf(...)` at the fixture facade boundary.

Representative updates:

```csharp
fix.WriteFile(PathOf("file.bin"), content);
fix.ReadRestored(PathOf("file.bin")).ShouldBe(content);
fix.RestoredExists(PathOf("file.bin")).ShouldBeTrue();
```

- [ ] **Step 2: Run tests to verify RED**

Run one focused integration suite that uses the fixture heavily, for example:
```bash
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RoundtripTests/*"
```

Expected:
- FAIL or compile break because the fixture facades still expose string signatures

- [ ] **Step 3: Retype facade methods and broad callers minimally**

Expected direction:

```csharp
public string WriteFile(RelativePath relativePath, byte[] content)
    => _repository.WriteFile(relativePath, content);

public byte[] ReadRestored(RelativePath relativePath)
    => _repository.ReadRestored(relativePath);

public bool RestoredExists(RelativePath relativePath)
    => _repository.RestoredExists(relativePath);
```

Then update direct callers to pass `PathOf(...)` or already-typed values.

- [ ] **Step 4: Run tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RoundtripTests/*|/*/*/RestoreDispositionTests/*"
```

Expected:
- PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs src/Arius.E2E.Tests/Fixtures/E2EFixture.cs <touched-integration-and-e2e-callers>
git commit -m "refactor: use typed paths in test fixture facades"
```

---

### Task 4: Retype `SyntheticRepositoryMaterializer` Boundary And Callers

**Files:**
- Modify: `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryMaterializer.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/MaterializeVersionStep.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs`
- Modify: `src/Arius.Benchmarks/ArchiveStepBenchmarks.cs`

- [ ] **Step 1: Write failing tests or compile-driven red step for typed materializer usage**

If there is no direct unit test for the materializer, use a focused compile-driven red step by updating one narrow E2E or benchmark caller to pass typed roots/paths.

Representative target shape:

```csharp
await SyntheticRepositoryMaterializer.MaterializeV1Async(
    definition,
    seed,
    RootOf(versionRootPath),
    encryption);
```

Within the materializer, path-bearing values should become typed as soon as dataset paths are consumed.

- [ ] **Step 2: Run tests to verify RED**

Run a focused E2E or compile-oriented command, for example:
```bash
dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/Canonical_Representative_Workflow_Runs_On_Supported_Backends*"
```

Expected:
- FAIL or compile break because the materializer and/or callers still use string root/path signatures

- [ ] **Step 3: Retype the materializer boundary and internal operations**

Expected direction:

```csharp
public static async Task<SyntheticRepositoryState> MaterializeV1Async(
    SyntheticRepositoryDefinition definition,
    int seed,
    LocalRootPath rootPath,
    IEncryptionService encryption)
```

And internally:

```csharp
var relativePath = RelativePath.Parse(file.Path);
await WriteFileAsync(rootPath, relativePath, bytes);
await using var stream = File.OpenRead(relativePath.RootedAt(rootPath).FullPath);
```

Keep dataset declaration text as string until it crosses the materializer boundary.

- [ ] **Step 4: Run tests to verify GREEN**

Run a focused E2E/materializer verification command appropriate to the touched callers.

Expected:
- PASS, or if Docker/backend availability blocks runtime coverage, the suite should skip cleanly with visible reasons

- [ ] **Step 5: Commit**

```bash
git add src/Arius.E2E.Tests/Datasets/SyntheticRepositoryMaterializer.cs src/Arius.E2E.Tests/Workflows/Steps/MaterializeVersionStep.cs src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs src/Arius.Benchmarks/ArchiveStepBenchmarks.cs
git commit -m "refactor: type synthetic repository materializer paths"
```

---

### Task 5: Remove Remaining Test-Local Path Normalization Helpers

**Files:**
- Modify: `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`
- Potentially modify: other helper-heavy test files found by compile fallout or grep

- [ ] **Step 1: Write failing tests or compile-driven red step around typed helper signatures**

Focus on helpers that still normalize path strings internally, such as:

```csharp
private static DirectoryEntry DirectoryEntryOf(string name, FileTreeHash hash) => new()
{
    Name = SegmentOf(name.TrimEnd('/')),
    FileTreeHash = hash
};
```

Change representative callers first so they pass typed values directly.

- [ ] **Step 2: Run tests to verify RED**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/*"
```

Expected:
- FAIL or compile break because helper signatures still assume string/slash normalization

- [ ] **Step 3: Retype the local helpers or replace them with `PathsHelper` usage**

Preferred direction:

```csharp
private static FileEntry FileEntryOf(PathSegment name, ContentHash hash) => new()
{
    Name = name,
    ContentHash = hash,
    Created = s_created,
    Modified = s_modified
};

private static DirectoryEntry DirectoryEntryOf(PathSegment name, FileTreeHash hash) => new()
{
    Name = name,
    FileTreeHash = hash
};
```

Update callers to use `SegmentOf(...)` at the boundary instead of relying on hidden trimming/normalization.

- [ ] **Step 4: Run tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/*"
```

Expected:
- PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs <other-touched-test-files>
git commit -m "test: remove local path normalization helpers"
```

---

### Task 6: Verify Slice 5 End-To-End

**Files:**
- Verify only:
  - `src/Arius.Tests.Shared/`
  - `src/Arius.E2E.Tests/Datasets/`
  - affected Core / integration / E2E test callers

- [ ] **Step 1: Run focused cross-suite regression coverage**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/*|/*/*/ListQueryHandlerTests/*"
```

Run:
```bash
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RoundtripTests/*|/*/*/RestoreDispositionTests/*"
```

Run:
```bash
dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/Canonical_Representative_Workflow_Runs_On_Supported_Backends*"
```

Expected:
- PASS, or E2E runtime skip only when the backend is unavailable for expected environment reasons

- [ ] **Step 2: Run the relevant full projects**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj"
```

Run:
```bash
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj"
```

Expected:
- PASS

- [ ] **Step 3: Run slopwatch**

Run:
```bash
slopwatch analyze
```

Expected:
- `0 issue(s) found`

---

### Notes For The Implementer

- Prefer retyping the owning helper signature over adding another parsing shim inside it.
- Reuse and extend `src/Arius.Tests.Shared/Paths/PathsHelper.cs` for test-only path ergonomics instead of scattering new local helpers.
- Keep dataset declaration text and parsing tests textual until the semantic path boundary.
- Keep OS-only helpers string-based unless they truly own repository/local-root semantics.
- Avoid mixing slash-trimming compatibility behavior into newly typed test helpers.

### Self-Review

**Spec coverage**
- shared path helper ergonomics centralized in `PathsHelper.cs`: Task 1.
- `RepositoryTestFixture` repository-relative helper APIs become typed: Task 2.
- integration/E2E fixture facades propagate typed usage: Task 3.
- `SyntheticRepositoryMaterializer` becomes typed at its semantic boundary: Task 4.
- test-local normalization helpers are removed or retyped: Task 5.
- multi-project verification and slopwatch evidence: Task 6.

**Placeholder scan**
- No `TODO` / `TBD` placeholders.
- Each task has exact files, commands, and expected results.
- Code steps include concrete signature shapes and caller examples.

**Type consistency**
- repository-relative values consistently become `RelativePath`.
- local-root values consistently become `LocalRootPath` or `RootedPath` where owned.
- `PathsHelper.cs` is consistently the shared home for test-only path construction ergonomics.
