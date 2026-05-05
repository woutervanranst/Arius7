# FileTree Path Typing Follow-Up Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove raw filesystem path strings from `FileTreePaths` and its production/test call sites by using `LocalRootPath` and `RootedPath` end-to-end.

**Architecture:** Keep deterministic hashed staging directory IDs as strings, but make every filesystem helper in `FileTreePaths` typed in both arguments and return values. Update production consumers first (`FileTreeStagingSession`, `FileTreeStagingWriter`, `FileTreeBuilder`, `FileTreeService`), then update filetree tests so the typed boundary is exercised directly.

**Tech Stack:** C# 14, .NET 10, TUnit, existing Arius path types (`RelativePath`, `LocalRootPath`, `RootedPath`)

---

### Task 1: Type `FileTreePaths` staging helpers and staging production callers

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreePaths.cs`
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeStagingSession.cs`
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs`

- [ ] **Step 1: Write the failing tests**

Add/adjust assertions so the staging helper boundary is typed instead of string-based.

```csharp
[Test]
public void GetStagingRootDirectory_ReturnsTypedLocalRootPath()
{
    var cacheRoot = LocalRootPath.Parse(Path.Combine(Path.GetTempPath(), $"arius-filetrees-{Guid.NewGuid():N}"));

    var stagingRoot = FileTreePaths.GetStagingRootDirectory(cacheRoot);

    stagingRoot.ShouldBe(cacheRoot / RelativePath.Parse(".staging")).Root;
}

[Test]
public void GetStagingNodePath_ReturnsTypedRootedPath()
{
    var stagingRoot = LocalRootPath.Parse(Path.Combine(Path.GetTempPath(), $"arius-staging-{Guid.NewGuid():N}"));

    var nodePath = FileTreePaths.GetStagingNodePath(stagingRoot, "abc123");

    nodePath.ShouldBe(stagingRoot / RelativePath.Parse("abc123"));
}
```

- [ ] **Step 2: Run the staging writer tests to verify they fail for the expected typed-boundary mismatch**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeStagingWriterTests/*"`

Expected: FAIL with compile/runtime fallout because `FileTreePaths` and callers still use string staging paths.

- [ ] **Step 3: Write the minimal typed staging implementation**

Update `FileTreePaths` so staging filesystem helpers accept typed roots and return typed paths.

```csharp
public static RootedPath GetStagingNodePath(LocalRootPath stagingRoot, string directoryId)
    => stagingRoot / RelativePath.Parse(directoryId);

public static LocalRootPath GetStagingRootDirectory(LocalRootPath fileTreeCacheDirectory)
    => LocalRootPath.Parse(Path.Combine(fileTreeCacheDirectory.ToString(), StagingDirectoryName));

public static RootedPath GetStagingLockPath(LocalRootPath fileTreeCacheDirectory)
    => fileTreeCacheDirectory / RelativePath.Parse(LockFileName);
```

Update staging production callers to stay typed until the final BCL boundary.

```csharp
var fileTreeCacheRoot = LocalRootPath.Parse(fileTreeCacheDirectory);
var lockPath = FileTreePaths.GetStagingLockPath(fileTreeCacheRoot);
lockStream = new FileStream(lockPath.FullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, useAsync: true);

var stagingRoot = FileTreePaths.GetStagingRootDirectory(fileTreeCacheRoot);
if (stagingRoot.ExistsDirectory)
    stagingRoot.DeleteDirectory(recursive: true);

stagingRoot.CreateDirectory();
```

```csharp
var nodePath = FileTreePaths.GetStagingNodePath(_stagingRoot, directoryId);
await AppendLineAsync(nodePath, FileTreeSerializer.SerializePersistedFileEntryLine(entry), cancellationToken);

private async Task AppendLineAsync(RootedPath path, string line, CancellationToken cancellationToken)
{
    var nodeLock = _lockStripes[(uint)StringComparer.Ordinal.GetHashCode(path.FullPath) % (uint)_lockStripes.Length];
    await nodeLock.WaitAsync(cancellationToken);

    try
    {
        _stagingRoot.CreateDirectory();
        await File.AppendAllLinesAsync(path.FullPath, [line], cancellationToken);
    }
    finally
    {
        nodeLock.Release();
    }
}
```

- [ ] **Step 4: Run the staging writer tests to verify they pass**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeStagingWriterTests/*"`

Expected: PASS

- [ ] **Step 5: Commit the staging helper slice**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreePaths.cs src/Arius.Core/Shared/FileTree/FileTreeStagingSession.cs src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs
git commit -m "refactor: type filetree staging paths"
```

### Task 2: Type `FileTreeBuilder` staging reads

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`

- [ ] **Step 1: Write the failing test adjustment**

Update the builder test setup to write typed staging node paths instead of string paths.

```csharp
await File.WriteAllLinesAsync(FileTreePaths.GetStagingNodePath(stagingRoot, directoryId).FullPath, lines);
```

- [ ] **Step 2: Run the builder tests to verify they fail for the old string path usage**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeBuilderTests/*"`

Expected: FAIL with compile fallout where `GetStagingNodePath(...)` still expects or returns string paths in builder-related code.

- [ ] **Step 3: Write the minimal builder implementation**

Keep the staging node path typed in `FileTreeBuilder` until the final file read boundary.

```csharp
async IAsyncEnumerable<string> ReadNodeLinesAsync(string directoryId, [EnumeratorCancellation] CancellationToken ct)
{
    var path = FileTreePaths.GetStagingNodePath(stagingRoot, directoryId);
    if (!path.ExistsFile)
        yield break;

    await foreach (var line in File.ReadLinesAsync(path.FullPath, ct))
        yield return line;
}
```

- [ ] **Step 4: Run the builder tests to verify they pass**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeBuilderTests/*"`

Expected: PASS

- [ ] **Step 5: Commit the builder slice**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs
git commit -m "refactor: type filetree builder staging reads"
```

### Task 3: Type filetree cache paths in `FileTreeService`

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreePaths.cs`
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeService.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs`

- [ ] **Step 1: Write the failing test adjustment**

Update one or more service tests to assert typed cache paths directly.

```csharp
var cacheRoot = LocalRootPath.Parse(cacheDir);
var diskPath = FileTreePaths.GetCachePath(cacheRoot, hash);

diskPath.ExistsFile.ShouldBeTrue();
```

- [ ] **Step 2: Run the filetree service tests to verify they fail before the cache-path typing change**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeServiceTests/*"`

Expected: FAIL with compile fallout because cache path helpers and service code still depend on raw strings.

- [ ] **Step 3: Write the minimal typed cache implementation**

Change `FileTreePaths.GetCachePath(...)` to return `RootedPath`, store `_diskCacheDir` as `LocalRootPath`, and use typed file existence/read/write boundaries.

```csharp
public static RootedPath GetCachePath(LocalRootPath fileTreeCacheDirectory, FileTreeHash hash)
    => GetCachePath(fileTreeCacheDirectory, hash.ToString());

public static RootedPath GetCachePath(LocalRootPath fileTreeCacheDirectory, string hashText)
    => fileTreeCacheDirectory / RelativePath.Parse(hashText);
```

```csharp
_diskCacheDir = LocalRootPath.Parse(RepositoryPaths.GetFileTreeCacheDirectory(accountName, containerName));
_diskCacheDir.CreateDirectory();

var diskPath = FileTreePaths.GetCachePath(_diskCacheDir, hashText);
if (TryReadCachedTree(diskPath, cancellationToken) is { } cachedTree)
    return cachedTree;
```

```csharp
IReadOnlyList<FileTreeEntry>? TryReadCachedTree(RootedPath diskPath, CancellationToken cancellationToken)
{
    if (!diskPath.ExistsFile)
        return null;

    try
    {
        var cached = File.ReadAllBytes(diskPath.FullPath);
        if (cached.Length == 0)
            return null;

        try
        {
            return FileTreeSerializer.Deserialize(cached);
        }
        catch (Exception)
        {
            try { diskPath.DeleteFile(); } catch { }
        }
    }
    catch (FileNotFoundException)
    {
    }
    catch (DirectoryNotFoundException)
    {
    }

    return null;
}
```

- [ ] **Step 4: Run the filetree service tests to verify they pass**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeServiceTests/*"`

Expected: PASS

- [ ] **Step 5: Commit the cache-path slice**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreePaths.cs src/Arius.Core/Shared/FileTree/FileTreeService.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs
git commit -m "refactor: type filetree cache paths"
```

### Task 4: Run broader filetree verification and finish the follow-up

**Files:**
- Modify: any test fallout files only if verification reveals typed-boundary misses

- [ ] **Step 1: Run focused filetree verification**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeStagingWriterTests/*"
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeBuilderTests/*"
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeServiceTests/*"
```

Expected: PASS on all three focused suites

- [ ] **Step 2: Run broader core verification**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj"`

Expected: PASS

- [ ] **Step 3: Run slopwatch if available**

Run: `dotnet tool run slopwatch analyze`

Expected: PASS, or the existing known tool-manifest missing-tool message if the repository still does not provide it

- [ ] **Step 4: Commit any final fallout fixes**

```bash
git add -A
git commit -m "test: finish filetree path typing follow-up"
```

## Self-Review

- Spec coverage: the plan covers the typed helper boundary, staging production callers, builder staging reads, service cache paths, and focused verification from the approved spec.
- Placeholder scan: no `TBD`/`TODO` implementation gaps remain in the task list.
- Type consistency: the plan consistently uses `LocalRootPath` for filetree cache/staging roots, `RootedPath` for returned filesystem paths, and keeps staging directory IDs as `string`.
