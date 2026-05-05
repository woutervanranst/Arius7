# FileTree Test Entry Helpers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Centralize repeated `FileEntry` and `DirectoryEntry` test construction in `Arius.Tests.Shared` and refactor current matching test call sites to use the shared typed helpers.

**Architecture:** Add one focused helper class under `src/Arius.Tests.Shared/FileTree/` that constructs typed filetree entries from already-typed `PathSegment` values. Then remove the duplicate local helpers from `ListQueryHandlerTests` and replace matching direct object literals in `FileTreeSerializerTests` where the shared helper improves clarity without hiding test intent.

**Tech Stack:** C# / .NET / TUnit

---

### File Structure

**Create**
- `src/Arius.Tests.Shared/FileTree/FileTreeEntryHelper.cs`

**Modify**
- `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`
- `src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs`

**Potentially modify if compile fallout requires it**
- `src/Arius.Core.Tests/Usings.cs`

**Do not modify in this cleanup**
- production filetree code
- add string overloads
- broaden into unrelated test DSL helpers

Reason: this is a narrow shared-helper cleanup for existing typed test construction patterns.

---

### Task 1: Add Shared Typed FileTree Entry Helpers

**Files:**
- Create: `src/Arius.Tests.Shared/FileTree/FileTreeEntryHelper.cs`
- Test: `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`

- [ ] **Step 1: Write the failing test usage**

Update `ListQueryHandlerTests` to call shared helper methods and remove the private local helper methods at the bottom of the file.

Representative usage:

```csharp
var tree = Entries(
    DirectoryEntryOf(SegmentOf("child"), childHash),
    FileEntryOf(SegmentOf("known.txt"), ContentHashFor("known"), s_created, s_modified));
```

And remove:

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

- [ ] **Step 2: Run test to verify it fails**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/*"
```

Expected:
- FAIL or compile break because the shared helper does not exist yet or is not imported.

- [ ] **Step 3: Write minimal implementation**

Create the helper class with typed-only overloads:

```csharp
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Paths;

namespace Arius.Tests.Shared.FileTree;

public static class FileTreeEntryHelper
{
    public static FileEntry FileEntryOf(PathSegment name, ContentHash hash, DateTimeOffset created, DateTimeOffset modified) => new()
    {
        Name = name,
        ContentHash = hash,
        Created = created,
        Modified = modified
    };

    public static DirectoryEntry DirectoryEntryOf(PathSegment name, FileTreeHash hash) => new()
    {
        Name = name,
        FileTreeHash = hash
    };
}
```

If needed for test visibility, add a global using in `src/Arius.Core.Tests/Usings.cs`:

```csharp
global using static Arius.Tests.Shared.FileTree.FileTreeEntryHelper;
```

- [ ] **Step 4: Run test to verify it passes**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/*"
```

Expected:
- PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Tests.Shared/FileTree/FileTreeEntryHelper.cs src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs src/Arius.Core.Tests/Usings.cs
git commit -m "test: share filetree entry test helpers"
```

---

### Task 2: Refactor Matching Serializer Test Construction Sites

**Files:**
- Modify: `src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs`

- [ ] **Step 1: Write the failing refactor**

Replace the matching direct object literals in `FileTreeSerializerTests` with the shared helper methods where the shape is identical.

Representative updates:

```csharp
entries.AddRange(items.Select(item => (FileTreeEntry)(item.isDirectory
    ? DirectoryEntryOf(SegmentOf(item.name), FileTreeHash.Parse(NormalizeHash(item.hash)))
    : FileEntryOf(SegmentOf(item.name), ContentHash.Parse(NormalizeHash(item.hash)), s_created, s_modified))));

var entry = DirectoryEntryOf(SegmentOf("photos"), FakeFileTreeHash('d'));

IReadOnlyList<FileTreeEntry> entries =
[
    DirectoryEntryOf(SegmentOf("sub"), FileTreeHash.Parse(NormalizeHash("abc"))),
    FileEntryOf(SegmentOf("f.txt"), ContentHash.Parse(NormalizeHash("def")), s_created, s_modified)
];
```

Do not refactor persisted-line tests that intentionally construct bespoke `FileEntry` records for exact equality assertions unless the helper keeps the test equally clear.

- [ ] **Step 2: Run test to verify it still passes**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeSerializerTests/*"
```

Expected:
- PASS

- [ ] **Step 3: Run the combined focused verification**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/*|/*/*/FileTreeSerializerTests/*"
```

Expected:
- PASS

- [ ] **Step 4: Commit**

```bash
git add src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs
git commit -m "test: reuse shared filetree entry helpers"
```

---

### Self-Review

- Spec coverage: this plan covers the slice-5 goal of removing repeated test-local helper duplication for path-semantic FileTree entries.
- Placeholder scan: no placeholders remain.
- Type consistency: helpers stay typed-only with `PathSegment`, `ContentHash`, and `FileTreeHash`; no string overloads are introduced.
