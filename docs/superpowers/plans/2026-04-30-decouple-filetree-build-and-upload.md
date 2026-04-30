# Decouple Filetree Build And Upload Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move filetree hash calculation into `FileTreeBuilder`, flatten staging to one file per directory id, replace `FileTreeBlob` with direct `IReadOnlyList<FileTreeEntry>` handling, and overlap filetree calculation with upload.

**Architecture:** `FileTreeBuilder` becomes the Merkle-node producer: it reads staged node files, validates duplicates, resolves child directory ids, sorts entries once, computes the hash, and publishes completed nodes to a bounded channel. `FileTreeService` becomes a pure immutable storage/cache boundary over `FileTreeHash` plus `IReadOnlyList<FileTreeEntry>`, while `FileTreeSerializer` unifies staged and persisted line handling without a `FileTreeBlob` wrapper.

**Tech Stack:** C# 12, .NET async/await, `System.Threading.Channels`, TUnit, existing Arius filetree/cache services

---

### Task 1: Replace `FileTreeBlob` With Direct Entry Serialization

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeModels.cs`
- Rename: `src/Arius.Core/Shared/FileTree/FileTreeBlobSerializer.cs` -> `src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs`
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeService.cs`
- Rename: `src/Arius.Core.Tests/Shared/FileTree/FileTreeBlobSerializerTests.cs` -> `src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs`
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs`

- [ ] **Step 1: Write the failing serializer and service tests**

Add or rename tests so they assert direct entry-list behavior instead of `FileTreeBlob` wrappers:

```csharp
[Test]
public void Serialize_IsDeterministic_ForEquivalentEntryLists()
{
    IReadOnlyList<FileTreeEntry> entries1 =
    [
        new FileEntry { Name = "b.jpg", ContentHash = FakeContentHash('2'), Created = s_created, Modified = s_modified },
        new FileEntry { Name = "a.jpg", ContentHash = FakeContentHash('1'), Created = s_created, Modified = s_modified }
    ];

    IReadOnlyList<FileTreeEntry> entries2 =
    [
        new FileEntry { Name = "a.jpg", ContentHash = FakeContentHash('1'), Created = s_created, Modified = s_modified },
        new FileEntry { Name = "b.jpg", ContentHash = FakeContentHash('2'), Created = s_created, Modified = s_modified }
    ];

    FileTreeSerializer.Serialize(entries1).ShouldBe(FileTreeSerializer.Serialize(entries2));
}

[Test]
public async Task EnsureStoredAsync_UsesProvidedHash_AndWritesMissingTree()
{
    var entries = (IReadOnlyList<FileTreeEntry>)
    [
        new FileEntry
        {
            Name = "readme.txt",
            ContentHash = ContentHash.Parse(new string('c', 64)),
            Created = DateTimeOffset.UnixEpoch,
            Modified = DateTimeOffset.UnixEpoch
        }
    ];

    var hash = FileTreeSerializer.ComputeHash(entries, encryption);
    var stored = await service.EnsureStoredAsync(hash, entries);

    stored.ShouldBe(hash);
    blobs.Uploaded.ShouldContain(BlobPaths.FileTree(hash));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeSerializerTests/*"
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeServiceTests/*EnsureStoredAsync_UsesProvidedHash_AndWritesMissingTree"
```

Expected: compile failures or test failures because `FileTreeSerializer` and the new `EnsureStoredAsync(hash, entries)` API do not exist yet.

- [ ] **Step 3: Write the minimal model and serializer implementation**

Update the model surface so only entry lists remain:

```csharp
public abstract record FileTreeEntry
{
    public required string Name { get; init; }
}

public sealed record FileEntry : FileTreeEntry
{
    public required ContentHash ContentHash { get; init; }
    public required DateTimeOffset Created { get; init; }
    public required DateTimeOffset Modified { get; init; }
}

public sealed record DirectoryEntry : FileTreeEntry
{
    public required FileTreeHash FileTreeHash { get; init; }
}
```

Rename and reshape the serializer:

```csharp
public static class FileTreeSerializer
{
    public static byte[] Serialize(IReadOnlyList<FileTreeEntry> entries) { ... }

    public static IReadOnlyList<FileTreeEntry> Deserialize(byte[] bytes) { ... }

    public static async Task<byte[]> SerializeForStorageAsync(
        IReadOnlyList<FileTreeEntry> entries,
        IEncryptionService encryption,
        CancellationToken cancellationToken = default) { ... }

    public static async Task<IReadOnlyList<FileTreeEntry>> DeserializeFromStorageAsync(
        Stream source,
        IEncryptionService encryption,
        CancellationToken cancellationToken = default) { ... }

    public static FileTreeHash ComputeHash(
        IReadOnlyList<FileTreeEntry> entries,
        IEncryptionService encryption)
        => FileTreeHash.Parse(encryption.ComputeHash(Serialize(entries)));
}
```

Update `FileTreeService` to use entry lists directly:

```csharp
public async Task<IReadOnlyList<FileTreeEntry>> ReadAsync(FileTreeHash hash, CancellationToken cancellationToken = default)
{
    ...
}

public async Task WriteAsync(
    FileTreeHash hash,
    IReadOnlyList<FileTreeEntry> entries,
    CancellationToken cancellationToken = default)
{
    var storageBytes = await FileTreeSerializer.SerializeForStorageAsync(entries, _encryption, cancellationToken);
    ...
    var plaintext = FileTreeSerializer.Serialize(entries);
    await File.WriteAllBytesAsync(diskPath, plaintext, cancellationToken);
}

public async Task<FileTreeHash> EnsureStoredAsync(
    FileTreeHash hash,
    IReadOnlyList<FileTreeEntry> entries,
    CancellationToken cancellationToken = default)
{
    if (!ExistsInRemote(hash))
        await WriteAsync(hash, entries, cancellationToken);

    return hash;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run:

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeSerializerTests/*"
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeServiceTests/*"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreeModels.cs src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs src/Arius.Core/Shared/FileTree/FileTreeService.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs
git commit -m "refactor: remove filetree blob wrapper"
```

### Task 2: Flatten Staging To One Node File And Add `StagedDirectoryEntry`

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeStagingPaths.cs`
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`
- Modify: `src/Arius.Core/Shared/FileTree/StagedDirectoryEntry.cs`
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs`
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs`

- [ ] **Step 1: Write the failing staging and staged-entry tests**

Add tests that lock the new layout and staged parsing rules:

```csharp
[Test]
public async Task AppendFileEntryAsync_WritesSingleNodeFilePerDirectoryId()
{
    await using var session = await FileTreeStagingSession.OpenAsync(cacheDir);
    using var writer = new FileTreeStagingWriter(session.StagingRoot);

    await writer.AppendFileEntryAsync("photos/2024/a.jpg", TestHash, TestTimestamp, TestTimestamp);

    var rootId = FileTreeStagingPaths.GetDirectoryId(string.Empty);
    var photosId = FileTreeStagingPaths.GetDirectoryId("photos");
    var rootLines = await File.ReadAllLinesAsync(FileTreeStagingPaths.GetNodePath(session.StagingRoot, rootId));
    var photosLines = await File.ReadAllLinesAsync(FileTreeStagingPaths.GetNodePath(session.StagingRoot, photosId));

    rootLines.ShouldContain(line => line == $"{photosId} D photos/");
    photosLines.ShouldContain(line => line.Contains(" F "));
}

[Test]
public void ParseStagedEntryLine_DirectoryLine_ReturnsStagedDirectoryEntry()
{
    var directoryId = FileTreeStagingPaths.GetDirectoryId("photos");
    var parsed = FileTreeSerializer.ParseStagedEntryLine($"{directoryId} D photos/");

    parsed.ShouldBeOfType<StagedDirectoryEntry>()
        .DirectoryId.ShouldBe(directoryId);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeStagingWriterTests/*"
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeSerializerTests/*ParseStagedEntryLine*"
```

Expected: failures because `GetNodePath` and `ParseStagedEntryLine` do not exist and the writer still uses separate staging files.

- [ ] **Step 3: Write the minimal staging implementation**

Flatten staging paths:

```csharp
internal static class FileTreeStagingPaths
{
    public static string GetNodePath(string stagingRoot, string directoryId)
        => Path.Combine(stagingRoot, directoryId);
}
```

Make `StagedDirectoryEntry` a staged-only subtype:

```csharp
internal sealed record StagedDirectoryEntry : FileTreeEntry
{
    public required string DirectoryId { get; init; }

    public static StagedDirectoryEntry Parse(string line)
    {
        ...
        return new StagedDirectoryEntry
        {
            DirectoryId = directoryId,
            Name = name
        };
    }
}
```

Write both file and directory lines into the same node file:

```csharp
private async Task AppendFileEntryAsync(string directoryPath, FileEntry entry, CancellationToken cancellationToken)
{
    var directoryId = FileTreeStagingPaths.GetDirectoryId(directoryPath);
    var nodePath = FileTreeStagingPaths.GetNodePath(_stagingRoot, directoryId);
    await AppendLineAsync(nodePath, FileTreeSerializer.SerializeFileEntryLine(entry), cancellationToken);
}

private async Task AppendDirectoryEntriesAsync(string[] segments, CancellationToken cancellationToken)
{
    for (var depth = 0; depth < segments.Length - 1; depth++)
    {
        var parentPath = depth == 0 ? string.Empty : string.Join('/', segments, 0, depth);
        var childPath = string.Join('/', segments, 0, depth + 1);
        var childId = FileTreeStagingPaths.GetDirectoryId(childPath);
        var nodePath = FileTreeStagingPaths.GetNodePath(_stagingRoot, FileTreeStagingPaths.GetDirectoryId(parentPath));
        await AppendLineAsync(nodePath, $"{childId} D {segments[depth]}/", cancellationToken);
    }
}
```

Add the staged parser entry point:

```csharp
public static FileTreeEntry ParseStagedEntryLine(string line)
    => line.Contains(" F ", StringComparison.Ordinal)
        ? ParseFileEntryLine(line)
        : StagedDirectoryEntry.Parse(line);
```

- [ ] **Step 4: Run tests to verify they pass**

Run:

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeStagingWriterTests/*"
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeSerializerTests/*"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreeStagingPaths.cs src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs src/Arius.Core/Shared/FileTree/StagedDirectoryEntry.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs
git commit -m "refactor: flatten staged filetree nodes"
```

### Task 3: Decouple Builder Calculation From Upload And Enforce Duplicate Rules

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`
- Modify: `src/Arius.Integration.Tests/Shared/FileTree/FileTreeBuilderIntegrationTests.cs`

- [ ] **Step 1: Write the failing builder tests**

Add tests for duplicate validation and decoupled upload behavior:

```csharp
[Test]
public async Task SynchronizeAsync_DuplicateFileNamesInOneDirectory_Throws()
{
    var rootId = FileTreeStagingPaths.GetDirectoryId(string.Empty);
    var nodePath = FileTreeStagingPaths.GetNodePath(stagingRoot, rootId);

    await File.WriteAllLinesAsync(nodePath,
    [
        FileTreeSerializer.SerializeFileEntryLine(new FileEntry { Name = "a.txt", ContentHash = FakeContentHash('a'), Created = now, Modified = now }),
        FileTreeSerializer.SerializeFileEntryLine(new FileEntry { Name = "a.txt", ContentHash = FakeContentHash('b'), Created = now, Modified = now })
    ]);

    await Should.ThrowAsync<InvalidOperationException>(() => builder.SynchronizeAsync(stagingRoot));
}

[Test]
public async Task SynchronizeAsync_CalculatesSiblingNodes_WhileUploadsAreBlocked()
{
    var uploadGate = new CountingFileTreeUploadBlobContainerService();
    var builder = CreateBuilder(uploadGate, acct, cont, out var fileTreeService);
    await fileTreeService.ValidateAsync();

    var syncTask = builder.SynchronizeAsync(stagingRoot);

    (await uploadGate.WaitForTwoUploadsAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
    uploadGate.AllowUploads();
    (await syncTask).ShouldNotBeNull();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderTests/*"
```

Expected: failures because the builder still reads separate files, does not enforce duplicate file-name errors, and awaits upload inline.

- [ ] **Step 3: Write the minimal builder/channel implementation**

Reshape `SynchronizeAsync` into producer plus upload workers:

```csharp
public async Task<FileTreeHash?> SynchronizeAsync(string stagingRoot, CancellationToken cancellationToken = default)
{
    var channel = Channel.CreateBounded<(FileTreeHash Hash, IReadOnlyList<FileTreeEntry> Entries)>(SiblingSubtreeWorkers * 2);

    var uploadTasks = Enumerable.Range(0, SiblingSubtreeWorkers)
        .Select(_ => Task.Run(async () =>
        {
            await foreach (var node in channel.Reader.ReadAllAsync(cancellationToken))
                await _fileTreeService.EnsureStoredAsync(node.Hash, node.Entries, cancellationToken);
        }, cancellationToken))
        .ToArray();

    try
    {
        var rootHash = await BuildDirectoryAsync(FileTreeStagingPaths.GetDirectoryId(string.Empty), cancellationToken);
        channel.Writer.Complete();
        await Task.WhenAll(uploadTasks);
        return rootHash;
    }
    catch (Exception ex)
    {
        channel.Writer.TryComplete(ex);
        await Task.WhenAll(uploadTasks);
        throw;
    }
}
```

Read one staged node file and enforce the new duplicate rules:

```csharp
async Task<FileTreeHash?> BuildDirectoryAsync(string directoryId, CancellationToken ct)
{
    var files = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
    var directories = new Dictionary<string, StagedDirectoryEntry>(StringComparer.Ordinal);

    await foreach (var line in File.ReadLinesAsync(FileTreeStagingPaths.GetNodePath(stagingRoot, directoryId), ct))
    {
        if (string.IsNullOrWhiteSpace(line))
            continue;

        switch (FileTreeSerializer.ParseStagedEntryLine(line))
        {
            case FileEntry fileEntry:
                if (!files.TryAdd(fileEntry.Name, fileEntry))
                    throw new InvalidOperationException($"Duplicate staged file entry '{fileEntry.Name}'.");
                break;

            case StagedDirectoryEntry stagedDirectory:
                if (directories.TryGetValue(stagedDirectory.Name, out var existing)
                    && existing.DirectoryId != stagedDirectory.DirectoryId)
                {
                    throw new InvalidOperationException($"Conflicting staged directory entry '{stagedDirectory.Name}'.");
                }

                directories[stagedDirectory.Name] = stagedDirectory;
                break;
        }
    }

    ... build children, create final `List<FileTreeEntry>`, sort once, compute hash, enqueue to channel ...
}
```

Use the final canonical list for hashing and upload:

```csharp
entries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
var hash = FileTreeSerializer.ComputeHash(entries, _encryption);
await channel.Writer.WriteAsync((hash, entries), ct);
return hash;
```

- [ ] **Step 4: Run tests to verify they pass**

Run:

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderTests/*"
dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderIntegrationTests/*"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs src/Arius.Integration.Tests/Shared/FileTree/FileTreeBuilderIntegrationTests.cs
git commit -m "feat: decouple filetree build from upload"
```

### Task 4: Update Archive Integration, Docs, And Verify End-To-End

**Files:**
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`
- Modify: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs`
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs`
- Modify: `docs/decisions/adr-0006-build-filetrees-from-hashed-directory-staging.md`
- Modify: `docs/superpowers/specs/2026-04-30-decouple-filetree-build-and-upload-design.md`

- [ ] **Step 1: Write the failing archive/regression tests**

Add a focused regression test that locks the archive path to the new builder/service contract:

```csharp
[Test]
public async Task Handle_WhenTreeBuildSucceeds_WaitsForDecoupledFileTreeUploadsBeforeSnapshot()
{
    var result = await handler.Handle(command, CancellationToken.None);

    result.Success.ShouldBeTrue();
    result.RootHash.ShouldNotBeNull();
    snapshotService.CreatedSnapshots.ShouldHaveSingleItem();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/ArchiveRecoveryTests/*"
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeSerializerTests/*"
```

Expected: failures until the archive path and remaining serializer references are updated.

- [ ] **Step 3: Write the minimal integration updates**

Update the archive tail to use the new builder/service boundaries without changing archive semantics:

```csharp
await _fileTreeService.ValidateAsync(cancellationToken);
await _chunkIndex.FlushAsync(cancellationToken);

var treeBuilder = new FileTreeBuilder(_encryption, _fileTreeService);
var rootHash = await treeBuilder.SynchronizeAsync(stagingSession.StagingRoot, cancellationToken);

if (rootHash is not null)
{
    var latestSnapshot = await _snapshotSvc.ResolveAsync(cancellationToken: cancellationToken);
    ...
}
```

Update the ADR references so the docs match the new approved design:

```markdown
* one flat staging node file exists per directory id at `filetrees/.staging/{dirId}`
* `FileTreeBuilder` computes hashes and publishes upload candidates
* `FileTreeService` stores already-identified immutable nodes
```

- [ ] **Step 4: Run verification and slopwatch**

Run:

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/ArchiveRecoveryTests/*"
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeSerializerTests/*"
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderTests/*"
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeStagingWriterTests/*"
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTreeServiceTests/*"
dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderIntegrationTests/*"
dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/RoundtripTests/*"
slopwatch analyze --fail-on warning
```

Expected: PASS, and no slopwatch warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs docs/decisions/adr-0006-build-filetrees-from-hashed-directory-staging.md docs/superpowers/specs/2026-04-30-decouple-filetree-build-and-upload-design.md
git commit -m "refactor: decouple filetree build and upload"
```

## Self-Review

- Spec coverage: the plan covers builder-owned hashing, single-file staging nodes, staged-only directory entries, direct `IReadOnlyList<FileTreeEntry>` storage APIs, duplicate validation, decoupled upload, docs alignment, and verification.
- Placeholder scan: no `TODO`, `TBD`, or "handle appropriately" placeholders remain; every task includes explicit files, tests, commands, and commit messages.
- Type consistency: the plan consistently uses `FileTreeSerializer`, `StagedDirectoryEntry`, `GetNodePath`, and `EnsureStoredAsync(FileTreeHash hash, IReadOnlyList<FileTreeEntry> entries, ...)`.
