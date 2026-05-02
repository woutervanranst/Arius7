# Filetree Mutation Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Improve filetree-domain mutation resistance until the remaining Stryker survivors are equivalent or low-value.

**Architecture:** Run Stryker against the existing `Arius.Core` scope, isolate survivors in `src/Arius.Core/Shared/FileTree/*`, and close them in small TDD cycles. Prefer test-only assertions when they describe intended behavior already, and allow minimal production hardening only when a mutant exposes a real behavioral gap.

**Tech Stack:** .NET, TUnit, Stryker.NET, Arius.Core filetree services and serializers

---

## File Map

- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs`
  Purpose: add focused regression tests for canonical parsing and serialization survivors.
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`
  Purpose: add focused regression tests for staged-node and builder invariants.
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs`
  Purpose: add focused regression tests for cache publication and validation semantics.
- Modify if required: `src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs`
  Purpose: minimal hardening only if serializer survivors reveal a real behavioral gap.
- Modify if required: `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`
  Purpose: minimal hardening only if builder survivors reveal a real behavioral gap.
- Modify if required: `src/Arius.Core/Shared/FileTree/FileTreeService.cs`
  Purpose: minimal hardening only if service survivors reveal a real behavioral gap.

### Task 1: Capture Baseline Survivors

**Files:**
- Modify: none
- Test: `src/Arius.Core.Tests/Arius.Core.Tests.csproj`

- [ ] **Step 1: Run baseline filetree mutation analysis**

```bash
dotnet stryker --config-file stryker-config.json
```

Expected: Stryker completes and writes the HTML report under `StrykerOutput/`.

- [ ] **Step 2: Record filetree-domain survivors from the Stryker report**

Focus only on survivors under these files:

```text
src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs
src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs
src/Arius.Core/Shared/FileTree/FileTreeService.cs
src/Arius.Core/Shared/FileTree/FileTreePaths.cs
src/Arius.Core/Shared/FileTree/FileTreeStagingSession.cs
src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs
```

- [ ] **Step 3: Group the next logical survivor cluster**

Pick the smallest meaningful group, for example:

```text
Serializer line parsing accepts a non-canonical name.
Builder duplicate-directory handling is only partially asserted.
FileTreeService cache publication leaves a weak observable invariant.
```

- [ ] **Step 4: Commit nothing yet**

```bash
git status --short
```

Expected: no code changes from Task 1.

### Task 2: Kill Serializer Survivors

**Files:**
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs`
- Modify if required: `src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs`
- Test: `src/Arius.Core.Tests/Arius.Core.Tests.csproj`

- [ ] **Step 1: Write the failing serializer regression test**

Add a focused test near the existing parser coverage, for example:

```csharp
[Test]
public void ParseStagedNodeEntryLine_DirectoryLineWithNonCanonicalName_Throws()
{
    var directoryId = FileTreePaths.GetStagingDirectoryId("photos");

    Should.Throw<FormatException>(() =>
        FileTreeSerializer.ParseStagedNodeEntryLine($"{directoryId} D photos//"));
}
```

- [ ] **Step 2: Run the focused test and verify red**

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeSerializerTests/ParseStagedNodeEntryLine_DirectoryLineWithNonCanonicalName_Throws"
```

Expected: FAIL if the current behavior accepts the mutant, or PASS if that survivor was already covered and a different serializer survivor should be selected.

- [ ] **Step 3: Write the minimal implementation only if the test fails for the expected reason**

Keep the change surgical inside `FileTreeSerializer`, for example tightening an existing guard rather than adding a new abstraction:

```csharp
if (!name.EndsWith("/", StringComparison.Ordinal)
    || name.Length == 1
    || name.Contains('\\')
    || name[..^1].Contains('/')
    || name is "./" or "../"
    || string.IsNullOrWhiteSpace(name[..^1]))
{
    throw new FormatException($"Invalid staged directory entry (non-canonical name): '{line}'");
}
```

- [ ] **Step 4: Run the focused serializer class and verify green**

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeSerializerTests/*"
```

Expected: PASS for the serializer class.

- [ ] **Step 5: Commit the single logical serializer change**

```bash
git add src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs
git commit -m "test: harden filetree serializer mutation coverage"
```

If only the test file changed, omit the production file from `git add`.

### Task 3: Kill Builder Survivors

**Files:**
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`
- Modify if required: `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`
- Test: `src/Arius.Core.Tests/Arius.Core.Tests.csproj`

- [ ] **Step 1: Write the failing builder regression test**

Add a focused test for one surviving builder invariant, for example duplicate staged directories with different ids:

```csharp
[Test]
public async Task SynchronizeAsync_ConflictingDuplicateStagedDirectoryEntries_Throws()
{
    const string accountName = "acct-builder-conflict";
    const string containerName = "cont-builder-conflict";
    var cacheDir = RepositoryPaths.GetFileTreeCacheDirectory(accountName, containerName);
    if (Directory.Exists(cacheDir))
        Directory.Delete(cacheDir, recursive: true);

    try
    {
        var blobs = new FakeRecordingBlobContainerService();
        var builder = CreateBuilder(blobs, accountName, containerName, out var fileTreeService);
        await fileTreeService.ValidateAsync();
        await using var stagingSession = await FileTreeStagingSession.OpenAsync(cacheDir);

        var rootDirectoryId = FileTreePaths.GetStagingDirectoryId(string.Empty);
        await WriteNodeLinesAsync(
            stagingSession.StagingRoot,
            rootDirectoryId,
            $"{new string('a', 64)} D photos/",
            $"{new string('b', 64)} D photos/");

        await Should.ThrowAsync<InvalidOperationException>(() => builder.SynchronizeAsync(stagingSession.StagingRoot));
    }
    finally
    {
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);
    }
}
```

- [ ] **Step 2: Run the focused builder test and verify red**

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderTests/SynchronizeAsync_ConflictingDuplicateStagedDirectoryEntries_Throws"
```

Expected: FAIL if this captures the current survivor.

- [ ] **Step 3: Write the minimal implementation only if required**

Keep the fix inside the existing duplicate-directory guard, for example:

```csharp
if (directoryEntries.TryGetValue(stagedDirectoryEntry.Name, out var existingDirectoryEntry))
{
    if (!string.Equals(existingDirectoryEntry.DirectoryNameHash, stagedDirectoryEntry.DirectoryNameHash, StringComparison.Ordinal))
        throw new InvalidOperationException($"Conflicting staged directory entry '{stagedDirectoryEntry.Name}'.");

    break;
}
```

- [ ] **Step 4: Run the focused builder class and verify green**

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderTests/*"
```

Expected: PASS for the builder class.

- [ ] **Step 5: Commit the single logical builder change**

```bash
git add src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs
git commit -m "test: harden filetree builder mutation coverage"
```

If only the test file changed, omit the production file from `git add`.

### Task 4: Kill Service Survivors

**Files:**
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs`
- Modify if required: `src/Arius.Core/Shared/FileTree/FileTreeService.cs`
- Test: `src/Arius.Core.Tests/Arius.Core.Tests.csproj`

- [ ] **Step 1: Write the failing service regression test**

Add a focused test for the next surviving service invariant, for example marker-file semantics:

```csharp
[Test]
public async Task ValidateAsync_SnapshotMismatch_CreatesMarkerFilesForRemoteFileTrees()
{
    const string acct = "tc-validate-markers";
    const string cont = "container";
    var (svc, blobs, cacheDir, snapshotsDir) = MakeService(acct, cont);
    try
    {
        blobs.SeedBlob($"snapshots/2026-05-02T10-00-00.0000000+00-00.json", [1, 2, 3]);
        var hash = new string('a', 64);
        blobs.SeedBlob($"filetrees/{hash}", [4, 5, 6]);

        await svc.ValidateAsync();

        var markerPath = FileTreePaths.GetCachePath(cacheDir, hash);
        File.Exists(markerPath).ShouldBeTrue();
        (await File.ReadAllBytesAsync(markerPath)).ShouldBeEmpty();
    }
    finally
    {
        await CleanupAsync(cacheDir, snapshotsDir);
    }
}
```

- [ ] **Step 2: Run the focused service test and verify red**

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeServiceTests/ValidateAsync_SnapshotMismatch_CreatesMarkerFilesForRemoteFileTrees"
```

Expected: FAIL if this captures the current survivor.

- [ ] **Step 3: Write the minimal implementation only if required**

Keep the fix inside the existing validation or cache-publication path, for example:

```csharp
if (!File.Exists(diskPath))
{
    await File.WriteAllBytesAsync(diskPath, [], cancellationToken);
}
```

- [ ] **Step 4: Run the focused service class and verify green**

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeServiceTests/*"
```

Expected: PASS for the service class.

- [ ] **Step 5: Commit the single logical service change**

```bash
git add src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs src/Arius.Core/Shared/FileTree/FileTreeService.cs
git commit -m "test: harden filetree service mutation coverage"
```

If only the test file changed, omit the production file from `git add`.

### Task 5: Verify Full Test Suite And Mutation Improvement

**Files:**
- Modify: none unless a final survivor requires one more cycle
- Test: `src/Arius.Core.Tests/Arius.Core.Tests.csproj`

- [ ] **Step 1: Run the full Core test project**

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj
```

Expected: PASS.

- [ ] **Step 2: Run Stryker again**

```bash
dotnet stryker --config-file stryker-config.json
```

Expected: improved mutation score and a reduced set of survivors in the filetree domain.

- [ ] **Step 3: Either loop back for the next survivor cluster or classify the remainder**

Use this decision rule:

```text
If a remaining filetree survivor still points at a meaningful missing behavior assertion or a real bug, return to Task 2, 3, or 4.
If a remaining filetree survivor is equivalent or would require disproportionate complexity, record it as low-value and stop.
```

- [ ] **Step 4: Summarize the remaining filetree survivors explicitly**

Capture a short list such as:

```text
Equivalent mutant: guard inversion on an already-impossible typed-hash parse path.
Low-value mutant: defensive catch cleanup path whose only externally visible difference is temp-file deletion after a failed cache write.
```

- [ ] **Step 5: Leave the worktree clean except for intentional uncommitted follow-up work**

```bash
git status --short
```

Expected: clean, or only intentional changes for the next cycle.
