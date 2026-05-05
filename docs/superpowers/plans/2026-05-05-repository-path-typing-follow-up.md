# RepositoryPaths Typing Follow-Up Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Type the shared `RepositoryPaths` directory helpers to `LocalRootPath`, remove the public repository-name helper, and update direct callers to keep repository directories typed until real string-only boundaries.

**Architecture:** `RepositoryPaths` becomes a typed directory-boundary helper that returns only `LocalRootPath` values for repository, chunk-index, filetree, snapshot, and logs directories. Production and test callers are updated in small slices so shared services and CLI logging stop passing raw repository directory strings around unnecessarily.

**Tech Stack:** C# 14, .NET 10, TUnit, existing Arius path types (`LocalRootPath`, `RootedPath`, `RelativePath`)

---

### Task 1: Type `RepositoryPaths` and its direct tests

**Files:**
- Modify: `src/Arius.Core/Shared/RepositoryPaths.cs`
- Modify: `src/Arius.Core.Tests/Shared/RepositoryPathsTests.cs`

- [ ] **Step 1: Write the failing test**

Replace the string-based assertions with typed directory assertions and remove the public `GetRepoDirectoryName(...)` expectation.

```csharp
[Test]
public void RepositoryDirectories_AreDerivedUnderUserProfile()
{
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var root = LocalRootPath.Parse(Path.Combine(home, ".arius", "account-container"));

    RepositoryPaths.GetRepositoryDirectory("account", "container").ShouldBe(root);
    RepositoryPaths.GetChunkIndexCacheDirectory("account", "container").ShouldBe(LocalRootPath.Parse(Path.Combine(root.ToString(), "chunk-index")));
    RepositoryPaths.GetFileTreeCacheDirectory("account", "container").ShouldBe(LocalRootPath.Parse(Path.Combine(root.ToString(), "filetrees")));
    RepositoryPaths.GetSnapshotCacheDirectory("account", "container").ShouldBe(LocalRootPath.Parse(Path.Combine(root.ToString(), "snapshots")));
    RepositoryPaths.GetLogsDirectory("account", "container").ShouldBe(LocalRootPath.Parse(Path.Combine(root.ToString(), "logs")));
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RepositoryPathsTests/*"`

Expected: FAIL with compile fallout because `RepositoryPaths` still returns `string` values and still exposes the old public helper shape.

- [ ] **Step 3: Write the minimal implementation**

Change the public directory helpers to return `LocalRootPath` and remove the public `GetRepoDirectoryName(...)` method.

```csharp
public static class RepositoryPaths
{
    public static LocalRootPath GetRepositoryDirectory(string accountName, string containerName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return LocalRootPath.Parse(Path.Combine(home, ".arius", $"{accountName}-{containerName}"));
    }

    public static LocalRootPath GetChunkIndexCacheDirectory(string accountName, string containerName)
        => LocalRootPath.Parse(Path.Combine(GetRepositoryDirectory(accountName, containerName).ToString(), "chunk-index"));

    public static LocalRootPath GetFileTreeCacheDirectory(string accountName, string containerName)
        => LocalRootPath.Parse(Path.Combine(GetRepositoryDirectory(accountName, containerName).ToString(), "filetrees"));

    public static LocalRootPath GetSnapshotCacheDirectory(string accountName, string containerName)
        => LocalRootPath.Parse(Path.Combine(GetRepositoryDirectory(accountName, containerName).ToString(), "snapshots"));

    public static LocalRootPath GetLogsDirectory(string accountName, string containerName)
        => LocalRootPath.Parse(Path.Combine(GetRepositoryDirectory(accountName, containerName).ToString(), "logs"));
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RepositoryPathsTests/*"`

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Shared/RepositoryPaths.cs src/Arius.Core.Tests/Shared/RepositoryPathsTests.cs
git commit -m "refactor: type repository paths"
```

### Task 2: Type shared-service repository directory callers

**Files:**
- Modify: `src/Arius.Core/Shared/ChunkIndex/ChunkIndexService.cs`
- Modify: `src/Arius.Core/Shared/Snapshot/SnapshotService.cs`
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeService.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs`
- Test: `src/Arius.Integration.Tests/ChunkIndex/ChunkIndexServiceIntegrationTests.cs`

- [ ] **Step 1: Write the failing test adjustments**

Update service tests to keep repository/cache directories typed.

```csharp
var cacheDir = RepositoryPaths.GetFileTreeCacheDirectory(acct, cont);
var diskPath = FileTreePaths.GetCachePath(cacheDir, hash);
diskPath.ExistsFile.ShouldBeTrue();
```

```csharp
var l2Dir = RepositoryPaths.GetChunkIndexCacheDirectory(Account, containerName);
var l2Path = l2Dir / RelativePath.Parse(prefix);
l2Path.ExistsFile.ShouldBeTrue();
```

- [ ] **Step 2: Run the focused tests to verify they fail**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeServiceTests/*"
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/ChunkIndexServiceIntegrationTests/*"
```

Expected: FAIL with compile fallout where shared services still store repository cache directories as strings.

- [ ] **Step 3: Write the minimal implementation**

Store repository directories as `LocalRootPath` in shared services and only cross to strings at true BCL boundaries.

```csharp
private readonly LocalRootPath _l2Dir;

_l2Dir = RepositoryPaths.GetChunkIndexCacheDirectory(accountName, containerName);
_l2Dir.CreateDirectory();
```

```csharp
private readonly LocalRootPath _diskCacheDir;

_diskCacheDir = GetDiskCacheDirectory(accountName, containerName);
_diskCacheDir.CreateDirectory();

public static LocalRootPath GetDiskCacheDirectory(string accountName, string containerName)
    => RepositoryPaths.GetSnapshotCacheDirectory(accountName, containerName);
```

Keep `FileTreeService` typed consistently with the already-typed `FileTreePaths` cache helpers.

- [ ] **Step 4: Run the focused tests to verify they pass**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeServiceTests/*"
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/ChunkIndexServiceIntegrationTests/*"
```

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Shared/ChunkIndex/ChunkIndexService.cs src/Arius.Core/Shared/Snapshot/SnapshotService.cs src/Arius.Core/Shared/FileTree/FileTreeService.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs src/Arius.Integration.Tests/ChunkIndex/ChunkIndexServiceIntegrationTests.cs
git commit -m "refactor: type repository cache roots"
```

### Task 3: Type CLI, feature, and fixture callers

**Files:**
- Modify: `src/Arius.Cli/CliBuilder.cs`
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`
- Modify: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveTestEnvironment.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs`
- Modify: `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`
- Modify: `src/Arius.E2E.Tests/Fixtures/E2EFixture.cs`

- [ ] **Step 1: Write the failing test or compile-boundary adjustment**

Update direct callers to keep `LocalRootPath` until the last real string boundary.

```csharp
var logDir = RepositoryPaths.GetLogsDirectory(accountName, containerName);
logDir.CreateDirectory();

var logFile = (logDir / RelativePath.Parse($"{timestamp}_{commandName}.txt")).FullPath;
```

```csharp
var stagingCacheDirectory = RepositoryPaths.GetFileTreeCacheDirectory(_accountName, _containerName);
await using var session = await FileTreeStagingSession.OpenAsync(stagingCacheDirectory, cancellationToken);
```

- [ ] **Step 2: Run focused verification for direct callers**

Run the narrowest relevant test suites after the caller updates:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ArchiveCommand*/*"
dotnet test --project "src/Arius.Cli.Tests/Arius.Cli.Tests.csproj"
```

Expected: initial failures or compile fallout before implementation, then PASS after the changes.

- [ ] **Step 3: Write the minimal implementation**

Update direct callers and any signatures that only exist to forward repository root directories.

```csharp
public static Task<FileTreeStagingSession> OpenAsync(LocalRootPath fileTreeCacheDirectory, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();

    fileTreeCacheDirectory.CreateDirectory();

    var lockPath = FileTreePaths.GetStagingLockPath(fileTreeCacheDirectory);
    // ...
}
```

Keep fixture fields typed when they model repository directories rather than arbitrary strings.

- [ ] **Step 4: Re-run focused verification to confirm pass**

Run the same focused suites again.

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Cli/CliBuilder.cs src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveTestEnvironment.cs src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs src/Arius.E2E.Tests/Fixtures/E2EFixture.cs src/Arius.Core/Shared/FileTree/FileTreeStagingSession.cs
git commit -m "refactor: adopt typed repository roots"
```

### Task 4: Run broader verification and finish the follow-up

**Files:**
- Modify: only fallout files revealed by verification

- [ ] **Step 1: Run focused suites one more time**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RepositoryPathsTests/*"
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeServiceTests/*"
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/ChunkIndexServiceIntegrationTests/*"
dotnet test --project "src/Arius.Cli.Tests/Arius.Cli.Tests.csproj"
```

Expected: PASS on all suites.

- [ ] **Step 2: Run broader verification**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj"
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj"
```

Expected: PASS

- [ ] **Step 3: Run slopwatch if available**

Run: `dotnet tool run slopwatch analyze`

Expected: PASS, or the existing known missing-tool message if the tool still is not present in the repository manifest.

- [ ] **Step 4: Commit any final fallout fixes**

```bash
git add -A
git commit -m "test: finish repository path typing follow-up"
```

## Self-Review

- Spec coverage: the plan covers typed `RepositoryPaths`, removal of the public repo-name helper, direct shared-service callers, CLI/feature/fixture callers, and focused-plus-broader verification.
- Placeholder scan: all tasks include concrete files, commands, and intended code shape.
- Type consistency: the plan consistently uses `LocalRootPath` for directory-returning helpers and retains string conversion only at true filename or host-library boundaries.
