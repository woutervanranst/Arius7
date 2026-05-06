# Production Typed IO Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the remaining production raw line-based file IO, typed-path regressions in pointer-file composition, and raw tar temp-file open/delete handling from the core archive/restore/filetree pipeline.

**Architecture:** Extend the existing typed filesystem surface on `RootedPath` instead of adding new helper layers, add one narrow pointer-file derivation helper on `RelativePath`, then switch the remaining production consumers to those typed APIs. Keep host temp-file creation as the only acceptable raw OS-path boundary in this slice.

**Tech Stack:** C# 14, .NET 10, TUnit, Arius typed path model (`LocalRootPath`, `RootedPath`, `RelativePath`, `PathSegment`)

---

## File Map

- `src/Arius.Core/Shared/Paths/RootedPathFileSystemExtensions.cs`: add typed line-based file APIs
- `src/Arius.Core/Shared/Paths/RelativePath.cs`: add pointer-file derivation helper
- `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`: switch staged node line reading to typed APIs
- `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`: switch staged node line appends to typed APIs
- `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`: switch tar temp-file read/delete and pointer-file composition to typed APIs
- `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs`: switch pointer-file composition to typed APIs
- `src/Arius.Core.Tests/Shared/RootedPathTests.cs`: add failing tests for typed line-based file APIs
- `src/Arius.Core.Tests/Shared/RelativePathTests.cs`: add failing tests for pointer-file derivation
- `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`: adopt typed line-based setup helpers
- `src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs`: adopt typed line-based assertions
- `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs`: verify archive still writes pointer files and handles tar reruns
- `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`: verify restore still creates pointer files through typed paths

### Task 1: Add typed line-based file APIs on `RootedPath`

**Files:**
- Modify: `src/Arius.Core/Shared/Paths/RootedPathFileSystemExtensions.cs`
- Test: `src/Arius.Core.Tests/Shared/RootedPathTests.cs`

- [ ] **Step 1: Write the failing tests**

Add one focused test that uses the new line-oriented APIs end-to-end.

```csharp
[Test]
public async Task ReadLinesAsync_WriteAllLinesAsync_And_AppendAllLinesAsync_WorkAgainstFile()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), $"arius-rooted-lines-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempRoot);

    try
    {
        var root = RootOf(tempRoot);
        var path = root / PathOf("docs/lines.txt");
        (root / PathOf("docs")).CreateDirectory();

        await path.WriteAllLinesAsync(["alpha", "beta"]);
        await path.AppendAllLinesAsync(["gamma"]);

        var lines = await path.ReadLinesAsync().ToArrayAsync();

        lines.ShouldBe(["alpha", "beta", "gamma"]);
    }
    finally
    {
        if (Directory.Exists(tempRoot))
            Directory.Delete(tempRoot, recursive: true);
    }
}
```

- [ ] **Step 2: Run the focused tests to verify they fail**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RootedPathTests/*"`

Expected: FAIL because `RootedPath` does not yet expose the new line-based methods.

- [ ] **Step 3: Write the minimal implementation**

Add only the missing line-based members to `RootedPathFileSystemExtensions`.

```csharp
public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken = default)
    => File.ReadLinesAsync(path.FullPath, cancellationToken);

public Task WriteAllLinesAsync(IEnumerable<string> lines, CancellationToken cancellationToken = default)
    => File.WriteAllLinesAsync(path.FullPath, lines, cancellationToken);

public Task AppendAllLinesAsync(IEnumerable<string> lines, CancellationToken cancellationToken = default)
    => File.AppendAllLinesAsync(path.FullPath, lines, cancellationToken);
```

- [ ] **Step 4: Run the focused tests to verify they pass**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RootedPathTests/*"`

Expected: PASS

### Task 2: Add typed pointer-file derivation on `RelativePath`

**Files:**
- Modify: `src/Arius.Core/Shared/Paths/RelativePath.cs`
- Test: `src/Arius.Core.Tests/Shared/RelativePathTests.cs`

- [ ] **Step 1: Write the failing tests**

Add one focused test that pins the pointer-file derivation rule.

```csharp
[Test]
public void ToPointerFilePath_AppendsPointerSuffix()
{
    var path = RelativePath.Parse("docs/readme.txt");

    path.ToPointerFilePath().ShouldBe(RelativePath.Parse("docs/readme.txt.pointer.arius"));
}
```

- [ ] **Step 2: Run the focused tests to verify they fail**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RelativePathTests/*"`

Expected: FAIL because `RelativePath` does not yet expose `ToPointerFilePath()`.

- [ ] **Step 3: Write the minimal implementation**

Add a narrow helper to `RelativePath`.

```csharp
public RelativePath ToPointerFilePath() => Parse($"{Value}.pointer.arius");
```

- [ ] **Step 4: Run the focused tests to verify they pass**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RelativePathTests/*"`

Expected: PASS

### Task 3: Refactor filetree production code to use typed line IO

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs`

- [ ] **Step 1: Write the failing test or compile-boundary adjustment**

Update one filetree test helper and one staging-writer assertion site to the new typed APIs.

```csharp
private static Task WriteNodeLinesAsync(LocalRootPath stagingRoot, string directoryId, params string[] lines)
    => FileTreePaths.GetStagingNodePath(stagingRoot, directoryId).WriteAllLinesAsync(lines);
```

```csharp
var line = (await photosPath.ReadLinesAsync().ToArrayAsync()).Single();
```

- [ ] **Step 2: Run focused verification to confirm it fails or exposes production fallout**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeBuilderTests/*"
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeStagingWriterTests/*"
```

Expected: any initial fallout is limited to missing typed line APIs or the remaining raw production call sites.

- [ ] **Step 3: Write the minimal implementation**

Switch production filetree code to the typed APIs.

```csharp
await foreach (var line in path.ReadLinesAsync(ct))
    yield return line;
```

```csharp
await path.AppendAllLinesAsync([line], cancellationToken);
```

Then update filetree tests to use the typed line methods instead of raw `.FullPath` BCL calls.

- [ ] **Step 4: Run focused verification to confirm it passes**

Run the same focused suites again.

Expected: PASS

### Task 4: Refactor archive and restore pointer-file creation to stay typed

**Files:**
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`
- Modify: `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs`
- Test: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs`
- Test: `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`

- [ ] **Step 1: Write the failing test or compile-boundary adjustment**

Add one direct assertion using the new helper in restore tests.

```csharp
var pointerPath = fixture.RestoreRoot / PathOf(relativePath).ToPointerFilePath();
```

Use the same pattern in one archive-side assertion if needed.

- [ ] **Step 2: Run focused verification to confirm it fails or exposes fallout**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ArchiveRecoveryTests/*"
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/*"
```

Expected: initial fallout is limited to the missing helper or the remaining string-based production composition.

- [ ] **Step 3: Write the minimal implementation**

Replace ad hoc string reparsing in archive and restore.

```csharp
var pointerPath = (opts.RootDirectory / relativePath.ToPointerFilePath());
```

```csharp
var pointerPath = file.RelativePath.ToPointerFilePath().RootedAt(opts.RootDirectory);
```

- [ ] **Step 4: Run focused verification to confirm it passes**

Run the same focused suites again.

Expected: PASS

### Task 5: Refactor archive tar temp-file open/delete handling to typed paths

**Files:**
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`
- Test: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs`

- [ ] **Step 1: Write the failing test or compile-boundary adjustment**

Change the tar temp-path carrier in production from raw string to typed rooted path after creation, then let the compile fallout identify remaining raw usages.

Example target shape:

```csharp
RootedPath? currentTarPath = null;
```

- [ ] **Step 2: Run focused verification to confirm it fails**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ArchiveRecoveryTests/*"`

Expected: FAIL from the partial conversion until open/read/delete call sites are updated.

- [ ] **Step 3: Write the minimal implementation**

Keep temp-file creation at the OS boundary, then parse once and stay typed.

```csharp
currentTarPath = LocalRootPath.Parse(Path.GetTempPath())
    .GetRelativePath(Path.GetTempFileName())
    .RootedAt(LocalRootPath.Parse(Path.GetPathRoot(Path.GetTempPath())!));
```

Use the repo’s actual simplest valid equivalent when implementing. After the temp path is typed:

```csharp
await using (var fs = currentTarPath.Value.OpenRead())
{
    tarHash = ChunkHash.Parse(await _encryption.ComputeHashAsync(fs, cancellationToken));
}
```

```csharp
await using var fs = sealed_.TarFilePath.OpenRead();
```

```csharp
try { sealed_.TarFilePath.DeleteFile(); } catch { /* ignore */ }
```

Also update any local sealed-tar carrier type as needed so `TarFilePath` becomes `RootedPath` rather than `string`.

- [ ] **Step 4: Run focused verification to confirm it passes**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ArchiveRecoveryTests/*"`

Expected: PASS

### Task 6: Broader verification and slopwatch

**Files:**
- Modify: only immediate fallout files caused by earlier tasks

- [ ] **Step 1: Run broader verification**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj"
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj"
dotnet test --project "src/Arius.Cli.Tests/Arius.Cli.Tests.csproj"
dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --no-restore
slopwatch analyze
```

Expected: PASS, with only the known recover-script integration skips.

## Self-Review

- Spec coverage: the plan covers all approved slice areas: typed line IO, pointer-file derivation, filetree consumers, tar temp-file handling, and verification.
- Placeholder scan: file paths, test shapes, commands, and target code shapes are concrete.
- Type consistency: `RootedPath` remains the typed IO carrier, `RelativePath` owns pointer-file derivation, and raw temp-file creation is preserved only at the host boundary.
