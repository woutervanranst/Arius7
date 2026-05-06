# Broad Typed Filesystem Sweep Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove remaining raw local-filesystem usage outside the typed wrapper layers across production, tests, shared test infrastructure, E2E helpers, and benchmarks while preserving explicit string boundaries.

**Architecture:** Extend the existing typed filesystem extension surface only where repeated caller-side raw usage still exists, then migrate the remaining production and non-production callers onto typed roots, typed paths, and typed IO. Keep true external boundaries such as persisted settings, output payloads, dataset declarations, and storage/blob-name strings unchanged.

**Tech Stack:** C# 14, .NET 10, TUnit, BenchmarkDotNet, Arius typed path model (`LocalRootPath`, `RootedPath`, `RelativePath`, `PathSegment`)

---

## File Map

- `src/Arius.Core/Shared/FileSystem/RootedPathExtensions.cs`: typed filesystem surface for file and directory operations; likely home for added directory-enumeration and atomic publish helpers.
- `src/Arius.Core/Shared/FileSystem/LocalRootPathExtensions.cs`: typed local-root directory operations; may need small helper additions if repeated root-level callers still use raw BCL APIs.
- `src/Arius.Core/Shared/FileSystem/FilePairEnumerator.cs`: remaining production raw filesystem enumeration site.
- `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`: remaining production local snapshot enumeration site.
- `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`: remaining production temp-file string boundary cleanup.
- `src/Arius.Core/Shared/FileTree/FileTreeService.cs`: optional atomic cache publish helper adoption if justified.
- `src/Arius.Tests.Shared/Helpers/FileSystemHelper.cs`: shared test infrastructure still uses raw directory enumeration.
- `src/Arius.Core.Tests/Shared/LocalFile/LocalFileEnumeratorTests.cs`: representative test fixture setup still uses raw string roots and raw file APIs.
- `src/Arius.Benchmarks/ArchiveStepBenchmarks.cs`: benchmark setup/cleanup still uses raw temp directory APIs.
- `src/Arius.Benchmarks/BenchmarkRunOptions.cs`: benchmark path option parsing still uses raw path/root discovery.

Additional likely touched files during execution:

- targeted tests under `src/Arius.Core.Tests/Shared/`, `src/Arius.Core.Tests/Features/`, `src/Arius.Integration.Tests/`, and `src/Arius.E2E.Tests/`
- benchmark support files if typed benchmark options need to flow further than `BenchmarkRunOptions`

### Task 1: Add the missing typed filesystem helpers for directory walking and atomic publish

**Files:**
- Modify: `src/Arius.Core/Shared/FileSystem/RootedPathExtensions.cs`
- Modify: `src/Arius.Core/Shared/FileSystem/LocalRootPathExtensions.cs` if root-level helpers are truly needed
- Test: `src/Arius.Core.Tests/Shared/RootedPathTests.cs`

- [ ] **Step 1: Write the failing tests**

Add focused tests that pin the exact helper surface needed by remaining callers.

```csharp
[Test]
public void EnumerateDirectories_ReturnsTypedChildDirectories()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), $"arius-rooted-enumerate-dirs-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempRoot);

    try
    {
        var root = RootOf(tempRoot);
        var docs = root / PathOf("docs");
        var photos = root / PathOf("photos");
        docs.CreateDirectory();
        photos.CreateDirectory();

        var directories = (root / RelativePath.Root)
            .EnumerateDirectories()
            .Select(path => path.RelativePath)
            .OrderBy(path => path.ToString(), StringComparer.Ordinal)
            .ToArray();

        directories.ShouldBe([PathOf("docs"), PathOf("photos")]);
    }
    finally
    {
        if (Directory.Exists(tempRoot))
            Directory.Delete(tempRoot, recursive: true);
    }
}

[Test]
public void EnumerateFileEntries_ReturnsTypedFilesWithMetadata()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), $"arius-rooted-enumerate-files-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempRoot);

    try
    {
        var root = RootOf(tempRoot);
        var file = root / PathOf("docs/readme.txt");
        (root / PathOf("docs")).CreateDirectory();
        file.WriteAllTextAsync("hello").GetAwaiter().GetResult();

        var entry = (root / PathOf("docs")).EnumerateFileEntries().Single();

        entry.Path.ShouldBe(file);
        entry.Length.ShouldBe(5);
        entry.Name.ShouldBe(PathSegment.Parse("readme.txt"));
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

Expected: FAIL because the typed enumeration helpers do not yet exist.

- [ ] **Step 3: Write the minimal implementation**

Add the smallest helper surface to `RootedPathExtensions` that removes repeated caller-side `DirectoryInfo` / `FileInfo` use.

```csharp
public IEnumerable<RootedPath> EnumerateDirectories(string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
{
    foreach (var fullPath in Directory.EnumerateDirectories(path.FullPath, searchPattern, searchOption))
        yield return path.Root.GetRelativePath(fullPath).RootedAt(path.Root);
}

public IEnumerable<RootedFileEntry> EnumerateFileEntries(string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
{
    foreach (var fullPath in Directory.EnumerateFiles(path.FullPath, searchPattern, searchOption))
    {
        var rootedPath = path.Root.GetRelativePath(fullPath).RootedAt(path.Root);
        yield return new RootedFileEntry(
            rootedPath,
            rootedPath.RelativePath.Name ?? throw new InvalidOperationException("Enumerated file must have a file name."),
            rootedPath.Length,
            rootedPath.CreationTimeUtc,
            rootedPath.LastWriteTimeUtc);
    }
}
```

If `FileTreeService` cleanup later proves a typed atomic publish helper is warranted, add it in this same wrapper layer rather than leaving a new raw caller-side helper elsewhere.

- [ ] **Step 4: Run the focused tests to verify they pass**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RootedPathTests/*"`

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Shared/FileSystem/RootedPathExtensions.cs src/Arius.Core/Shared/FileSystem/LocalRootPathExtensions.cs src/Arius.Core.Tests/Shared/RootedPathTests.cs
git commit -m "feat: add typed directory enumeration"
```

### Task 2: Remove remaining production raw directory/file enumeration

**Files:**
- Modify: `src/Arius.Core/Shared/FileSystem/FilePairEnumerator.cs`
- Modify: `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`
- Test: `src/Arius.Core.Tests/Shared/LocalFile/LocalFileEnumeratorTests.cs`
- Test: `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`

- [ ] **Step 1: Write the failing test or compile-boundary change**

Move one enumerator test helper and one list-query path through the new typed enumeration APIs.

```csharp
private RootedPath CreateFile(RelativePath relativePath, string? content = null)
{
    var fullPath = relativePath.RootedAt(_localRoot);
    var parent = fullPath.RelativePath.Parent;
    if (parent is not null)
        (_localRoot / parent.Value).CreateDirectory();
    fullPath.WriteAllTextAsync(content ?? "binary-data").GetAwaiter().GetResult();
    return fullPath;
}
```

Update one list-query test assertion to verify the returned local path still matches after typed enumeration refactoring.

- [ ] **Step 2: Run focused verification to confirm current fallout**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/LocalFileEnumeratorTests/*"
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/*"
```

Expected: FAIL or compile fallout limited to the remaining raw enumeration call sites.

- [ ] **Step 3: Write the minimal implementation**

Refactor `FilePairEnumerator` and `ListQueryHandler` to consume typed helpers instead of raw `DirectoryInfo` / `FileInfo`.

Representative implementation shape:

```csharp
foreach (var file in directory.EnumerateFileEntries())
{
    var relativePath = file.Path.RelativePath;
    // existing pointer/binary logic stays the same
}

foreach (var subdirectory in directory.EnumerateDirectories().OrderBy(path => path.RelativePath.Name?.ToString(), StringComparer.OrdinalIgnoreCase))
{
    foreach (var file in EnumerateFilesDepthFirst(subdirectory))
        yield return file;
}
```

```csharp
foreach (var directory in localDir.Value.EnumerateDirectories().OrderBy(path => path.RelativePath.Name?.ToString(), StringComparer.OrdinalIgnoreCase))
{
    var name = directory.RelativePath.Name?.ToString()
        ?? throw new InvalidOperationException("Enumerated local directory must have a name.");
    directories[name] = new LocalDirectoryState(name, directory);
}

foreach (var file in localDir.Value.EnumerateFileEntries().OrderBy(entry => entry.Name.ToString(), StringComparer.OrdinalIgnoreCase))
{
    var relativeFilePath = file.Path.RelativePath;
    // existing pointer/binary logic stays the same
}
```

- [ ] **Step 4: Run focused verification to confirm it passes**

Run the same focused suites again.

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Shared/FileSystem/FilePairEnumerator.cs src/Arius.Core/Features/ListQuery/ListQueryHandler.cs src/Arius.Core.Tests/Shared/LocalFile/LocalFileEnumeratorTests.cs src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs
git commit -m "refactor: use typed local filesystem enumeration"
```

### Task 3: Finish remaining production temp-file and atomic publish cleanup

**Files:**
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeService.cs` if a typed atomic publish helper proves justified
- Test: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs`

- [ ] **Step 1: Write the failing test or compile-boundary change**

Add one focused archive-side assertion that the temp-file flow stays typed after creation and one filetree cache publication assertion if an atomic helper is introduced.

```csharp
// Archive-side test helper uses typed temp rooted path operations only after temp file creation.
var pointerPath = fixture.LocalRoot / PathOf("docs/readme.txt").ToPointerFilePath();
pointerPath.ExistsFile.ShouldBeTrue();
```

If no new filetree atomic helper is needed, skip the extra test and keep the task limited to archive temp-file handling.

- [ ] **Step 2: Run focused verification to confirm current fallout**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ArchiveRecoveryTests/*"
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeServiceTests/*"
```

Expected: compile fallout or failing assertions limited to the remaining temp-file / atomic publish cleanup.

- [ ] **Step 3: Write the minimal implementation**

For `ArchiveCommandHandler`, keep the host temp-file creation boundary but stop decomposing the returned path into separate string parts more than once.

```csharp
var tempTarFile = RootedPath.Parse(Path.GetTempFileName());
currentTarPath = tempTarFile;
tarStream = currentTarPath.Value.Open(FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
```

If `FileTreeService` still needs caller-side `File.Replace` / `File.Move`, either:

- introduce a small wrapper-layer helper such as `ReplaceFileAtomically(RootedPath destination, RootedPath source)`, or
- explicitly keep the raw call localized there if the wrapper would add no real value

Do not add a generic abstraction unless the final code is clearly simpler.

- [ ] **Step 4: Run focused verification to confirm it passes**

Run the same focused suites again.

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs src/Arius.Core/Shared/FileTree/FileTreeService.cs src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs
git commit -m "refactor: keep temp file flows typed"
```

### Task 4: Sweep shared test infrastructure onto typed filesystem helpers

**Files:**
- Modify: `src/Arius.Tests.Shared/Helpers/FileSystemHelper.cs`
- Test: `src/Arius.Core.Tests/Shared/FileSystemHelperTests.cs`

- [ ] **Step 1: Write the failing test or compile-boundary change**

Update one shared-helper test to assert directory copy succeeds without caller-side raw directory enumeration.

```csharp
var sourceRoot = RootOf(sourceText);
var targetRoot = RootOf(targetText);

await FileSystemHelper.CopyDirectoryAsync(sourceRoot, targetRoot);

(targetRoot / PathOf("nested/file.txt")).ExistsFile.ShouldBeTrue();
```

- [ ] **Step 2: Run focused verification to confirm current fallout**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileSystemHelperTests/*"`

Expected: compile fallout or failing assertions limited to shared helper refactoring.

- [ ] **Step 3: Write the minimal implementation**

Replace raw `Directory.EnumerateDirectories(...)` use with typed directory enumeration.

```csharp
foreach (var directoryPath in (sourceRootPath / RelativePath.Root).EnumerateDirectories(searchOption: SearchOption.AllDirectories))
{
    var relativePath = directoryPath.RelativePath;
    (targetRootPath / relativePath).CreateDirectory();
}
```

Keep file copy on `CopyToAsync(...)` as it already is.

- [ ] **Step 4: Run focused verification to confirm it passes**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileSystemHelperTests/*"`

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Tests.Shared/Helpers/FileSystemHelper.cs src/Arius.Core.Tests/Shared/FileSystemHelperTests.cs
git commit -m "refactor: use typed directory copy helpers"
```

### Task 5: Sweep direct tests and E2E-style temp-root setup onto typed roots

**Files:**
- Modify: `src/Arius.Core.Tests/Shared/LocalFile/LocalFileEnumeratorTests.cs`
- Modify: selected touched files in `src/Arius.Core.Tests/`, `src/Arius.Integration.Tests/`, and `src/Arius.E2E.Tests/` that still model temp roots through raw strings

- [ ] **Step 1: Write the failing test or compile-boundary change**

Convert one representative test fixture from raw string temp-root setup to typed root setup.

```csharp
private readonly LocalRootPath _localRoot;

public LocalFileEnumeratorTests()
{
    _localRoot = RootOf(Path.Combine(Path.GetTempPath(), $"arius-enum-test-{Guid.NewGuid():N}"));
    _localRoot.CreateDirectory();
}

public void Dispose()
{
    if (_localRoot.ExistsDirectory)
        _localRoot.DeleteDirectory(recursive: true);
}
```

- [ ] **Step 2: Run focused verification to confirm current fallout**

Run targeted suites for the touched files, for example:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/LocalFileEnumeratorTests/*"
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/*/RestorePointerTimestampTests/*"
```

Expected: compile fallout or assertion drift limited to touched test setup.

- [ ] **Step 3: Write the minimal implementation**

Refactor touched tests to:

- store typed roots instead of raw temp-root strings
- create files through typed rooted paths
- use typed existence/time APIs instead of direct `File.*` / `Directory.*`
- keep raw strings only for intentional textual-path assertions or dataset declarations

Representative helper shape:

```csharp
private RootedPath CreateFile(RelativePath relativePath, string? content = null)
{
    var fullPath = relativePath.RootedAt(_localRoot);
    var parent = fullPath.RelativePath.Parent;
    if (parent is not null)
        (_localRoot / parent.Value).CreateDirectory();
    fullPath.WriteAllTextAsync(content ?? "binary-data").GetAwaiter().GetResult();
    return fullPath;
}
```

- [ ] **Step 4: Run focused verification to confirm it passes**

Run the same focused suites again.

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core.Tests/Shared/LocalFile/LocalFileEnumeratorTests.cs src/Arius.Integration.Tests src/Arius.E2E.Tests
git commit -m "test: adopt typed temp roots"
```

### Task 6: Sweep benchmarks onto typed roots and typed path options

**Files:**
- Modify: `src/Arius.Benchmarks/ArchiveStepBenchmarks.cs`
- Modify: `src/Arius.Benchmarks/BenchmarkRunOptions.cs`
- Modify: any benchmark support files that consume those option types
- Test/Verify: benchmark project build

- [ ] **Step 1: Write the failing test or compile-boundary change**

Since benchmark projects often lack direct unit tests, start by changing the option/result types to typed paths and then build the benchmark project to expose fallout.

```csharp
internal sealed record BenchmarkRunOptions(
    LocalRootPath RepositoryRoot,
    LocalRootPath RawOutputRoot,
    RootedPath TailLogPath)
```

Use typed temp-root setup in `ArchiveStepBenchmarks`.

```csharp
_preparedSourceRoot = RootOf(Path.Combine(Path.GetTempPath(), "arius", $"benchmark-source-{Guid.NewGuid():N}"));
_preparedSourceRoot.Value.CreateDirectory();
```

- [ ] **Step 2: Run build verification to expose fallout**

Run: `dotnet build "src/Arius.Benchmarks/Arius.Benchmarks.csproj"`

Expected: compile failures pinpointing remaining benchmark string-path consumers.

- [ ] **Step 3: Write the minimal implementation**

Refactor benchmark setup/cleanup and option parsing to typed paths as early as practical.

Representative implementation shape:

```csharp
var defaultBenchmarkRoot = RootedPath.Parse(Path.Combine(repositoryRoot.ToString(), "src", "Arius.Benchmarks")).Root;
var defaultRawOutputRoot = defaultBenchmarkRoot.GetSubdirectoryRoot(PathSegment.Parse("raw"));
var tailLogPath = RelativePath.Parse("benchmark-tail.md").RootedAt(defaultBenchmarkRoot);

return new(
    LocalRootPath.Parse(repositoryRoot),
    LocalRootPath.Parse(Path.GetFullPath(rawOutputRoot)),
    RootedPath.Parse(Path.GetFullPath(tailLogPath)));
```

Keep only true command-line string boundaries at parse time.

- [ ] **Step 4: Run build verification to confirm it passes**

Run: `dotnet build "src/Arius.Benchmarks/Arius.Benchmarks.csproj"`

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Benchmarks
git commit -m "refactor: type benchmark filesystem paths"
```

### Task 7: Structural grep verification and full sequential validation

**Files:**
- No code changes expected unless verification reveals overlooked call sites

- [ ] **Step 1: Run structural grep verification**

Run the equivalent of these checks and inspect the remaining hits manually:

```bash
rg "\b(File|Directory)\." src
rg "DirectoryInfo|FileInfo" src
rg "Path\.Combine|Path\.Join|Path\.Get(FileName|DirectoryName|Extension|FullPath)|Path\.GetTempPath|Path\.GetTempFileName|Path\.IsPathRooted" src
```

Expected: remaining matches are confined to typed wrapper/path implementation files or explicit allowed boundaries such as persisted settings, CLI/progress payloads, dataset declarations, or storage/blob-name string handling.

- [ ] **Step 2: If structural verification exposes missed local-filesystem callers, fix them before broad verification**

No placeholder work here: any newly discovered caller should be fixed immediately using the same typed-helper rules from earlier tasks, then re-run the grep checks.

- [ ] **Step 3: Run full sequential verification**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj"
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj"
dotnet test --project "src/Arius.Cli.Tests/Arius.Cli.Tests.csproj"
dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --no-restore
dotnet build "src/Arius.Benchmarks/Arius.Benchmarks.csproj"
slopwatch analyze
```

Expected: PASS, aside from known/skipped environment-gated integration tests.

- [ ] **Step 4: Commit the verification-complete sweep**

```bash
git add src docs
git commit -m "refactor: complete typed filesystem sweep"
```

## Self-Review

- Spec coverage: the plan covers typed helper additions, remaining production callers, shared test infrastructure, direct tests/E2E temp-root cleanup, benchmark cleanup, grep verification, and sequential behavioral verification.
- Placeholder scan: each task names concrete files, concrete code shapes, and concrete commands.
- Type consistency: the plan consistently uses `LocalRootPath` for roots, `RootedPath` for specific filesystem entries, and preserves textual boundaries only where the spec explicitly allowed them.
