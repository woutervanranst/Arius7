# Local Root And Rooted Paths Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Introduce `LocalRootPath` and `RootedPath`, make them the primary local-path types across archive, restore, and list, and replace host-path string helpers with typed rooted-path operations.

**Architecture:** Add two new local-path value objects beside `PathSegment` and `RelativePath`, then shift root-relative operations onto `LocalRootPath` / `RootedPath` ownership. Retype archive, restore, and list option boundaries to `LocalRootPath`, migrate their internals to carry `RootedPath` where a local filesystem address is semantically known, and finish by removing `RelativePath.ToPlatformPath(...)` and switching Explorer file-name display to `RelativePath.Name`.

**Tech Stack:** C# 13, .NET 10, Mediator, TUnit, WPF MVVM, System.CommandLine

---

## File Layout

- Create: `src/Arius.Core/Shared/Paths/LocalRootPath.cs`
  Purpose: absolute canonical local root boundary with host-path equality and root-owned relativization.
- Create: `src/Arius.Core/Shared/Paths/RootedPath.cs`
  Purpose: typed local filesystem address composed from `LocalRootPath` + `RelativePath`.
- Modify: `src/Arius.Core/Shared/Paths/RelativePath.cs`
  Purpose: add `RootedAt(LocalRootPath root)`, keep `Name`, and remove rooted-local-path ownership when callers have migrated.
- Modify: `src/Arius.Core/Shared/LocalFile/LocalFileEnumerator.cs`
  Purpose: enumerate from `LocalRootPath` and use rooted-path operations instead of `Path.Combine(root, relative)`.
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommand.cs`
  Purpose: type `ArchiveCommandOptions.RootDirectory` as `LocalRootPath`.
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`
  Purpose: migrate archive filesystem access to `RootedPath`.
- Modify: `src/Arius.Core/Features/RestoreCommand/RestoreCommand.cs`
  Purpose: type `RestoreOptions.RootDirectory` as `LocalRootPath`.
- Modify: `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs`
  Purpose: migrate restore filesystem access to `RootedPath`.
- Modify: `src/Arius.Core/Features/ListQuery/ListQuery.cs`
  Purpose: type `ListQueryOptions.LocalPath` as `LocalRootPath?`.
- Modify: `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`
  Purpose: carry `RootedPath?` in list recursion and starting-point state.
- Modify: `src/Arius.Cli/Commands/Archive/ArchiveVerb.cs`
  Purpose: parse CLI archive path into `LocalRootPath`.
- Modify: `src/Arius.Cli/Commands/Restore/RestoreVerb.cs`
  Purpose: parse CLI restore target into `LocalRootPath`.
- Modify: `src/Arius.Explorer/RepositoryExplorer/RepositoryExplorerViewModel.cs`
  Purpose: parse `Repository.LocalDirectoryPath` into `LocalRootPath` when constructing Core requests.
- Modify: `src/Arius.Explorer/RepositoryExplorer/FileItemViewModel.cs`
  Purpose: use `RelativePath.Name` instead of `Path.GetFileName(...)`.
- Modify: `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`
  Purpose: add typed root accessors/helpers for tests and fixtures.
- Modify: `src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs`
  Purpose: flow typed roots through integration helper defaults.
- Create: `src/Arius.Core.Tests/Shared/LocalRootPathTests.cs`
  Purpose: verify canonicalization, validation, no-existence requirement, and host equality.
- Create: `src/Arius.Core.Tests/Shared/RootedPathTests.cs`
  Purpose: verify composition, full-path rendering, and relativization round-trips.
- Modify: `src/Arius.Core.Tests/Shared/RelativePathTests.cs`
  Purpose: move rooted-path coverage to `RootedAt(...)` and retire `ToPlatformPath(...)` assertions.
- Modify: `src/Arius.Core.Tests/Shared/LocalFile/LocalFileEnumeratorTests.cs`
  Purpose: update enumerator entry point to `LocalRootPath`.
- Modify: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveTestEnvironment.cs`
  Purpose: construct typed archive roots in test helpers.
- Modify: `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`
  Purpose: use typed restore roots and new rooted-path assertions.
- Modify: `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`
  Purpose: use typed local roots and verify rooted starting-point traversal.
- Modify: `src/Arius.Cli.Tests/Commands/Archive/ArchiveCommandTests.cs`
  Purpose: assert archive CLI builds typed root options.
- Modify: `src/Arius.Cli.Tests/Commands/Restore/RestoreCommandTests.cs`
  Purpose: assert restore CLI builds typed root options.
- Modify: `src/Arius.Explorer.Tests/RepositoryExplorer/RepositoryExplorerViewModelTests.cs`
  Purpose: assert Explorer sends typed `LocalRootPath` into restore/list requests.
- Modify: `src/Arius.Explorer.Tests/RepositoryExplorer/FileItemViewModelTests.cs`
  Purpose: assert file display name comes from `RelativePath.Name`.
- Modify: `src/Arius.Integration.Tests/Pipeline/RoundtripTests.cs`
  Purpose: update typed roots in end-to-end archive/restore round trips.
- Modify: `src/Arius.Integration.Tests/Pipeline/RestoreDispositionTests.cs`
  Purpose: update typed restore roots in overwrite/skip behavior tests.
- Modify: `src/Arius.Integration.Tests/Pipeline/ListQueryIntegrationTests.cs`
  Purpose: update typed list local-root usage.

### Task 1: Add `LocalRootPath`

**Files:**
- Create: `src/Arius.Core/Shared/Paths/LocalRootPath.cs`
- Create: `src/Arius.Core.Tests/Shared/LocalRootPathTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Arius.Core.Shared.Paths;

namespace Arius.Core.Tests.Shared;

public class LocalRootPathTests
{
    [Test]
    public void Parse_AbsolutePath_CanonicalizesAndDoesNotRequireExistence()
    {
        var path = Path.Combine(Path.GetTempPath(), "arius-root-tests", "..", "arius-root-tests", "source");

        var root = LocalRootPath.Parse(path);

        root.ToString().ShouldBe(Path.GetFullPath(path));
    }

    [Test]
    public void Parse_RelativePath_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => LocalRootPath.Parse("source"));
    }

    [Test]
    public void Equality_FollowsHostPathSemantics()
    {
        var lower = LocalRootPath.Parse(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "arius-host-equality")));
        var upper = LocalRootPath.Parse(lower.ToString().ToUpperInvariant());

        if (OperatingSystem.IsWindows())
        {
            lower.ShouldBe(upper);
        }
        else
        {
            lower.ShouldNotBe(upper);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/LocalRootPathTests/*"`
Expected: FAIL at compile time because `LocalRootPath` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace Arius.Core.Shared.Paths;

public readonly record struct LocalRootPath
{
    private LocalRootPath(string value)
    {
        Value = value;
    }

    private string Value => field ?? throw new InvalidOperationException("LocalRootPath is uninitialized.");

    public static LocalRootPath Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var fullPath = Path.GetFullPath(value);
        if (!Path.IsPathRooted(fullPath))
        {
            throw new ArgumentException("Local root path must be absolute.", nameof(value));
        }

        return new LocalRootPath(Path.TrimEndingDirectorySeparator(fullPath));
    }

    public static bool TryParse(string? value, out LocalRootPath root)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            root = default;
            return false;
        }

        try
        {
            root = Parse(value);
            return true;
        }
        catch (ArgumentException)
        {
            root = default;
            return false;
        }
    }

    public bool Equals(LocalRootPath other) => Comparer.Equals(Value, other.Value);

    public override int GetHashCode() => Comparer.GetHashCode(Value);

    public override string ToString() => Value;

    private static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/LocalRootPathTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add "src/Arius.Core/Shared/Paths/LocalRootPath.cs" "src/Arius.Core.Tests/Shared/LocalRootPathTests.cs"
git commit -m "feat: add local root path type"
```

### Task 2: Add `RootedPath` And Root-Owned Relativization

**Files:**
- Create: `src/Arius.Core/Shared/Paths/RootedPath.cs`
- Modify: `src/Arius.Core/Shared/Paths/LocalRootPath.cs`
- Modify: `src/Arius.Core/Shared/Paths/RelativePath.cs`
- Create: `src/Arius.Core.Tests/Shared/RootedPathTests.cs`
- Modify: `src/Arius.Core.Tests/Shared/RelativePathTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Arius.Core.Shared.Paths;

namespace Arius.Core.Tests.Shared;

public class RootedPathTests
{
    [Test]
    public void RootedAt_ComposesRootAndRelativePath()
    {
        var root = LocalRootPath.Parse(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "arius-rooted")));
        var path = RelativePath.Parse("photos/2024/a.jpg");

        var rooted = path.RootedAt(root);

        rooted.Root.ShouldBe(root);
        rooted.RelativePath.ShouldBe(path);
        rooted.FullPath.ShouldBe(Path.Combine(root.ToString(), "photos", "2024", "a.jpg"));
    }

    [Test]
    public void GetRelativePath_RoundTripsAbsolutePathUnderRoot()
    {
        var root = LocalRootPath.Parse(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "arius-roundtrip")));
        var fullPath = Path.Combine(root.ToString(), "docs", "readme.txt");

        root.GetRelativePath(fullPath).ShouldBe(RelativePath.Parse("docs/readme.txt"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RootedPathTests/*|/*/*/RelativePathTests/*"`
Expected: FAIL at compile time because `RootedPath`, `RelativePath.RootedAt(...)`, and `LocalRootPath.GetRelativePath(...)` do not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace Arius.Core.Shared.Paths;

public readonly record struct RootedPath
{
    public RootedPath(LocalRootPath root, RelativePath relativePath)
    {
        Root = root;
        RelativePath = relativePath;
    }

    public LocalRootPath Root { get; }

    public RelativePath RelativePath { get; }

    public PathSegment? Name => RelativePath.Name;

    public string FullPath => RelativePath.IsRoot
        ? Root.ToString()
        : Path.Combine(Root.ToString(), RelativePath.ToString().Replace('/', Path.DirectorySeparatorChar));

    public bool Equals(RootedPath other) => Comparer.Equals(FullPath, other.FullPath);

    public override int GetHashCode() => Comparer.GetHashCode(FullPath);

    private static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
```

```csharp
public RelativePath GetRelativePath(string fullPath)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

    var absolute = Path.GetFullPath(fullPath);
    if (!Path.IsPathRooted(absolute))
    {
        throw new ArgumentException("Full path must be absolute.", nameof(fullPath));
    }

    var relative = Path.GetRelativePath(Value, absolute);
    if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
    {
        throw new ArgumentOutOfRangeException(nameof(fullPath), "Path must stay within the local root.");
    }

    return RelativePath.FromPlatformRelativePath(relative, allowEmpty: true);
}

public bool TryGetRelativePath(string fullPath, out RelativePath path)
{
    try
    {
        path = GetRelativePath(fullPath);
        return true;
    }
    catch (ArgumentException)
    {
        path = default;
        return false;
    }
    catch (ArgumentOutOfRangeException)
    {
        path = default;
        return false;
    }
}
```

```csharp
public RootedPath RootedAt(LocalRootPath root) => new(root, this);
```

Replace the rooted-path assertion in `RelativePathTests` with:

```csharp
[Test]
public void RootedAt_JoinsWithRootDirectory()
{
    var root = LocalRootPath.Parse(Path.Combine("C:", "repo"));
    var path = RelativePath.Parse("photos/2024/a.jpg");

    path.RootedAt(root).FullPath.ShouldBe(Path.Combine(root.ToString(), "photos", "2024", "a.jpg"));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RootedPathTests/*|/*/*/RelativePathTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add "src/Arius.Core/Shared/Paths/LocalRootPath.cs" "src/Arius.Core/Shared/Paths/RootedPath.cs" "src/Arius.Core/Shared/Paths/RelativePath.cs" "src/Arius.Core.Tests/Shared/RootedPathTests.cs" "src/Arius.Core.Tests/Shared/RelativePathTests.cs"
git commit -m "feat: add rooted path model"
```

### Task 3: Retype Archive, Restore, And List Root Boundaries

**Files:**
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommand.cs`
- Modify: `src/Arius.Core/Features/RestoreCommand/RestoreCommand.cs`
- Modify: `src/Arius.Core/Features/ListQuery/ListQuery.cs`
- Modify: `src/Arius.Cli/Commands/Archive/ArchiveVerb.cs`
- Modify: `src/Arius.Cli/Commands/Restore/RestoreVerb.cs`
- Modify: `src/Arius.Explorer/RepositoryExplorer/RepositoryExplorerViewModel.cs`
- Modify: `src/Arius.Cli.Tests/Commands/Archive/ArchiveCommandTests.cs`
- Modify: `src/Arius.Cli.Tests/Commands/Restore/RestoreCommandTests.cs`
- Modify: `src/Arius.Explorer.Tests/RepositoryExplorer/RepositoryExplorerViewModelTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
cmd.CommandOptions.RootDirectory.ShouldBe(LocalRootPath.Parse(Path.GetFullPath("/tmp")));
```

```csharp
cmd.Options.RootDirectory.ShouldBe(LocalRootPath.Parse(Path.GetFullPath("/data")));
```

```csharp
await mediator.Received(1).Send(
    Arg.Is<RestoreCommand>(command =>
        command.Options.RootDirectory == LocalRootPath.Parse("C:/data") &&
        command.Options.TargetPath == PathOf("file-a.txt")),
    Arg.Any<CancellationToken>());
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.Cli.Tests/Arius.Cli.Tests.csproj" --treenode-filter "/*/*/ArchiveCommandTests/*|/*/*/RestoreCommandTests/*"`
Expected: FAIL at compile time because the option properties still use `string`.

Run: `dotnet test --project "src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj" --treenode-filter "/*/*/RepositoryExplorerViewModelTests/RestoreCommand_WhenUserConfirms_RestoresFilesAndRefreshesNode"`
Expected: FAIL at compile time because Explorer still builds string root options.

- [ ] **Step 3: Write minimal implementation**

```csharp
using Arius.Core.Shared.Paths;

public sealed record ArchiveCommandOptions
{
    public required LocalRootPath RootDirectory { get; init; }
}

public sealed record RestoreOptions
{
    public required LocalRootPath RootDirectory { get; init; }
}

public sealed record ListQueryOptions
{
    public LocalRootPath? LocalPath { get; init; }
}
```

```csharp
var opts = new ArchiveCommandOptions
{
    RootDirectory = LocalRootPath.Parse(Path.GetFullPath(path)),
    UploadTier = tier,
    RemoveLocal = removeLocal,
    NoPointers = noPointers,
    SmallFileThreshold = 1024 * 1024L,
    TarTargetSize = 64L * 1024 * 1024,
};
```

```csharp
var opts = new RestoreOptions
{
    RootDirectory = LocalRootPath.Parse(Path.GetFullPath(path)),
    Version = version,
    NoPointers = noPointers,
    Overwrite = overwrite,
};
```

```csharp
var query = new ListQuery(new ListQueryOptions
{
    Prefix = node.Prefix,
    Recursive = false,
    LocalPath = LocalRootPath.Parse(Repository.LocalDirectoryPath),
});
```

```csharp
var command = new RestoreCommand(new RestoreOptions
{
    RootDirectory = LocalRootPath.Parse(Repository.LocalDirectoryPath),
    TargetPath = selectedFile.File.RelativePath,
    Overwrite = true,
    NoPointers = false,
});
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --project "src/Arius.Cli.Tests/Arius.Cli.Tests.csproj" --treenode-filter "/*/*/ArchiveCommandTests/*|/*/*/RestoreCommandTests/*"`
Expected: PASS.

Run: `dotnet test --project "src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj" --treenode-filter "/*/*/RepositoryExplorerViewModelTests/RestoreCommand_WhenUserConfirms_RestoresFilesAndRefreshesNode"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add "src/Arius.Core/Features/ArchiveCommand/ArchiveCommand.cs" "src/Arius.Core/Features/RestoreCommand/RestoreCommand.cs" "src/Arius.Core/Features/ListQuery/ListQuery.cs" "src/Arius.Cli/Commands/Archive/ArchiveVerb.cs" "src/Arius.Cli/Commands/Restore/RestoreVerb.cs" "src/Arius.Explorer/RepositoryExplorer/RepositoryExplorerViewModel.cs" "src/Arius.Cli.Tests/Commands/Archive/ArchiveCommandTests.cs" "src/Arius.Cli.Tests/Commands/Restore/RestoreCommandTests.cs" "src/Arius.Explorer.Tests/RepositoryExplorer/RepositoryExplorerViewModelTests.cs"
git commit -m "refactor: type local root option boundaries"
```

### Task 4: Migrate Restore And List Internals To `RootedPath`

**Files:**
- Modify: `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs`
- Modify: `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`
- Modify: `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`
- Modify: `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RestoreDispositionTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/ListQueryIntegrationTests.cs`

- [ ] **Step 1: Write the failing tests**

Use the existing list local-root tests and restore target tests as the red phase by retyping their options first:

```csharp
await foreach (var entry in handler.Handle(
    new ListQuery(new ListQueryOptions
    {
        LocalPath = LocalRootPath.Parse(tempRoot),
        Recursive = false,
    }),
    CancellationToken.None))
{
    results.Add(entry);
}
```

```csharp
var restoreResult = await fixture.CreateRestoreHandler().Handle(
    new RestoreCommand(new RestoreOptions
    {
        RootDirectory = LocalRootPath.Parse(fixture.RestoreRoot),
        Overwrite = true,
        TargetPath = PathOf("file-a.txt"),
    }),
    CancellationToken.None);
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/Handle_TargetPathWithTypedRootFile_RestoresSelectedRootFile|/*/*/RestoreCommandHandlerTests/Handle_LargeFileRestore_UsesCanonicalCallbackPath_AndPlatformWriteTarget|/*/*/ListQueryHandlerTests/Handle_WithLocalPath_MergesCloudAndLocalFilesInOneDirectory|/*/*/ListQueryHandlerTests/Handle_DirectoryMerge_AllThreeKindsYieldedWithCorrectFlags"`
Expected: FAIL because handlers and recursion state still depend on `string` rooted paths.

- [ ] **Step 3: Write minimal implementation**

Replace restore local path creation with:

```csharp
var rootedPath = file.RelativePath.RootedAt(opts.RootDirectory);
var localPath = rootedPath.FullPath;

Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
```

Replace list starting-point state with:

```csharp
private async Task<(FileTreeHash? TreeHash, RootedPath? LocalDirectory, RelativePath RelativeDirectory)> ResolveStartingPointAsync(
    FileTreeHash rootHash,
    LocalRootPath? localRoot,
    RelativePath? prefix,
    CancellationToken cancellationToken)
{
    if (prefix is null)
    {
        return (rootHash, localRoot is null ? null : RelativePath.Root.RootedAt(localRoot.Value), RelativePath.Root);
    }

    FileTreeHash? currentHash = rootHash;
    RootedPath? currentLocalDirectory = localRoot is null ? null : RelativePath.Root.RootedAt(localRoot.Value);
    var currentRelativeDirectory = RelativePath.Root;

    foreach (var segment in prefix.Value.Segments)
    {
        currentRelativeDirectory = currentRelativeDirectory / segment;

        if (currentHash is not null)
        {
            var treeEntries = await _fileTreeService.ReadAsync(currentHash.Value, cancellationToken);
            var nextDirectory = treeEntries
                .OfType<DirectoryEntry>()
                .FirstOrDefault(e => e.GetDirectoryName().Equals(segment, StringComparison.OrdinalIgnoreCase));

            currentHash = nextDirectory?.FileTreeHash;
        }

        if (currentLocalDirectory is not null)
        {
            var candidate = currentRelativeDirectory.RootedAt(currentLocalDirectory.Value.Root);
            currentLocalDirectory = Directory.Exists(candidate.FullPath) ? candidate : null;
        }
    }

    return (currentHash, currentLocalDirectory, currentRelativeDirectory);
}
```

And change local snapshot entry points to consume `RootedPath?`:

```csharp
private LocalDirectorySnapshot BuildLocalDirectorySnapshot(RootedPath? localDir, RelativePath currentRelativeDirectory)
{
    if (localDir is null || !Directory.Exists(localDir.Value.FullPath))
    {
        return LocalDirectorySnapshot.Empty;
    }

    var directories = new Dictionary<string, LocalDirectoryState>(StringComparer.OrdinalIgnoreCase);
    try
    {
        foreach (var directory in new DirectoryInfo(localDir.Value.FullPath).EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            directories[directory.Name] = new LocalDirectoryState(
                directory.Name,
                (currentRelativeDirectory / PathSegment.Parse(directory.Name)).RootedAt(localDir.Value.Root));
        }
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Could not enumerate subdirectories of: {Directory}", localDir.Value.FullPath);
    }

    var files = new Dictionary<string, LocalFileState>(StringComparer.OrdinalIgnoreCase);
    IEnumerable<FileInfo> fileInfos;
    try
    {
        fileInfos = new DirectoryInfo(localDir.Value.FullPath).EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Could not enumerate files in: {Directory}", localDir.Value.FullPath);
        fileInfos = [];
    }

    foreach (var file in fileInfos)
    {
        files[file.Name] = new LocalFileState(
            Name: file.Name,
            BinaryExists: true,
            PointerExists: File.Exists(file.FullName + PointerSuffix),
            PointerHash: null,
            FileSize: file.Length,
            Created: new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero),
            Modified: new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
    }

    return new LocalDirectorySnapshot(directories, files);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/Handle_TargetPathWithTypedRootFile_RestoresSelectedRootFile|/*/*/RestoreCommandHandlerTests/Handle_LargeFileRestore_UsesCanonicalCallbackPath_AndPlatformWriteTarget|/*/*/ListQueryHandlerTests/Handle_WithLocalPath_MergesCloudAndLocalFilesInOneDirectory|/*/*/ListQueryHandlerTests/Handle_DirectoryMerge_AllThreeKindsYieldedWithCorrectFlags"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add "src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs" "src/Arius.Core/Features/ListQuery/ListQueryHandler.cs" "src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs" "src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs" "src/Arius.Integration.Tests/Pipeline/RestoreDispositionTests.cs" "src/Arius.Integration.Tests/Pipeline/ListQueryIntegrationTests.cs"
git commit -m "refactor: root restore and list local paths"
```

### Task 5: Migrate Archive And Local Enumeration To Typed Roots

**Files:**
- Modify: `src/Arius.Core/Shared/LocalFile/LocalFileEnumerator.cs`
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`
- Modify: `src/Arius.Core.Tests/Shared/LocalFile/LocalFileEnumeratorTests.cs`
- Modify: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveTestEnvironment.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RoundtripTests.cs`
- Modify: `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`

- [ ] **Step 1: Write the failing tests**

Retype the existing enumerator and archive tests first:

```csharp
var pairs = _enumerator.Enumerate(LocalRootPath.Parse(_root)).ToList();
```

```csharp
return await handler.Handle(
    new ArchiveCommand(new ArchiveCommandOptions
    {
        RootDirectory = LocalRootPath.Parse(_rootDirectory),
        UploadTier = uploadTier,
    }),
    cancellationToken);
```

```csharp
opts ??= new ArchiveCommandOptions
{
    RootDirectory = LocalRootPath.Parse(LocalRoot),
    UploadTier = BlobTier.Hot,
};
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/LocalFileEnumeratorTests/*|/*/*/ArchiveRecoveryTests/*"`
Expected: FAIL because archive and enumeration still accept string roots and use `ToPlatformPath(...)`.

Run: `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RoundtripTests/Archive_TwoIdenticalFiles_SingleChunkUploaded_BothRestored|/*/*/RoundtripTests/Archive_RemoveLocal_ThenArchivePointers_Restore_Verify"`
Expected: FAIL at compile time once the fixtures are retyped.

- [ ] **Step 3: Write minimal implementation**

Change enumerator entry point and relative-path derivation to:

```csharp
public IEnumerable<FilePair> Enumerate(LocalRootPath rootDirectory)
{
    foreach (var file in EnumerateFilesDepthFirst(rootDirectory.ToString()))
    {
        var relativePath = rootDirectory.GetRelativePath(file.FullName);

        if (relativePath.ToString().EndsWith(PointerSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var binaryRelativeText = relativePath.ToString()[..^PointerSuffix.Length];
            var binaryFileRelativePath = RelativePath.Parse(binaryRelativeText);
            var binaryFileFullPath = binaryFileRelativePath.RootedAt(rootDirectory).FullPath;
        }
    }
}
```

Change archive handler filesystem access to:

```csharp
var pairs = enumerator.Enumerate(opts.RootDirectory);
var fullPath = pair.BinaryExists ? pair.RelativePath.RootedAt(opts.RootDirectory).FullPath : null;
var fullBinaryPath = pair.BinaryExists ? pair.RelativePath.RootedAt(opts.RootDirectory).FullPath : null;
```

And change filetree metadata reads to:

```csharp
private static async Task WriteFileTreeEntry(HashedFilePair hashed, LocalRootPath rootDirectory, FileTreeStagingWriter writer, CancellationToken ct)
{
    var pair = hashed.FilePair;

    DateTimeOffset created;
    DateTimeOffset modified;
    if (pair.BinaryExists)
    {
        var fullPath = pair.RelativePath.RootedAt(rootDirectory).FullPath;
        var fi = new FileInfo(fullPath);
        created = new DateTimeOffset(fi.CreationTimeUtc, TimeSpan.Zero);
        modified = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero);
    }
    else
    {
        created = DateTimeOffset.UtcNow;
        modified = DateTimeOffset.UtcNow;
    }

    await writer.AppendFileEntryAsync(pair.RelativePath, hashed.ContentHash, created, modified, ct);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/LocalFileEnumeratorTests/*|/*/*/ArchiveRecoveryTests/*"`
Expected: PASS.

Run: `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RoundtripTests/Archive_TwoIdenticalFiles_SingleChunkUploaded_BothRestored|/*/*/RoundtripTests/Archive_RemoveLocal_ThenArchivePointers_Restore_Verify"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add "src/Arius.Core/Shared/LocalFile/LocalFileEnumerator.cs" "src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs" "src/Arius.Core.Tests/Shared/LocalFile/LocalFileEnumeratorTests.cs" "src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveTestEnvironment.cs" "src/Arius.Integration.Tests/Pipeline/PipelineFixture.cs" "src/Arius.Integration.Tests/Pipeline/RoundtripTests.cs" "src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs"
git commit -m "refactor: root archive local paths"
```

### Task 6: Remove `ToPlatformPath(...)` And Switch Explorer Name Rendering

**Files:**
- Modify: `src/Arius.Core/Shared/Paths/RelativePath.cs`
- Modify: `src/Arius.Explorer/RepositoryExplorer/FileItemViewModel.cs`
- Modify: `src/Arius.Explorer.Tests/RepositoryExplorer/FileItemViewModelTests.cs`
- Modify: `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
[Test]
public void Constructor_UsesRelativePathName_ForDisplayName()
{
    var file = new RepositoryFileEntry(
        RelativePath: PathOf("nested/report.txt"),
        ContentHash: ContentHashA,
        OriginalSize: 1,
        Created: null,
        Modified: null,
        ExistsInCloud: true,
        ExistsLocally: true,
        HasPointerFile: false,
        BinaryExists: false,
        Hydrated: null);

    var viewModel = new FileItemViewModel(file);

    viewModel.Name.ShouldBe("report.txt");
}
```

Update the restore assertion to use rooted paths:

```csharp
var restoredPath = PathOf("docs/readme.txt").RootedAt(LocalRootPath.Parse(fixture.RestoreRoot)).FullPath;
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj" --treenode-filter "/*/*/FileItemViewModelTests/*"`
Expected: FAIL because `FileItemViewModel` still uses `Path.GetFileName(file.RelativePath.ToString())` and the new name test does not exist yet.

- [ ] **Step 3: Write minimal implementation**

```csharp
public FileItemViewModel(RepositoryFileEntry file)
{
    File = file;

    Name = file.RelativePath.Name?.ToString() ?? string.Empty;

    PointerFileStateColor = file.HasPointerFile == true ? Brushes.Black : Brushes.Transparent;
    BinaryFileStateColor = file.BinaryExists == true ? Brushes.Blue : Brushes.White;
    PointerFileEntryStateColor = file.ExistsInCloud ? Brushes.Black : Brushes.Transparent;
}
```

Then remove `ToPlatformPath(...)` from `RelativePath.cs` and update remaining callers to `RootedAt(...).FullPath`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --project "src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj" --treenode-filter "/*/*/FileItemViewModelTests/*"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add "src/Arius.Core/Shared/Paths/RelativePath.cs" "src/Arius.Explorer/RepositoryExplorer/FileItemViewModel.cs" "src/Arius.Explorer.Tests/RepositoryExplorer/FileItemViewModelTests.cs" "src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs"
git commit -m "refactor: use rooted paths for local joins"
```

### Task 7: Full Verification

**Files:**
- Modify: any touched files from Tasks 1-6

- [ ] **Step 1: Run focused Core verification**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/LocalRootPathTests/*|/*/*/RootedPathTests/*|/*/*/RelativePathTests/*|/*/*/LocalFileEnumeratorTests/*|/*/*/RestoreCommandHandlerTests/*|/*/*/ListQueryHandlerTests/*|/*/*/ArchiveRecoveryTests/*"`
Expected: PASS.

- [ ] **Step 2: Run focused CLI verification**

Run: `dotnet test --project "src/Arius.Cli.Tests/Arius.Cli.Tests.csproj" --treenode-filter "/*/*/ArchiveCommandTests/*|/*/*/RestoreCommandTests/*|/*/*/ListQueryParsingTests/*"`
Expected: PASS.

- [ ] **Step 3: Run focused Explorer verification**

Run: `dotnet test --project "src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj" --treenode-filter "/*/*/RepositoryExplorerViewModelTests/*|/*/*/FileItemViewModelTests/*"`
Expected: PASS.

- [ ] **Step 4: Run focused integration verification**

Run: `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RoundtripTests/*|/*/*/RestoreDispositionTests/*|/*/*/ListQueryIntegrationTests/*"`
Expected: PASS.

- [ ] **Step 5: Run slopwatch**

Run: `slopwatch analyze`
Expected: `0 issue(s) found`.

- [ ] **Step 6: Commit verification cleanups**

```bash
git add -A
git commit -m "test: verify local root path migration"
```
