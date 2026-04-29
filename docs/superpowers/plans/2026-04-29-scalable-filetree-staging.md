# Scalable Filetree Staging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace manifest-based filetree construction with hashed `.staging` filetree staging that scales to millions of files spread across many directories.

**Architecture:** Archive writes completed files into repository-local filetree staging nodes keyed by SHA-256 of canonical relative directory paths. The builder consumes the staging graph bottom-up, combines staged `FileEntry` lines with final child `DirectoryEntry` values, and stores immutable filetrees through `FileTreeService.EnsureStoredAsync`.

**Tech Stack:** .NET 10, C#, TUnit, existing Arius filetree serialization, repository-local `.arius` cache directories.

---

## File Structure

- Create `src/Arius.Core/Shared/FileTree/FileTreeStagingPaths.cs`: path and directory-id helpers for `.staging`.
- Create `src/Arius.Core/Shared/FileTree/FileTreeStagingSession.cs`: owns staging lock, start cleanup, and final cleanup.
- Create `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`: appends staged `FileEntry` and child-link lines.
- Create `src/Arius.Core/Shared/FileTree/StagedChildLink.cs`: internal readonly value used by the builder to parse `children` lines.
- Modify `src/Arius.Core/Shared/FileTree/FileTreeBlobSerializer.cs`: add focused helpers for serializing and parsing one `FileEntry` line.
- Modify `src/Arius.Core/Shared/FileTree/FileTreeService.cs`: add `EnsureStoredAsync(FileTreeBlob, CancellationToken)`.
- Modify `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`: consume a staging directory instead of a sorted manifest path.
- Modify `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`: replace manifest writer/sorter usage with staging session and writer.
- Delete `src/Arius.Core/Shared/FileTree/ManifestEntry.cs`, `src/Arius.Core/Shared/FileTree/ManifestWriter.cs`, and `src/Arius.Core/Shared/FileTree/ManifestSorter.cs` after all call sites are removed.
- Modify `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`: update builder tests to staging input.
- Create `src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs`: unit tests for staging path and writer behavior.
- Modify `src/Arius.Integration.Tests/Shared/FileTree/FileTreeBuilderIntegrationTests.cs`: update integration tests to staging input.

### Task 1: Add Staging Path Helpers

**Files:**
- Create: `src/Arius.Core/Shared/FileTree/FileTreeStagingPaths.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs`

- [ ] **Step 1: Write failing tests for directory ids and node paths**

Add this test class:

```csharp
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreeStagingWriterTests
{
    [Test]
    public void GetDirectoryId_UsesCanonicalForwardSlashPath()
    {
        var id1 = FileTreeStagingPaths.GetDirectoryId("photos/2024");
        var id2 = FileTreeStagingPaths.GetDirectoryId("photos\\2024");

        id1.ShouldBe(id2);
        id1.Length.ShouldBe(64);
        id1.ShouldAllBe(c => Uri.IsHexDigit(c));
    }

    [Test]
    public void GetNodeDirectory_UsesTwoCharacterFanout()
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), "arius-staging-test");
        var dirId = FileTreeStagingPaths.GetDirectoryId("docs");

        var nodePath = FileTreeStagingPaths.GetNodeDirectory(stagingRoot, dirId);

        nodePath.ShouldBe(Path.Combine(stagingRoot, "dirs", dirId[..2], dirId));
    }
}
```

- [ ] **Step 2: Run tests and verify they fail because the helper does not exist**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeStagingWriterTests/*"
```

Expected: build fails with `FileTreeStagingPaths` not found.

- [ ] **Step 3: Implement `FileTreeStagingPaths`**

Create `src/Arius.Core/Shared/FileTree/FileTreeStagingPaths.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.FileTree;

internal static class FileTreeStagingPaths
{
    public const string StagingDirectoryName = ".staging";
    public const string EntriesFileName = "entries";
    public const string ChildrenFileName = "children";

    public static string GetStagingRoot(string fileTreeCacheDirectory) =>
        Path.Combine(fileTreeCacheDirectory, StagingDirectoryName);

    public static string GetDirectoryId(string canonicalRelativeDirectoryPath)
    {
        var normalized = NormalizeDirectoryPath(canonicalRelativeDirectoryPath);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes);
    }

    public static string GetNodeDirectory(string stagingRoot, string directoryId) =>
        Path.Combine(stagingRoot, "dirs", directoryId[..2], directoryId);

    public static string GetEntriesPath(string stagingRoot, string directoryId) =>
        Path.Combine(GetNodeDirectory(stagingRoot, directoryId), EntriesFileName);

    public static string GetChildrenPath(string stagingRoot, string directoryId) =>
        Path.Combine(GetNodeDirectory(stagingRoot, directoryId), ChildrenFileName);

    public static string NormalizeDirectoryPath(string directoryPath)
    {
        var normalized = directoryPath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        return normalized.Trim('/');
    }
}
```

- [ ] **Step 4: Run tests and verify they pass**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeStagingWriterTests/*"
```

Expected: `FileTreeStagingWriterTests` passes.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreeStagingPaths.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs
git commit -m "feat: add filetree staging paths"
```

### Task 2: Add Single-Entry Serialization Helpers

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeBlobSerializer.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeBlobSerializerTests.cs`

- [ ] **Step 1: Write failing tests for one-line file entry roundtrip**

Add this test to `FileTreeBlobSerializerTests`:

```csharp
[Test]
public void SerializeFileEntryLine_RoundTripsSingleFileEntry()
{
    var created = new DateTimeOffset(2026, 4, 29, 10, 0, 0, TimeSpan.Zero);
    var modified = created.AddMinutes(5);
    var entry = new FileEntry
    {
        Name = "file with spaces.txt",
        ContentHash = ContentHash.Parse(NormalizeHash("abc")),
        Created = created,
        Modified = modified
    };

    var line = FileTreeBlobSerializer.SerializeFileEntryLine(entry);
    var parsed = FileTreeBlobSerializer.ParseFileEntryLine(line);

    parsed.ShouldBe(entry);
}
```

- [ ] **Step 2: Run the test and verify it fails because the methods do not exist**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeBlobSerializerTests/SerializeFileEntryLine_RoundTripsSingleFileEntry"
```

Expected: build fails with missing `SerializeFileEntryLine` and `ParseFileEntryLine`.

- [ ] **Step 3: Add serializer helpers**

Add these methods to `FileTreeBlobSerializer`:

```csharp
public static string SerializeFileEntryLine(FileEntry fileEntry) =>
    $"{fileEntry.ContentHash} F {fileEntry.Created:O} {fileEntry.Modified:O} {fileEntry.Name}";

public static FileEntry ParseFileEntryLine(string line)
{
    var blob = ParseLines([line]);
    if (blob.Entries.Count != 1 || blob.Entries[0] is not FileEntry fileEntry)
        throw new FormatException($"Invalid staged file entry: '{line}'");

    return fileEntry;
}
```

Then update existing file-entry serialization loops in `Serialize` and `SerializeForStorageAsync` to call `SerializeFileEntryLine(fileEntry)` so staging and persisted file lines stay identical.

- [ ] **Step 4: Run serializer tests**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeBlobSerializerTests/*"
```

Expected: all `FileTreeBlobSerializerTests` pass.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreeBlobSerializer.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeBlobSerializerTests.cs
git commit -m "refactor: share filetree file entry serialization"
```

### Task 3: Add Staging Session Cleanup And Locking

**Files:**
- Create: `src/Arius.Core/Shared/FileTree/FileTreeStagingSession.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs`

- [ ] **Step 1: Write failing tests for stale staging cleanup and lock contention**

Add these tests to `FileTreeStagingWriterTests`:

```csharp
[Test]
public async Task OpenAsync_DeletesExistingStagingDirectory()
{
    var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
    try
    {
        var stagingRoot = FileTreeStagingPaths.GetStagingRoot(cacheDir);
        Directory.CreateDirectory(stagingRoot);
        await File.WriteAllTextAsync(Path.Combine(stagingRoot, "stale"), "old");

        await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);

        File.Exists(Path.Combine(stagingRoot, "stale")).ShouldBeFalse();
        Directory.Exists(session.StagingRoot).ShouldBeTrue();
    }
    finally
    {
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);
    }
}

[Test]
public async Task OpenAsync_FailsWhenAnotherSessionHoldsLock()
{
    var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
    try
    {
        await using var first = await FileTreeStagingSession.OpenAsync(cacheDir);

        await Assert.ThrowsAsync<IOException>(async () =>
            await FileTreeStagingSession.OpenAsync(cacheDir));
    }
    finally
    {
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);
    }
}
```

- [ ] **Step 2: Run tests and verify they fail because the session does not exist**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeStagingWriterTests/*"
```

Expected: build fails with `FileTreeStagingSession` not found.

- [ ] **Step 3: Implement staging session**

Create `src/Arius.Core/Shared/FileTree/FileTreeStagingSession.cs`:

```csharp
namespace Arius.Core.Shared.FileTree;

internal sealed class FileTreeStagingSession : IAsyncDisposable
{
    private readonly FileStream _lockStream;

    private FileTreeStagingSession(string stagingRoot, FileStream lockStream)
    {
        StagingRoot = stagingRoot;
        _lockStream = lockStream;
    }

    public string StagingRoot { get; }

    public static Task<FileTreeStagingSession> OpenAsync(string fileTreeCacheDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(fileTreeCacheDirectory);
        var lockPath = Path.Combine(fileTreeCacheDirectory, ".staging.lock");
        var lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var stagingRoot = FileTreeStagingPaths.GetStagingRoot(fileTreeCacheDirectory);

        if (Directory.Exists(stagingRoot))
            Directory.Delete(stagingRoot, recursive: true);

        Directory.CreateDirectory(stagingRoot);
        return Task.FromResult(new FileTreeStagingSession(stagingRoot, lockStream));
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(StagingRoot))
                Directory.Delete(StagingRoot, recursive: true);
        }
        finally
        {
            _lockStream.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 4: Run staging tests**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeStagingWriterTests/*"
```

Expected: `FileTreeStagingWriterTests` passes.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreeStagingSession.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs
git commit -m "feat: add filetree staging session"
```

### Task 4: Add Staging Writer

**Files:**
- Create: `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs`

- [ ] **Step 1: Write failing tests for entries and child links**

Add these tests to `FileTreeStagingWriterTests`:

```csharp
[Test]
public async Task AppendFileAsync_WritesFileEntryToParentDirectoryNode()
{
    var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
    await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
    var writer = new FileTreeStagingWriter(session.StagingRoot);
    var now = new DateTimeOffset(2026, 4, 29, 10, 0, 0, TimeSpan.Zero);
    var hash = ContentHash.Parse(new string('a', 64));

    await writer.AppendFileAsync("photos/a.jpg", hash, now, now);

    var photosId = FileTreeStagingPaths.GetDirectoryId("photos");
    var entriesPath = FileTreeStagingPaths.GetEntriesPath(session.StagingRoot, photosId);
    var line = (await File.ReadAllLinesAsync(entriesPath)).Single();
    var entry = FileTreeBlobSerializer.ParseFileEntryLine(line);

    entry.Name.ShouldBe("a.jpg");
    entry.ContentHash.ShouldBe(hash);
}

[Test]
public async Task AppendFileAsync_WritesChildLinksForNestedPath()
{
    var cacheDir = Path.Combine(Path.GetTempPath(), $"arius-cache-{Guid.NewGuid():N}");
    await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
    var writer = new FileTreeStagingWriter(session.StagingRoot);
    var now = new DateTimeOffset(2026, 4, 29, 10, 0, 0, TimeSpan.Zero);

    await writer.AppendFileAsync("photos/2024/a.jpg", ContentHash.Parse(new string('b', 64)), now, now);

    var rootId = FileTreeStagingPaths.GetDirectoryId(string.Empty);
    var photosId = FileTreeStagingPaths.GetDirectoryId("photos");
    var photos2024Id = FileTreeStagingPaths.GetDirectoryId("photos/2024");

    var rootChildren = await File.ReadAllTextAsync(FileTreeStagingPaths.GetChildrenPath(session.StagingRoot, rootId));
    var photosChildren = await File.ReadAllTextAsync(FileTreeStagingPaths.GetChildrenPath(session.StagingRoot, photosId));

    rootChildren.ShouldContain($"{photosId} D photos/");
    photosChildren.ShouldContain($"{photos2024Id} D 2024/");
}
```

- [ ] **Step 2: Run tests and verify they fail because the writer does not exist**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeStagingWriterTests/*"
```

Expected: build fails with `FileTreeStagingWriter` not found.

- [ ] **Step 3: Implement staging writer**

Create `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`:

```csharp
using System.Collections.Concurrent;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.FileTree;

internal sealed class FileTreeStagingWriter
{
    private readonly string _stagingRoot;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _nodeLocks = new(StringComparer.Ordinal);

    public FileTreeStagingWriter(string stagingRoot)
    {
        _stagingRoot = stagingRoot;
    }

    public async Task AppendFileAsync(string relativePath, ContentHash contentHash, DateTimeOffset created, DateTimeOffset modified, CancellationToken cancellationToken = default)
    {
        var normalizedPath = relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').Trim('/');
        var directoryPath = GetDirectoryPath(normalizedPath);
        var fileName = Path.GetFileName(normalizedPath);
        var directoryId = FileTreeStagingPaths.GetDirectoryId(directoryPath);
        var fileEntry = new FileEntry
        {
            Name = fileName,
            ContentHash = contentHash,
            Created = created,
            Modified = modified
        };

        await AppendLineAsync(directoryId, FileTreeStagingPaths.EntriesFileName, FileTreeBlobSerializer.SerializeFileEntryLine(fileEntry), cancellationToken);
        await EnsureChildLinksAsync(directoryPath, cancellationToken);
    }

    private async Task EnsureChildLinksAsync(string directoryPath, CancellationToken cancellationToken)
    {
        if (directoryPath.Length == 0)
            return;

        var segments = directoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parentPath = string.Empty;
        foreach (var segment in segments)
        {
            var childPath = parentPath.Length == 0 ? segment : $"{parentPath}/{segment}";
            var parentId = FileTreeStagingPaths.GetDirectoryId(parentPath);
            var childId = FileTreeStagingPaths.GetDirectoryId(childPath);
            await AppendLineAsync(parentId, FileTreeStagingPaths.ChildrenFileName, $"{childId} D {segment}/", cancellationToken);
            parentPath = childPath;
        }
    }

    private async Task AppendLineAsync(string directoryId, string fileName, string line, CancellationToken cancellationToken)
    {
        var gate = _nodeLocks.GetOrAdd($"{directoryId}/{fileName}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var nodeDirectory = FileTreeStagingPaths.GetNodeDirectory(_stagingRoot, directoryId);
            Directory.CreateDirectory(nodeDirectory);
            var path = Path.Combine(nodeDirectory, fileName);
            await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string GetDirectoryPath(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }
}
```

- [ ] **Step 4: Run staging writer tests**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeStagingWriterTests/*"
```

Expected: `FileTreeStagingWriterTests` passes.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs
git commit -m "feat: write filetree staging entries"
```

### Task 5: Add FileTreeService EnsureStoredAsync

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeService.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs`

- [ ] **Step 1: Write failing tests for ensure-store behavior**

Add this test to `FileTreeServiceTests`:

```csharp
[Test]
public async Task EnsureStoredAsync_ComputesHashAndWritesMissingTree()
{
    var blobs = new FakeRecordingBlobContainerService();
    var account = $"acc-{Guid.NewGuid():N}";
    var container = $"con-{Guid.NewGuid():N}";
    var encryption = new PlaintextPassthroughService();
    var chunkIndex = new ChunkIndexService(blobs, encryption, account, container);
    var service = new FileTreeService(blobs, encryption, chunkIndex, account, container);
    await service.ValidateAsync();
    var tree = new FileTreeBlob
    {
        Entries =
        [
            new FileEntry
            {
                Name = "readme.txt",
                ContentHash = ContentHash.Parse(new string('c', 64)),
                Created = DateTimeOffset.UnixEpoch,
                Modified = DateTimeOffset.UnixEpoch
            }
        ]
    };

    var hash = await service.EnsureStoredAsync(tree);

    hash.ShouldBe(FileTreeBlobSerializer.ComputeHash(tree, encryption));
    blobs.Uploaded.ShouldContain(BlobPaths.FileTree(hash));
}
```

- [ ] **Step 2: Run the test and verify it fails because `EnsureStoredAsync` does not exist**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeServiceTests/EnsureStoredAsync_ComputesHashAndWritesMissingTree"
```

Expected: build fails with missing `EnsureStoredAsync`.

- [ ] **Step 3: Implement `EnsureStoredAsync`**

Add this method to `FileTreeService`:

```csharp
public async Task<FileTreeHash> EnsureStoredAsync(FileTreeBlob tree, CancellationToken cancellationToken = default)
{
    var hash = FileTreeBlobSerializer.ComputeHash(tree, _encryption);
    if (!ExistsInRemote(hash))
        await WriteAsync(hash, tree, cancellationToken);

    return hash;
}
```

- [ ] **Step 4: Run filetree service tests**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeServiceTests/*"
```

Expected: `FileTreeServiceTests` passes.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreeService.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs
git commit -m "feat: ensure filetree storage through service"
```

### Task 6: Build Filetrees From Staging

**Files:**
- Create: `src/Arius.Core/Shared/FileTree/StagedChildLink.cs`
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`

- [ ] **Step 1: Write failing builder tests using staging input**

Replace manifest-file setup in `FileTreeBuilderTests` with staging setup. Add this helper in the test class:

```csharp
private static async Task<(FileTreeStagingSession Session, string StagingRoot)> CreateStagingAsync(string accountName, string containerName, params (string Path, ContentHash Hash, DateTimeOffset Created, DateTimeOffset Modified)[] files)
{
    var cacheDir = FileTreeService.GetDiskCacheDirectory(accountName, containerName);
    if (Directory.Exists(cacheDir))
        Directory.Delete(cacheDir, recursive: true);

    var session = await FileTreeStagingSession.OpenAsync(cacheDir);
    var writer = new FileTreeStagingWriter(session.StagingRoot);
    foreach (var file in files)
        await writer.AppendFileAsync(file.Path, file.Hash, file.Created, file.Modified);

    return (session, session.StagingRoot);
}
```

Update the single-file test to call:

```csharp
var (stagingSession, stagingRoot) = await CreateStagingAsync(acct, cont, ("readme.txt", FakeContentHash('b'), now, now));
await using (stagingSession)
{
var root = await builder.SynchronizeAsync(stagingRoot);
}
```

- [ ] **Step 2: Run the focused builder test and verify it fails with current manifest parser behavior**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeBuilderTests/SynchronizeAsync_SingleFile_RootTreeUploaded"
```

Expected: test fails because `SynchronizeAsync` still expects a manifest file path.

- [ ] **Step 3: Add staged child link parser**

Create `src/Arius.Core/Shared/FileTree/StagedChildLink.cs`:

```csharp
namespace Arius.Core.Shared.FileTree;

internal readonly record struct StagedChildLink(string DirectoryId, string Name)
{
    public static StagedChildLink Parse(string line)
    {
        var firstSpace = line.IndexOf(' ');
        if (firstSpace < 0)
            throw new FormatException($"Invalid staged child link: '{line}'");

        var directoryId = line[..firstSpace];
        var markerAndName = line[(firstSpace + 1)..];
        if (markerAndName.Length < 3 || markerAndName[0] != 'D' || markerAndName[1] != ' ')
            throw new FormatException($"Invalid staged child link: '{line}'");

        var name = markerAndName[2..];
        if (directoryId.Length != 64 || string.IsNullOrWhiteSpace(name))
            throw new FormatException($"Invalid staged child link: '{line}'");

        return new StagedChildLink(directoryId, name);
    }
}
```

- [ ] **Step 4: Rewrite `FileTreeBuilder.SynchronizeAsync` to consume staging root**

Replace the manifest-reading implementation with staging recursion. Keep the public name `SynchronizeAsync` and interpret the string parameter as `stagingRoot`:

```csharp
public async Task<FileTreeHash?> SynchronizeAsync(string stagingRoot, CancellationToken cancellationToken = default)
{
    var rootId = FileTreeStagingPaths.GetDirectoryId(string.Empty);
    return await BuildDirectoryAsync(stagingRoot, rootId, cancellationToken);
}

private async Task<FileTreeHash?> BuildDirectoryAsync(string stagingRoot, string directoryId, CancellationToken cancellationToken)
{
    var childEntries = new List<FileTreeEntry>();
    foreach (var child in await ReadChildLinksAsync(stagingRoot, directoryId, cancellationToken))
    {
        var childHash = await BuildDirectoryAsync(stagingRoot, child.DirectoryId, cancellationToken);
        if (childHash is not null)
        {
            childEntries.Add(new DirectoryEntry
            {
                Name = child.Name,
                FileTreeHash = childHash.Value
            });
        }
    }

    var entries = new List<FileTreeEntry>();
    entries.AddRange(await ReadFileEntriesAsync(stagingRoot, directoryId, cancellationToken));
    entries.AddRange(childEntries);

    if (entries.Count == 0)
        return null;

    var tree = new FileTreeBlob { Entries = entries };
    return await _fileTreeService.EnsureStoredAsync(tree, cancellationToken);
}
```

Add private readers that read missing `entries` or `children` files as empty, parse file lines with `FileTreeBlobSerializer.ParseFileEntryLine`, parse child lines with `StagedChildLink.Parse`, and deduplicate child links with `DistinctBy(link => (link.Name, link.DirectoryId))`.

- [ ] **Step 5: Run builder tests**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeBuilderTests/*"
```

Expected: `FileTreeBuilderTests` passes after all tests use staging roots instead of manifest files.

- [ ] **Step 6: Commit**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs src/Arius.Core/Shared/FileTree/StagedChildLink.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs
git commit -m "feat: build filetrees from staging"
```

### Task 7: Add Bounded Parallel Subtree Build

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`

- [ ] **Step 1: Write failing test that observes sibling subtree concurrency**

Adapt `SynchronizeAsync_StartsMultipleFileTreeUploadsBeforeReturning` so its staging contains at least two sibling directories with files. Keep `BlockingFileTreeUploadBlobContainerService` and assert that two filetree uploads start before uploads are allowed to complete.

Use staged files:

```csharp
var (stagingSession, stagingRoot) = await CreateStagingAsync(
    accountName,
    containerName,
    ("photos/a.jpg", FakeContentHash('7'), now, now),
    ("docs/report.pdf", FakeContentHash('8'), now, now),
    ("music/song.mp3", FakeContentHash('9'), now, now));
await using (stagingSession)
{
    var synchronizeTask = builder.SynchronizeAsync(stagingRoot);
}
```

- [ ] **Step 2: Run the concurrency test and verify it fails if build is sequential**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeBuilderTests/SynchronizeAsync_StartsMultipleFileTreeUploadsBeforeReturning"
```

Expected: test fails until sibling builds can overlap.

- [ ] **Step 3: Add bounded branch parallelism**

In `FileTreeBuilder`, add:

```csharp
private const int FileTreeBuildWorkers = 4;
private readonly SemaphoreSlim _branchWorkers = new(FileTreeBuildWorkers, FileTreeBuildWorkers);
```

When building children, start a task only when a branch worker is available; otherwise build inline:

```csharp
var childTasks = new List<Task<DirectoryEntry?>>();
foreach (var child in childLinks)
{
    if (await _branchWorkers.WaitAsync(0, cancellationToken))
    {
        childTasks.Add(Task.Run(async () =>
        {
            try
            {
                var childHash = await BuildDirectoryAsync(stagingRoot, child.DirectoryId, cancellationToken);
                return childHash is null ? null : new DirectoryEntry { Name = child.Name, FileTreeHash = childHash.Value };
            }
            finally
            {
                _branchWorkers.Release();
            }
        }, cancellationToken));
    }
    else
    {
        var childHash = await BuildDirectoryAsync(stagingRoot, child.DirectoryId, cancellationToken);
        childTasks.Add(Task.FromResult<DirectoryEntry?>(childHash is null ? null : new DirectoryEntry { Name = child.Name, FileTreeHash = childHash.Value }));
    }
}

foreach (var childEntry in await Task.WhenAll(childTasks))
{
    if (childEntry is not null)
        childEntries.Add(childEntry);
}
```

- [ ] **Step 4: Run builder tests**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeBuilderTests/*"
```

Expected: `FileTreeBuilderTests` passes.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs
git commit -m "perf: parallelize staged filetree build"
```

### Task 8: Wire Staging Into ArchiveCommandHandler

**Files:**
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`
- Delete: `src/Arius.Core/Shared/FileTree/ManifestEntry.cs`
- Delete: `src/Arius.Core/Shared/FileTree/ManifestWriter.cs`
- Delete: `src/Arius.Core/Shared/FileTree/ManifestSorter.cs`
- Test: `src/Arius.Integration.Tests/Shared/FileTree/FileTreeBuilderIntegrationTests.cs`

- [ ] **Step 1: Update integration tests to create staging instead of manifest files**

In `FileTreeBuilderIntegrationTests`, replace `ManifestEntry` and temp manifest setup with `FileTreeStagingSession` and `FileTreeStagingWriter`, mirroring the unit-test helper from Task 6.

- [ ] **Step 2: Run integration builder tests and verify they fail until archive wiring and builder signatures are complete**

Run:

```bash
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/FileTreeBuilderIntegrationTests/*"
```

Expected: build or tests fail while old manifest call sites remain.

- [ ] **Step 3: Replace manifest usage in archive handler**

In `ArchiveCommandHandler.Handle`, replace:

```csharp
var manifestPath = Path.GetTempFileName();
var manifestWriter = new ManifestWriter(manifestPath);
```

with:

```csharp
var fileTreeCacheDir = FileTreeService.GetDiskCacheDirectory(_accountName, _containerName);
await using var stagingSession = await FileTreeStagingSession.OpenAsync(fileTreeCacheDir, cancellationToken);
var stagingWriter = new FileTreeStagingWriter(stagingSession.StagingRoot);
```

Replace each `WriteManifestEntry(..., manifestWriter, ...)` call with:

```csharp
await WriteFileTreeEntry(hashed, opts.RootDirectory, stagingWriter, cancellationToken);
```

Replace the archive tail:

```csharp
await manifestWriter.DisposeAsync();
await ManifestSorter.SortAsync(manifestPath, cancellationToken);
var treeBuilder = new FileTreeBuilder(_encryption, _fileTreeService);
var rootHash = await treeBuilder.SynchronizeAsync(manifestPath, cancellationToken);
```

with:

```csharp
var treeBuilder = new FileTreeBuilder(_encryption, _fileTreeService);
var rootHash = await treeBuilder.SynchronizeAsync(stagingSession.StagingRoot, cancellationToken);
```

Rename `WriteManifestEntry` to `WriteFileTreeEntry` and make it call:

```csharp
await writer.AppendFileAsync(manifestPath, hashed.ContentHash, created, modified, ct);
```

- [ ] **Step 4: Delete manifest types after all call sites are gone**

Delete:

```text
src/Arius.Core/Shared/FileTree/ManifestEntry.cs
src/Arius.Core/Shared/FileTree/ManifestWriter.cs
src/Arius.Core/Shared/FileTree/ManifestSorter.cs
```

- [ ] **Step 5: Run focused integration tests**

Run:

```bash
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/FileTreeBuilderIntegrationTests/*"
```

Expected: `FileTreeBuilderIntegrationTests` passes.

- [ ] **Step 6: Commit**

```bash
git add src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs src/Arius.Core/Shared/FileTree src/Arius.Integration.Tests/Shared/FileTree/FileTreeBuilderIntegrationTests.cs
git commit -m "refactor: stage filetrees without manifest files"
```

### Task 9: Verify Archive Behavior End To End

**Files:**
- Modify only files needed to fix failures discovered by verification.

- [ ] **Step 1: Run core filetree tests**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTree*/*"
```

Expected: all matching core filetree tests pass.

- [ ] **Step 2: Run archive and filetree integration tests**

Run:

```bash
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/FileTreeBuilderIntegrationTests/*|/*/*/RoundtripTests/*"
```

Expected: matching integration tests pass when Azurite or the configured test backend is available.

- [ ] **Step 3: Run full core tests**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj"
```

Expected: all core tests pass.

- [ ] **Step 4: Run Slopwatch**

Run:

```bash
slopwatch analyze --fail-on warning
```

Expected: `0 issues`.

- [ ] **Step 5: Commit verification fixes**

If verification required fixes, commit them:

```bash
git add src/Arius.Core src/Arius.Core.Tests src/Arius.Integration.Tests
git commit -m "fix: stabilize staged filetree archive flow"
```

If no files changed during verification, do not create an empty commit.

## Plan Self-Review

- Spec coverage: tasks cover staging paths, staging cleanup, staged file lines, child links, service responsibility split, bottom-up builder, bounded parallelism, archive wiring, manifest removal, and verification.
- Placeholder scan: no placeholder sections remain; each implementation task includes concrete files, code shape, commands, and expected outcomes.
- Type consistency: the plan consistently uses `FileTreeStagingPaths`, `FileTreeStagingSession`, `FileTreeStagingWriter`, `StagedChildLink`, `FileTreeService.EnsureStoredAsync`, and `FileTreeBuilder.SynchronizeAsync`.
