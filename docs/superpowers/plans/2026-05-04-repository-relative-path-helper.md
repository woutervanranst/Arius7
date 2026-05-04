# Repository Relative Path Helper Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the duplicated canonical repository-relative path validation into one shared helper, keep `""` as the internal root sentinel, and migrate the filetree production callers to it without broadening into unrelated normalization or safe-join behavior.

**Architecture:** Add one narrow shared helper in `Arius.Core.Shared` that owns canonical repository-relative path validation rules. Use it from filetree staging and staging-directory-id generation first; keep query-prefix normalization and fixture containment helpers separate because they solve different problems.

**Tech Stack:** C# / .NET / TUnit

---

### File Structure

**Create**
- `src/Arius.Core/Shared/RepositoryRelativePath.cs`
- `src/Arius.Core.Tests/Shared/RepositoryRelativePathTests.cs`

**Modify**
- `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`
- `src/Arius.Core/Shared/FileTree/FileTreePaths.cs`
- `src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs`
- `src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs`
- `src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs`

**Do not modify in this refactor**
- `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs`
- `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`
- `src/Arius.Core/Shared/LocalFile/LocalFileEnumerator.cs`
- `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`
- `src/Arius.E2E.Tests/Fixtures/E2EFixture.cs`
- `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryDefinition.cs`

Reason: those are adjacent but have different semantics: loose prefix normalization, safe local join, and synthetic-dataset validation.

---

### Planned Helper Contract

Recommended API shape:

```csharp
namespace Arius.Core.Shared;

internal static class RepositoryRelativePath
{
    public static void ValidateCanonical(string path, string paramName, bool allowEmpty = false);
}
```

Rules:
- reject `null`, empty, or whitespace unless `allowEmpty: true` and the value is exactly `""`
- reject rooted / absolute paths
- reject drive-rooted `C:/...`
- reject backslashes
- reject repeated separators / empty segments
- reject `.` and `..`
- reject control chars `\r`, `\n`, `\0`
- reject whitespace-only segments
- preserve spaces inside otherwise valid segments
- canonical form is bare relative path only, e.g. `photos/2024/a.jpg`
- internal root sentinel is `""`

Keep staged directory entry validation separate from plain relative-path validation, because `photos/` is an entry-name shape, not a canonical path shape.

---

### Task 1: Add Failing Shared Helper Tests

**Files:**
- Create: `src/Arius.Core.Tests/Shared/RepositoryRelativePathTests.cs`
- Check existing patterns: `src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs`

- [ ] **Step 1: Write the failing test file**

```csharp
using Arius.Core.Shared;

namespace Arius.Core.Tests.Shared;

public class RepositoryRelativePathTests
{
    [Test]
    [Arguments("photos/a.jpg")]
    [Arguments("photos/2024/a.jpg")]
    [Arguments(" photos/a.jpg ")]
    public void ValidateCanonical_ValidCanonicalPath_DoesNotThrow(string path)
    {
        RepositoryRelativePath.ValidateCanonical(path, nameof(path));
    }

    [Test]
    public void ValidateCanonical_EmptyPathAllowedForRootSentinel_DoesNotThrow()
    {
        RepositoryRelativePath.ValidateCanonical(string.Empty, "path", allowEmpty: true);
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("/photos")]
    [Arguments("/photos/a.jpg")]
    [Arguments("C:/photos/a.jpg")]
    [Arguments("C:\\photos\\a.jpg")]
    [Arguments("photos\\a.jpg")]
    [Arguments("photos//a.jpg")]
    [Arguments("photos/./a.jpg")]
    [Arguments("photos/../a.jpg")]
    [Arguments("photos/\r/a.jpg")]
    [Arguments("photos/\n/a.jpg")]
    [Arguments("photos/\0/a.jpg")]
    [Arguments("photos/   /a.jpg")]
    public void ValidateCanonical_InvalidPath_ThrowsArgumentException(string path)
    {
        Should.Throw<ArgumentException>(() => RepositoryRelativePath.ValidateCanonical(path, nameof(path)));
    }

    [Test]
    public void ValidateCanonical_EmptyPathWithoutAllowEmpty_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => RepositoryRelativePath.ValidateCanonical(string.Empty, "path"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RepositoryRelativePathTests/*"
```

Expected:
- FAIL with compile error because `RepositoryRelativePath` does not exist yet

- [ ] **Step 3: Commit**

```bash
git add src/Arius.Core.Tests/Shared/RepositoryRelativePathTests.cs
git commit -m "test: define repository relative path rules"
```

---

### Task 2: Implement The Shared Helper

**Files:**
- Create: `src/Arius.Core/Shared/RepositoryRelativePath.cs`
- Test: `src/Arius.Core.Tests/Shared/RepositoryRelativePathTests.cs`

- [ ] **Step 1: Write minimal implementation**

```csharp
namespace Arius.Core.Shared;

internal static class RepositoryRelativePath
{
    public static void ValidateCanonical(string path, string paramName, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Length == 0)
        {
            if (allowEmpty)
                return;

            throw new ArgumentException("Path must be a canonical relative path.", paramName);
        }

        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith('/', StringComparison.Ordinal)
            || Path.IsPathRooted(path)
            || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/'))
        {
            throw new ArgumentException("Path must be a canonical relative path.", paramName);
        }

        var segments = path.Split('/');

        foreach (var segment in segments)
        {
            if (segment.Length == 0
                || string.IsNullOrWhiteSpace(segment)
                || segment is "." or ".."
                || segment.Contains('\\')
                || segment.IndexOfAny(['\r', '\n', '\0']) >= 0)
            {
                throw new ArgumentException("Path must be a canonical relative path.", paramName);
            }
        }
    }
}
```

- [ ] **Step 2: Run helper tests to verify they pass**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RepositoryRelativePathTests/*"
```

Expected:
- PASS

- [ ] **Step 3: Commit**

```bash
git add src/Arius.Core/Shared/RepositoryRelativePath.cs src/Arius.Core.Tests/Shared/RepositoryRelativePathTests.cs
git commit -m "refactor: add repository relative path helper"
```

---

### Task 3: Migrate `FileTreeStagingWriter` To The Helper

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs`

- [ ] **Step 1: Replace the local validator with the shared helper**

Target change:
- remove the local `ValidateCanonicalRelativePath` function
- call the shared helper directly

Relevant code shape:

```csharp
public async Task AppendFileEntryAsync(
    string filePath,
    ContentHash contentHash,
    DateTimeOffset created,
    DateTimeOffset modified,
    CancellationToken cancellationToken = default)
{
    ArgumentException.ThrowIfNullOrEmpty(filePath);
    cancellationToken.ThrowIfCancellationRequested();

    RepositoryRelativePath.ValidateCanonical(filePath, nameof(filePath));

    var segments = filePath.Split('/');
    if (segments.Length == 0)
        throw new ArgumentException("File path must include a file name.", nameof(filePath));

    var fileName = segments[^1];
    if (string.IsNullOrWhiteSpace(fileName))
        throw new ArgumentException("File path must include a non-empty file name.", nameof(filePath));

    var parentPath = segments.Length == 1 ? string.Empty : string.Join('/', segments, 0, segments.Length - 1);

    await AppendDirectoryEntriesAsync(segments, cancellationToken);
    await AppendFileEntryAsync(parentPath, new FileEntry
    {
        Name        = fileName,
        ContentHash = contentHash,
        Created     = created,
        Modified    = modified
    }, cancellationToken);
}
```

- [ ] **Step 2: Run staging-writer tests**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeStagingWriterTests/*"
```

Expected:
- PASS

- [ ] **Step 3: Commit**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs
git commit -m "refactor: share filetree staging path validation"
```

---

### Task 4: Migrate `FileTreePaths.GetStagingDirectoryId`

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreePaths.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs`

- [ ] **Step 1: Replace local absolute-path checks with shared validation**

Recommended implementation shape:

```csharp
public static string GetStagingDirectoryId(string directoryPath)
{
    ArgumentNullException.ThrowIfNull(directoryPath);

    RepositoryRelativePath.ValidateCanonical(directoryPath, nameof(directoryPath), allowEmpty: true);

    return HashCodec.ToLowerHex(SHA256.HashData(Encoding.UTF8.GetBytes(directoryPath)));
}
```

This intentionally preserves:
- `""` as valid root sentinel
- bare canonical paths like `photos/2024`
- rejection of rooted or non-canonical paths

- [ ] **Step 2: Run focused filetree tests**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeStagingWriterTests/*"
```

Expected:
- PASS

- [ ] **Step 3: Commit**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreePaths.cs
git commit -m "refactor: reuse shared staging directory path validation"
```

---

### Task 5: Decide And Apply The `FileTreeSerializer` Boundary

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs`

Goal:
- reduce duplicated canonical-name checks
- without collapsing directory entry names like `photos/` into plain relative-path semantics

Recommended implementation:
- add a small private helper inside `FileTreeSerializer`, for example `ValidateCanonicalDirectoryEntryName`
- implement it by:
  - requiring trailing `/`
  - rejecting bare `/`
  - trimming the trailing `/`
  - calling `RepositoryRelativePath.ValidateCanonical(trimmed, paramName)`
- use that helper in `ParseStagedDirectoryEntryLine`

- [ ] **Step 1: Add failing serializer tests for boundary behavior if missing**

Suggested additions:

```csharp
[Test]
public void ParseStagedNodeEntryLine_WhitespaceOnlyDirectorySegment_Throws()
{
    var directoryId = FileTreePaths.GetStagingDirectoryId("photos");

    Should.Throw<FormatException>(() =>
        FileTreeSerializer.ParseStagedNodeEntryLine($"{directoryId} D    /"));
}

[Test]
public void ParseStagedNodeEntryLine_CanonicalDirectoryName_RoundTrips()
{
    var directoryId = FileTreePaths.GetStagingDirectoryId("photos");

    var parsed = FileTreeSerializer.ParseStagedNodeEntryLine($"{directoryId} D photos/");

    parsed.ShouldBeOfType<StagedDirectoryEntry>().Name.ShouldBe("photos/");
}
```

- [ ] **Step 2: Implement the narrow serializer-local directory-entry-name helper**

Code shape:

```csharp
private static void ValidateCanonicalDirectoryEntryName(string name, string line)
{
    if (!name.EndsWith("/", StringComparison.Ordinal) || name.Length == 1)
        throw new FormatException($"Invalid staged directory entry (non-canonical name): '{line}'");

    try
    {
        RepositoryRelativePath.ValidateCanonical(name[..^1], nameof(name));
    }
    catch (ArgumentException ex)
    {
        throw new FormatException($"Invalid staged directory entry (non-canonical name): '{line}'", ex);
    }
}
```

Then call it from `ParseStagedDirectoryEntryLine`.

- [ ] **Step 3: Run serializer tests**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeSerializerTests/*"
```

Expected:
- PASS

- [ ] **Step 4: Commit**

```bash
git add src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs
git commit -m "refactor: share staged directory name path validation"
```

---

### Task 6: Run Combined Verification

**Files:**
- Verify only:
  - `src/Arius.Core/Shared/RepositoryRelativePath.cs`
  - `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`
  - `src/Arius.Core/Shared/FileTree/FileTreePaths.cs`
  - `src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs`
  - related tests

- [ ] **Step 1: Run the full relevant test slice**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RepositoryRelativePathTests/*|/*/*/FileTreeStagingWriterTests/*|/*/*/FileTreeSerializerTests/*"
```

Expected:
- PASS

- [ ] **Step 2: Run the full core test project if the focused slice passes**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj"
```

Expected:
- PASS

- [ ] **Step 3: Commit final integration if needed**

```bash
git add src/Arius.Core/Shared/RepositoryRelativePath.cs src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs src/Arius.Core/Shared/FileTree/FileTreePaths.cs src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs src/Arius.Core.Tests/Shared/RepositoryRelativePathTests.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs
git commit -m "refactor: centralize repository relative path validation"
```

---

### Notes For The Implementer

- Keep the helper `internal`, not `public`.
- Do not expand this refactor into `RestoreCommandHandler`, `ListQueryHandler`, or `LocalFileEnumerator`; those methods normalize looser input for matching rather than enforce canonical repository paths.
- Do not merge fixture safe-join helpers into this work; they are about root containment on local disk.
- Preserve current behavior that segments with surrounding spaces are valid as long as the segment is not whitespace-only.
- Preserve `""` as the internal root sentinel only where callers explicitly opt in with `allowEmpty: true`.

### Self-Review

**Spec coverage**
- Shared helper extraction: covered by Tasks 1-2.
- Production filetree migration: covered by Tasks 3-5.
- `""` root sentinel preserved: covered by Task 4 and helper contract.
- Avoid over-broad path utility: enforced by file scope and notes.

**Placeholder scan**
- No `TODO` / `TBD`.
- All code-touching steps include concrete code or target shape.
- All verification steps include exact commands.

**Type consistency**
- Helper name is consistently `RepositoryRelativePath`.
- Shared API is consistently `ValidateCanonical(string path, string paramName, bool allowEmpty = false)`.
