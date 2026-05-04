# Relative Path Domain Slice 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Introduce the first typed repository-path slice by replacing the helper-style `RepositoryRelativePath` validator with kind-neutral `PathSegment` and `RelativePath` value objects in a dedicated `Shared.Paths` namespace, while keeping production integration deliberately narrow.

**Architecture:** This slice builds the typed path foundation first, without trying to migrate archive, list, restore, and filetree in one change. `PathSegment` and `RelativePath` become the canonical repository-path types in `Arius.Core.Shared.Paths`, with stable ordinal case-sensitive identity and explicit `Root`. Existing string-based callers remain temporarily supported through compatibility-oriented parsing and validation surfaces so later slices can migrate feature models incrementally.

**Tech Stack:** C# / .NET / TUnit

---

### File Structure

**Create**
- `src/Arius.Core/Shared/Paths/PathSegment.cs`
- `src/Arius.Core/Shared/Paths/RelativePath.cs`
- `src/Arius.Core.Tests/Shared/PathSegmentTests.cs`
- `src/Arius.Core.Tests/Shared/RelativePathTests.cs`

**Modify**
- `src/Arius.Core/Shared/RepositoryRelativePath.cs`
- `src/Arius.Core.Tests/Shared/RepositoryRelativePathTests.cs`
- `src/Arius.Core/Shared/FileTree/FileTreePaths.cs`
- `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`
- `src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs`

**Do not modify in this slice**
- `src/Arius.Core/Features/ArchiveCommand/`
- `src/Arius.Core/Features/ListQuery/ListQuery.cs`
- `src/Arius.Core/Features/RestoreCommand/Models.cs`
- `src/Arius.Core/Shared/FileTree/FileTreeModels.cs`
- `src/Arius.Tests.Shared/`
- `src/Arius.E2E.Tests/`

Reason: this slice is only the type foundation plus narrow reuse at the existing helper integration points. Feature-model and filetree-model migrations belong to later slices.

---

### Target API Shape

`PathSegment` should be a `readonly record struct` with:

```csharp
namespace Arius.Core.Shared.Paths;

public readonly record struct PathSegment
{
    public static PathSegment Parse(string value);
    public static bool TryParse(string? value, out PathSegment segment);
    public override string ToString();
}
```

`RelativePath` should be a `readonly record struct` with:

```csharp
namespace Arius.Core.Shared.Paths;

public readonly record struct RelativePath
{
    public static RelativePath Root { get; }

    public bool IsRoot { get; }
    public int SegmentCount { get; }
    public PathSegment? Name { get; }
    public RelativePath? Parent { get; }

    public static RelativePath Parse(string value, bool allowEmpty = false);
    public static bool TryParse(string? value, out RelativePath path, bool allowEmpty = false);

    public bool StartsWith(RelativePath other);
    public RelativePath Append(PathSegment segment);

    public static RelativePath operator /(RelativePath left, PathSegment right);

    public override string ToString();
}
```

Required semantics:
- case-preserving
- ordinal, case-sensitive equality and hashing
- canonical separator `/`
- no trailing slash in identity form
- explicit root via `RelativePath.Root`
- root string form remains `""` for compatibility
- host filesystem matching rules are out of scope

Compatibility bridge for existing code in this slice:

```csharp
namespace Arius.Core.Shared;

using Arius.Core.Shared.Paths;

internal static class RepositoryRelativePath
{
    public static void ValidateCanonical(string path, bool allowEmpty = false)
        => RelativePath.Parse(path, allowEmpty);
}
```

---

### Task 1: Add Failing `PathSegment` Tests

**Files:**
- Create: `src/Arius.Core.Tests/Shared/PathSegmentTests.cs`

- [x] **Step 1: Write the failing tests**

```csharp
using Arius.Core.Shared;
using Arius.Core.Shared.Paths;

namespace Arius.Core.Tests.Shared;

public class PathSegmentTests
{
    [Test]
    [Arguments("photos")]
    [Arguments("2024 trip")]
    [Arguments(" report.pdf ")]
    public void Parse_ValidSegment_RoundTrips(string value)
    {
        var segment = PathSegment.Parse(value);

        segment.ToString().ShouldBe(value);
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments(".")]
    [Arguments("..")]
    [Arguments("photos/2024")]
    [Arguments("photos\\2024")]
    [Arguments("photos\r")]
    [Arguments("photos\n")]
    [Arguments("photos\0")]
    public void Parse_InvalidSegment_ThrowsArgumentException(string value)
    {
        Should.Throw<ArgumentException>(() => PathSegment.Parse(value));
    }

    [Test]
    public void Parse_NullSegment_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => PathSegment.Parse(null!));
    }
}
```

- [x] **Step 2: Run tests to verify RED**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/PathSegmentTests/*"
```

Expected:
- FAIL because `PathSegment` does not exist yet

- [ ] **Step 3: Commit**

```bash
git add src/Arius.Core.Tests/Shared/PathSegmentTests.cs
git commit -m "test: define path segment rules"
```

---

### Task 2: Implement `PathSegment`

**Files:**
- Create: `src/Arius.Core/Shared/Paths/PathSegment.cs`
- Test: `src/Arius.Core.Tests/Shared/PathSegmentTests.cs`

- [x] **Step 1: Write minimal implementation**

```csharp
namespace Arius.Core.Shared.Paths;

public readonly record struct PathSegment
{
    private PathSegment(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0 || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Path segment must not be empty or whitespace.", nameof(value));

        if (value is "." or ".."
            || value.Contains('/')
            || value.Contains('\\')
            || value.Contains('\r')
            || value.Contains('\n')
            || value.Contains('\0'))
        {
            throw new ArgumentException("Path segment must be canonical.", nameof(value));
        }

        Value = value;
    }

    private string Value => field ?? throw new InvalidOperationException("PathSegment is uninitialized.");

    public static PathSegment Parse(string value) => new(value);

    public static bool TryParse(string? value, out PathSegment segment)
    {
        try
        {
            if (value is null)
            {
                segment = default;
                return false;
            }

            segment = Parse(value);
            return true;
        }
        catch (ArgumentException)
        {
            segment = default;
            return false;
        }
    }

    public override string ToString() => Value;
}
```

- [x] **Step 2: Run tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/PathSegmentTests/*"
```

Expected:
- PASS

- [ ] **Step 3: Commit**

```bash
git add src/Arius.Core/Shared/Paths/PathSegment.cs src/Arius.Core.Tests/Shared/PathSegmentTests.cs
git commit -m "feat: add typed path segment"
```

---

### Task 3: Add Failing `RelativePath` Tests

**Files:**
- Create: `src/Arius.Core.Tests/Shared/RelativePathTests.cs`

- [x] **Step 1: Write the failing tests**

```csharp
using Arius.Core.Shared;
using Arius.Core.Shared.Paths;

namespace Arius.Core.Tests.Shared;

public class RelativePathTests
{
    [Test]
    public void Root_IsExplicitAndEmptyWhenFormatted()
    {
        RelativePath.Root.IsRoot.ShouldBeTrue();
        RelativePath.Root.ToString().ShouldBe(string.Empty);
    }

    [Test]
    public void Parse_CanonicalPath_RoundTripsAndExposesNameAndParent()
    {
        var path = RelativePath.Parse("photos/2024/a.jpg");

        path.IsRoot.ShouldBeFalse();
        path.SegmentCount.ShouldBe(3);
        path.Name.ShouldBe(PathSegment.Parse("a.jpg"));
        path.Parent.ShouldBe(RelativePath.Parse("photos/2024"));
        path.ToString().ShouldBe("photos/2024/a.jpg");
    }

    [Test]
    public void Parse_AllowEmpty_ReturnsRoot()
    {
        RelativePath.Parse(string.Empty, allowEmpty: true).ShouldBe(RelativePath.Root);
    }

    [Test]
    public void Equality_IsOrdinalAndCaseSensitive()
    {
        var lower = RelativePath.Parse("photos/a.jpg");
        var upper = RelativePath.Parse("Photos/a.jpg");

        lower.ShouldNotBe(upper);
    }

    [Test]
    public void Append_ComposesPaths()
    {
        var path = RelativePath.Root / PathSegment.Parse("photos") / PathSegment.Parse("a.jpg");

        path.ToString().ShouldBe("photos/a.jpg");
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("/photos")]
    [Arguments("C:/photos")]
    [Arguments("photos//a.jpg")]
    [Arguments("photos/./a.jpg")]
    [Arguments("photos/../a.jpg")]
    [Arguments("photos\\a.jpg")]
    public void Parse_InvalidPath_ThrowsArgumentException(string value)
    {
        Should.Throw<ArgumentException>(() => RelativePath.Parse(value));
    }
}
```

- [x] **Step 2: Run tests to verify RED**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RelativePathTests/*"
```

Expected:
- FAIL because `RelativePath` does not exist yet

- [ ] **Step 3: Commit**

```bash
git add src/Arius.Core.Tests/Shared/RelativePathTests.cs
git commit -m "test: define relative path rules"
```

---

### Task 4: Implement `RelativePath`

**Files:**
- Create: `src/Arius.Core/Shared/Paths/RelativePath.cs`
- Test: `src/Arius.Core.Tests/Shared/RelativePathTests.cs`

- [x] **Step 1: Write minimal implementation**

```csharp
namespace Arius.Core.Shared.Paths;

public readonly record struct RelativePath
{
    private readonly string[] _segments;

    private RelativePath(string[] segments)
    {
        _segments = segments;
    }

    public static RelativePath Root { get; } = new([]);

    public bool IsRoot => _segments.Length == 0;
    public int SegmentCount => _segments.Length;
    public PathSegment? Name => IsRoot ? null : PathSegment.Parse(_segments[^1]);
    public RelativePath? Parent => IsRoot ? null : new RelativePath(_segments[..^1]);

    public static RelativePath Parse(string value, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
        {
            if (allowEmpty)
                return Root;

            throw new ArgumentException("Path must not be empty.", nameof(value));
        }

        if (string.IsNullOrWhiteSpace(value)
            || Path.IsPathRooted(value)
            || (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '/'))
        {
            throw new ArgumentException("Path must be repository-relative.", nameof(value));
        }

        if (value.Contains('\\') || value.Contains("//") || value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
            throw new ArgumentException("Path must be canonical.", nameof(value));

        var rawSegments = value.Split('/');
        var segments = rawSegments.Select(segment => PathSegment.Parse(segment).ToString()).ToArray();
        return new RelativePath(segments);
    }

    public static bool TryParse(string? value, out RelativePath path, bool allowEmpty = false)
    {
        try
        {
            if (value is null)
            {
                path = default;
                return false;
            }

            path = Parse(value, allowEmpty);
            return true;
        }
        catch (ArgumentException)
        {
            path = default;
            return false;
        }
    }

    public bool StartsWith(RelativePath other)
    {
        if (other.SegmentCount > SegmentCount)
            return false;

        for (var i = 0; i < other.SegmentCount; i++)
        {
            if (!string.Equals(_segments[i], other._segments[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    public RelativePath Append(PathSegment segment) => new([.. _segments, segment.ToString()]);

    public static RelativePath operator /(RelativePath left, PathSegment right) => left.Append(right);

    public override string ToString() => string.Join('/', _segments);
}
```

- [x] **Step 2: Run tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RelativePathTests/*|/*/*/PathSegmentTests/*"
```

Expected:
- PASS

- [ ] **Step 3: Commit**

```bash
git add src/Arius.Core/Shared/Paths/RelativePath.cs src/Arius.Core.Tests/Shared/RelativePathTests.cs src/Arius.Core/Shared/Paths/PathSegment.cs src/Arius.Core.Tests/Shared/PathSegmentTests.cs
git commit -m "feat: add typed relative path foundation"
```

---

### Task 5: Rebase The Existing Helper On `RelativePath`

**Files:**
- Modify: `src/Arius.Core/Shared/RepositoryRelativePath.cs`
- Modify: `src/Arius.Core.Tests/Shared/RepositoryRelativePathTests.cs`

- [x] **Step 1: Add failing compatibility tests for root-returning parse bridge if needed**

If the current tests are sufficient, keep them and use them as the failing compatibility suite.

- [x] **Step 2: Change the helper to delegate to `RelativePath.Parse`**

Implementation shape:

```csharp
namespace Arius.Core.Shared;

internal static class RepositoryRelativePath
{
    public static void ValidateCanonical(string path, bool allowEmpty = false)
        => RelativePath.Parse(path, allowEmpty);
}
```

- [x] **Step 3: Run compatibility tests**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RepositoryRelativePathTests/*|/*/*/RelativePathTests/*|/*/*/PathSegmentTests/*"
```

Expected:
- PASS

- [ ] **Step 4: Commit**

```bash
git add src/Arius.Core/Shared/RepositoryRelativePath.cs src/Arius.Core.Tests/Shared/RepositoryRelativePathTests.cs src/Arius.Core/Shared/Paths/RelativePath.cs src/Arius.Core.Tests/Shared/RelativePathTests.cs src/Arius.Core/Shared/Paths/PathSegment.cs src/Arius.Core.Tests/Shared/PathSegmentTests.cs
git commit -m "refactor: rebase canonical path helper on relative path"
```

---

### Task 6: Prove Existing Filetree Integration Still Works

**Files:**
- Verify existing integration points only:
  - `src/Arius.Core/Shared/FileTree/FileTreePaths.cs`
  - `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`
  - `src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs`

- [x] **Step 1: Run focused existing suites without changing production integration**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RepositoryRelativePathTests/*|/*/*/RelativePathTests/*|/*/*/PathSegmentTests/*|/*/*/FileTreeStagingWriterTests/*|/*/*/FileTreeSerializerTests/*"
```

Expected:
- PASS

- [ ] **Step 2: Commit verification checkpoint only if supporting test adjustments were required**

```bash
git add src/Arius.Core/Shared/RepositoryRelativePath.cs src/Arius.Core.Tests/Shared/RepositoryRelativePathTests.cs src/Arius.Core/Shared/Paths/RelativePath.cs src/Arius.Core.Tests/Shared/RelativePathTests.cs src/Arius.Core/Shared/Paths/PathSegment.cs src/Arius.Core.Tests/Shared/PathSegmentTests.cs
git commit -m "test: verify typed relative path foundation against filetree integration"
```

If no new code or tests changed in this step, do not create an empty commit.

---

### Task 7: Run Final Verification For Slice 1

**Files:**
- Verify:
  - `src/Arius.Core/Shared/Paths/PathSegment.cs`
  - `src/Arius.Core/Shared/Paths/RelativePath.cs`
  - `src/Arius.Core/Shared/RepositoryRelativePath.cs`
  - related tests

- [x] **Step 1: Run the focused slice verification**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/PathSegmentTests/*|/*/*/RelativePathTests/*|/*/*/RepositoryRelativePathTests/*|/*/*/FileTreeStagingWriterTests/*|/*/*/FileTreeSerializerTests/*"
```

Expected:
- PASS

- [x] **Step 2: Run the full core test project**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj"
```

Expected:
- PASS

- [x] **Step 3: Run slopwatch**

Run:
```bash
slopwatch analyze
```

Expected:
- `0 issue(s) found`

---

### Notes For The Implementer

- Keep `RelativePath` kind-neutral in this slice.
- Do not encode trailing slash into `RelativePath` identity.
- Keep equality and hashing ordinal and case-sensitive even on Windows.
- Do not add implicit string conversions.
- Do not start migrating `FileToRestore`, `RepositoryEntry`, or `FileTreeEntry.Name` yet in this slice.
- Do not move local filesystem path joining or containment into `RelativePath`.
- `/` operator support should only compose `RelativePath` with `PathSegment`, not string.

### Self-Review

**Spec coverage**
- `PathSegment` and `RelativePath` introduced: Tasks 1-4.
- stable ordinal identity and explicit root: Task 4.
- compatibility bridge from old helper: Task 5.
- no accidental breakage in current filetree integration: Tasks 6-7.

**Placeholder scan**
- No `TODO` / `TBD` placeholders.
- Every task includes exact files and commands.
- Code steps contain concrete code, not vague instructions.

**Type consistency**
- Core types are consistently `PathSegment` and `RelativePath`.
- Core namespace is consistently `Arius.Core.Shared.Paths`.
- helper bridge consistently targets `RelativePath.Parse(path, allowEmpty)`.
