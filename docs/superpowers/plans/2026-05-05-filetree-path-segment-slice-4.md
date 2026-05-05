# Filetree PathSegment Slice 4 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Type filetree child names as `PathSegment` and remove trailing-slash directory naming from both the in-memory filetree model and persisted/staged filetree text.

**Architecture:** This slice retypes `FileTreeEntry.Name` and the filetree serializer boundary together so filetree child identity is one canonical segment everywhere. The `F` / `D` entry marker continues to carry file-vs-directory kind, while `FileTreeStagingWriter`, `FileTreeBuilder`, and affected tests move to typed child-name handling with only natural compile-fallout cleanup outside the filetree area.

**Tech Stack:** C# / .NET / TUnit

---

### File Structure

**Modify**
- `src/Arius.Core/Shared/FileTree/FileTreeModels.cs`
- `src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs`
- `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`
- `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`
- `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs`
- `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`
- `src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs`
- `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`
- `src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs`
- `src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs`
- `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs`

**Likely delete**
- `src/Arius.Core/Shared/FileTree/DirectoryEntryExtensions.cs`

**Potentially modify if compile fallout requires it**
- small filetree-adjacent helpers in `src/Arius.Tests.Shared/`
- tests that directly construct `FileEntry` / `DirectoryEntry` with string names

**Do not modify in this slice**
- `src/Arius.Core/Features/ArchiveCommand/` beyond fallout from filetree entry-name assertions
- CLI / Explorer behavior
- E2E dataset APIs unless a direct compile break forces a narrow fix

Reason: this slice is the filetree model and persistence cleanup, not another broad feature-boundary migration.

---

### Task 1: Retype Filetree Entry Names To `PathSegment`

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeModels.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs`

- [ ] **Step 1: Write failing tests for typed in-memory names**

Update the focused serializer tests so filetree entries are created and asserted with `PathSegment` instead of raw strings.

Representative updates:

```csharp
[Test]
public void Serialize_ThenDeserialize_RoundTrips()
{
    IReadOnlyList<FileTreeEntry> entries =
    [
        new FileEntry
        {
            Name = SegmentOf("photo.jpg"),
            ContentHash = FakeContentHash('a'),
            Created = s_created,
            Modified = s_modified
        },
        new DirectoryEntry
        {
            Name = SegmentOf("subdir"),
            FileTreeHash = FakeFileTreeHash('e')
        }
    ];

    var bytes = FileTreeSerializer.Serialize(entries);
    var back = FileTreeSerializer.Deserialize(bytes);

    back.Single(e => e.Name == SegmentOf("photo.jpg")).ShouldBeOfType<FileEntry>();
    back.Single(e => e.Name == SegmentOf("subdir")).ShouldBeOfType<DirectoryEntry>();
}
```

Also replace the `GetDirectoryName_TrimsSerializerSlashAndParsesSegment` test with a typed-name assertion that demonstrates a `DirectoryEntry` already carries `SegmentOf("photos")` in memory.

- [ ] **Step 2: Run tests to verify RED**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeSerializerTests/*"
```

Expected:
- FAIL or compile break because `FileTreeEntry.Name` is still `string`

- [ ] **Step 3: Change `FileTreeEntry.Name` to `PathSegment`**

Target shape:

```csharp
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Paths;

namespace Arius.Core.Shared.FileTree;

public abstract record FileTreeEntry
{
    public required PathSegment Name { get; init; }
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

internal sealed record StagedDirectoryEntry : FileTreeEntry
{
    public required string DirectoryNameHash { get; init; }
}
```

- [ ] **Step 4: Run tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeSerializerTests/*|/*/*/PathSegmentTests/*"
```

Expected:
- PASS for the updated typed-name assertions, or narrower remaining failures limited to serializer/writer fallout to be fixed next

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreeModels.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs
git commit -m "refactor: type filetree entry names"
```

---

### Task 2: Remove Slash-Suffixed Directory Names From Filetree Persistence

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs`

- [ ] **Step 1: Write failing serializer tests for bare directory segment persistence**

Update the serializer tests so persisted and staged directory lines use bare names instead of slash-suffixed names.

Representative test updates:

```csharp
[Test]
public void ParseStagedNodeEntryLine_DirectoryLine_ReturnsStagedDirectoryEntry()
{
    var directoryId = FileTreePaths.GetStagingDirectoryId(RelativePath.Parse("photos"));

    var parsed = FileTreeSerializer.ParseStagedNodeEntryLine($"{directoryId} D photos");

    var entry = parsed.ShouldBeOfType<StagedDirectoryEntry>();
    entry.DirectoryNameHash.ShouldBe(directoryId);
    entry.Name.ShouldBe(SegmentOf("photos"));
}

[Test]
public void ParsePersistedNodeEntryLine_DirectoryLine_ReturnsDirectoryEntry()
{
    var hash = FakeFileTreeHash('d');

    var parsed = FileTreeSerializer.ParsePersistedNodeEntryLine($"{hash} D photos");

    var entry = parsed.ShouldBeOfType<DirectoryEntry>();
    entry.FileTreeHash.ShouldBe(hash);
    entry.Name.ShouldBe(SegmentOf("photos"));
}
```

Add one explicit test that serialized directory plaintext contains `" D subdir"` and does **not** contain `" D subdir/"`.

Update invalid-name coverage so `"photos/"`, `"photos//"`, `"photos/2024"`, and `"photos\\"` are rejected.

- [ ] **Step 2: Run tests to verify RED**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeSerializerTests/*"
```

Expected:
- FAIL because serializer/parser still emit and accept slash-suffixed directory names

- [ ] **Step 3: Update serializer and parser to use bare `PathSegment` names**

Implementation shape:

```csharp
public static string SerializePersistedFileEntryLine(FileEntry entry)
    => $"{entry.ContentHash} F {entry.Created:O} {entry.Modified:O} {entry.Name}";

public static string SerializePersistedDirectoryEntryLine(DirectoryEntry entry)
    => SerializePersistedDirectoryEntryLine(entry.FileTreeHash, entry.Name);

public static string SerializePersistedDirectoryEntryLine(FileTreeHash hash, PathSegment name)
    => $"{hash} D {name}";

public static string SerializePersistedDirectoryEntryLine(string hash, PathSegment name)
    => $"{hash} D {name}";
```

Parse directory names through `PathSegment.Parse(afterType)` for both persisted and staged directory lines.

Update ordering to sort by `entry.Name.ToString()` with ordinal comparison.

- [ ] **Step 4: Remove slash-based directory-name validation**

Replace the current `ValidateCanonicalDirectoryEntryName(...)` logic with single-segment validation through `PathSegment.Parse(...)`.

Target shape:

```csharp
private static PathSegment ParseDirectoryEntryName(string name, string line)
{
    try
    {
        return PathSegment.Parse(name);
    }
    catch (ArgumentException ex)
    {
        throw new FormatException($"Invalid directory entry (non-canonical name): '{line}'", ex);
    }
}
```

- [ ] **Step 5: Run tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeSerializerTests/*|/*/*/PathSegmentTests/*"
```

Expected:
- PASS

- [ ] **Step 6: Commit**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs src/Arius.Core/Shared/FileTree/FileTreeModels.cs
git commit -m "refactor: remove slash-shaped filetree directory names"
```

---

### Task 3: Retype Filetree Staging Writer Around Segment Names

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs`

- [ ] **Step 1: Write failing staging-writer tests for bare directory names**

Update or add focused tests asserting that staged node lines for directories no longer contain a trailing slash.

Representative test shape:

```csharp
[Test]
public async Task AppendFileEntryAsync_NestedFile_WritesBareDirectorySegmentNames()
{
    using var temp = new TempDirectory();
    using var writer = new FileTreeStagingWriter(temp.Path);

    await writer.AppendFileEntryAsync(
        RelativePath.Parse("photos/2024/a.jpg"),
        FakeContentHash('a'),
        s_created,
        s_modified);

    var rootId = FileTreePaths.GetStagingDirectoryId(RelativePath.Root);
    var rootLines = await File.ReadAllLinesAsync(FileTreePaths.GetStagingNodePath(temp.Path, rootId));

    rootLines.ShouldContain(line => line.EndsWith(" D photos", StringComparison.Ordinal));
    rootLines.ShouldNotContain(line => line.EndsWith(" D photos/", StringComparison.Ordinal));
}
```

- [ ] **Step 2: Run tests to verify RED**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeStagingWriterTests/*"
```

Expected:
- FAIL because the writer still appends `"/"` to directory names

- [ ] **Step 3: Change staging writer to derive typed names from `RelativePath` structure**

Use `filePath.Name` for file entries and `directoryPath.Name` for directory entries instead of `string.Split('/')` plus string concatenation.

Implementation shape:

```csharp
public async Task AppendFileEntryAsync(
    RelativePath filePath,
    ContentHash contentHash,
    DateTimeOffset created,
    DateTimeOffset modified,
    CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();

    var fileName = filePath.Name
        ?? throw new ArgumentException("File path must include a file name.", nameof(filePath));

    var parentPath = filePath.Parent ?? RelativePath.Root;

    await AppendDirectoryEntriesAsync(filePath, cancellationToken);
    await AppendFileEntryAsync(parentPath, new FileEntry
    {
        Name = fileName,
        ContentHash = contentHash,
        Created = created,
        Modified = modified
    }, cancellationToken);
}
```

In directory staging, build parent/child `RelativePath` values with typed path operations and pass `directoryPath.Name!.Value` only as `PathSegment`, not as slash-shaped text.

- [ ] **Step 4: Run tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeStagingWriterTests/*|/*/*/FileTreeSerializerTests/*"
```

Expected:
- PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs
git commit -m "refactor: use typed filetree segment names in staging"
```

---

### Task 4: Retype Filetree Builder And Remove Directory Name Adapter

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`
- Modify: `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs`
- Modify: `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`
- Delete: `src/Arius.Core/Shared/FileTree/DirectoryEntryExtensions.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`

- [ ] **Step 1: Write failing builder tests for typed duplicate detection and bare staged names**

Update the existing builder tests to construct filetree entries with `SegmentOf(...)` and staged lines like `"{directoryId} D child"` instead of `"child/"`.

Representative updates:

```csharp
Should.Throw<FormatException>(() => FileTreeSerializer.ParseStagedNodeEntryLine("not-a-directory-id D child"));

ex.Message.ShouldContain("Duplicate staged file entry 'a.txt'.");
```

Update the non-canonical-name arguments so slash-containing values are rejected without expecting a trailing slash canonical form:

```csharp
[Arguments("child/grandchild")]
[Arguments("child\\")]
[Arguments(".")]
[Arguments("..")]
```

- [ ] **Step 2: Run tests to verify RED**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeBuilderTests/*|/*/*/FileTreeSerializerTests/*"
```

Expected:
- FAIL because builder dictionaries and caller code still assume string/slash directory names

- [ ] **Step 3: Change builder dictionaries and conversions to use typed names**

Target changes:

```csharp
var fileEntries = new Dictionary<PathSegment, FileEntry>();
var directoryEntries = new Dictionary<PathSegment, StagedDirectoryEntry>();
```

And:

```csharp
if (!fileEntries.TryAdd(fileEntry.Name, fileEntry))
    throw new InvalidOperationException($"Duplicate staged file entry '{fileEntry.Name}'.");
```

The recursive `DirectoryEntry` materialization should pass the typed `PathSegment` name directly.

- [ ] **Step 4: Remove `DirectoryEntryExtensions` and update callers**

Replace:

```csharp
var dirPath = currentPath / directoryEntry.GetDirectoryName();
```

with:

```csharp
var dirPath = currentPath / directoryEntry.Name;
```

Do the same in `ListQueryHandler`, including the child-directory lookup comparison by `PathSegment` instead of parsing/trimming via the extension.

Delete `src/Arius.Core/Shared/FileTree/DirectoryEntryExtensions.cs` once there are no remaining callers.

- [ ] **Step 5: Run tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeBuilderTests/*|/*/*/RestoreCommandHandlerTests/*|/*/*/ListQueryHandlerTests/*|/*/*/FileTreeSerializerTests/*"
```

Expected:
- PASS

- [ ] **Step 6: Commit**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs src/Arius.Core/Features/ListQuery/ListQueryHandler.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs
git rm src/Arius.Core/Shared/FileTree/DirectoryEntryExtensions.cs
git commit -m "refactor: remove slash-shaped filetree directory adapters"
```

---

### Task 5: Clean Up Direct Filetree Entry Construction In Affected Tests

**Files:**
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs`
- Modify: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs`
- Potentially modify: additional directly affected tests found by compile fallout

- [ ] **Step 1: Write or update failing assertions to use typed names**

Update direct construction and assertions.

Representative updates:

```csharp
var docsDirectory = rootEntries
    .OfType<DirectoryEntry>()
    .Single(entry => entry.Name == SegmentOf("docs"));

docsEntries.OfType<FileEntry>().Single(entry => entry.Name == SegmentOf("readme.txt"));
```

And:

```csharp
new DirectoryEntry
{
    Name = SegmentOf("subdir"),
    FileTreeHash = FakeFileTreeHash('d')
}
```

- [ ] **Step 2: Run tests to verify RED**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ArchiveRecoveryTests/*|/*/*/FileTreeServiceTests/*"
```

Expected:
- FAIL or compile break because tests still construct or assert string/slash names

- [ ] **Step 3: Apply the minimal fallout cleanup**

Retype direct test construction and assertions to `SegmentOf(...)` / `PathSegment` equality. Do not redesign helpers beyond what compile fallout requires.

- [ ] **Step 4: Run tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ArchiveRecoveryTests/*|/*/*/FileTreeServiceTests/*|/*/*/FileTreeSerializerTests/*|/*/*/FileTreeBuilderTests/*|/*/*/FileTreeStagingWriterTests/*"
```

Expected:
- PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs
git commit -m "test: update filetree name assertions to path segments"
```

---

### Task 6: Verify The Slice End-To-End

**Files:**
- Verify only:
  - `src/Arius.Core/Shared/FileTree/`
  - filetree-adjacent restore/list callers
  - affected tests

- [ ] **Step 1: Run focused filetree and fallout regressions**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeSerializerTests/*|/*/*/FileTreeBuilderTests/*|/*/*/FileTreeStagingWriterTests/*|/*/*/FileTreeServiceTests/*|/*/*/ArchiveRecoveryTests/*|/*/*/RestoreCommandHandlerTests/*|/*/*/ListQueryHandlerTests/*"
```

Expected:
- PASS

- [ ] **Step 2: Run the full core test project**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj"
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

- `PathSegment` is the only child-name identity type for this slice.
- The `F` / `D` type marker is sufficient to represent file-vs-directory kind in persisted and staged filetree text.
- Do not keep slash-suffixed canonical directory names as a second parallel convention.
- Prefer using `RelativePath.Name`, `directoryEntry.Name`, and typed dictionary keys over parsing or trimming strings.
- Remove feature-local or filetree-local adapters that only existed to undo slash-shaped directory names.
- If compile fallout reaches tests outside the named files, keep those edits minimal and directly tied to the typed-name change.

### Self-Review

**Spec coverage**
- in-memory filetree entry names become `PathSegment`: Task 1.
- persisted and staged directory lines drop trailing slash naming: Task 2.
- writer and builder move to typed child-name handling: Tasks 3-4.
- natural test/helper fallout is covered without broad redesign: Task 5.
- full verification and slopwatch evidence: Task 6.

**Placeholder scan**
- No `TODO` / `TBD` placeholders.
- Each task names exact files, commands, and expected results.
- Code steps include concrete code shapes for the changed APIs and assertions.

**Type consistency**
- `FileTreeEntry.Name`, `FileEntry.Name`, `DirectoryEntry.Name`, and `StagedDirectoryEntry.Name` are consistently `PathSegment`.
- directory text persistence is consistently bare segment text with no slash convention.
- filetree caller updates consistently use typed `PathSegment` values rather than string trimming helpers.
