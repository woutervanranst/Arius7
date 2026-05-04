# Relative Path Domain Slice 2 Archive Boundary Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the archive boundary and archive pipeline off raw repository-path strings by introducing `RelativePath` at `LocalFileEnumerator`, `FilePair`, `HashedFilePair`, and `ArchiveCommandHandler`.

**Architecture:** This slice converts repository-internal archive paths to `RelativePath` as soon as local filesystem enumeration crosses into the Arius domain. It keeps local OS paths as strings at the boundary and continues to format event/log payloads as strings only when publishing or displaying them. Filetree models, list, and restore remain unchanged in this slice.

**Tech Stack:** C# / .NET / TUnit

---

### File Structure

**Modify**
- `src/Arius.Core/Shared/LocalFile/LocalFileEnumerator.cs`
- `src/Arius.Core/Features/ArchiveCommand/Models.cs`
- `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`
- `src/Arius.Core/Features/ArchiveCommand/Events.cs`
- `src/Arius.Core.Tests/Shared/LocalFile/LocalFileEnumeratorTests.cs`
- `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs`

**Potentially modify if compile fallout requires it**
- `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveTestEnvironment.cs`
- any archive-focused tests under `src/Arius.Core.Tests/Features/ArchiveCommand/`

**Do not modify in this slice**
- `src/Arius.Core/Features/ListQuery/`
- `src/Arius.Core/Features/RestoreCommand/`
- `src/Arius.Core/Shared/FileTree/FileTreeModels.cs`
- `src/Arius.Core/Features/ListQuery/ListQuery.cs`
- `src/Arius.Core/Features/RestoreCommand/Models.cs`

Reason: this slice is only the archive boundary. List, restore, and filetree model typing are later slices.

---

### Slice Intent

At the end of this slice:

- `LocalFileEnumerator` yields domain `FilePair` objects with `RelativePath`
- `FilePair.RelativePath` is typed as `RelativePath`
- `HashedFilePair` and archive pipeline code carry typed relative paths internally
- archive events may still expose string payloads for logging/CLI compatibility, but they should be produced from `RelativePath.ToString()` at the boundary
- local OS path joins remain explicit boundary code using `opts.RootDirectory` plus `RelativePath.ToString()`

---

### Task 1: Convert `FilePair` To `RelativePath`

**Files:**
- Modify: `src/Arius.Core/Shared/LocalFile/LocalFileEnumerator.cs`
- Test: `src/Arius.Core.Tests/Shared/LocalFile/LocalFileEnumeratorTests.cs`

- [ ] **Step 1: Write failing tests for typed `FilePair.RelativePath` usage**

Update existing tests to assert against `RelativePath` rather than raw string.

Representative changes:

```csharp
using Arius.Core.Shared.Paths;

pair.RelativePath.ShouldBe(RelativePath.Parse("photos/vacation.jpg"));
pairs.Select(p => p.RelativePath).ShouldContain(RelativePath.Parse("a.txt"));
```

Add one focused test for the root-relative normalization boundary if needed:

```csharp
[Test]
public void Enumerate_BinaryFile_YieldsTypedRelativePath()
{
    CreateFile("docs/readme.txt");

    var pair = _enumerator.Enumerate(_root).Single();

    pair.RelativePath.ShouldBe(RelativePath.Parse("docs/readme.txt"));
}
```

- [ ] **Step 2: Run tests to verify RED**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/LocalFileEnumeratorTests/*"
```

Expected:
- FAIL because `FilePair.RelativePath` is still `string`

- [ ] **Step 3: Change `FilePair.RelativePath` to `RelativePath` and update enumerator construction**

Target shape:

```csharp
using Arius.Core.Shared.Paths;

public sealed record FilePair
{
    public required RelativePath RelativePath { get; init; }
    ...
}
```

In `Enumerate(...)`, convert normalized archive-relative path strings immediately:

```csharp
var relativeName = NormalizePath(Path.GetRelativePath(rootDirectory, file.FullName));
var relativePath = RelativePath.Parse(relativeName);
```

And for pointer-only:

```csharp
var binaryFileRelativeName = relativeName[..^PointerSuffix.Length];
...
RelativePath = RelativePath.Parse(binaryFileRelativeName),
```

- [ ] **Step 4: Keep `NormalizePath` as a boundary helper for now**

Do not remove `NormalizePath` in this slice. It still belongs at the OS-path-to-domain boundary.

- [ ] **Step 5: Run tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/LocalFileEnumeratorTests/*|/*/*/PathSegmentTests/*|/*/*/RelativePathTests/*"
```

Expected:
- PASS

- [ ] **Step 6: Commit**

```bash
git add src/Arius.Core/Shared/LocalFile/LocalFileEnumerator.cs src/Arius.Core.Tests/Shared/LocalFile/LocalFileEnumeratorTests.cs
git commit -m "refactor: type archive file pair relative paths"
```

---

### Task 2: Convert Archive Pipeline Models To Typed Relative Paths

**Files:**
- Modify: `src/Arius.Core/Features/ArchiveCommand/Models.cs`
- Potentially modify: archive-focused tests that instantiate these models directly

- [ ] **Step 1: Write failing tests or compile-driven red step for `HashedFilePair` consumers**

If there are no direct model tests, use compile breakage from Task 1 as the red step and capture it by running the focused archive tests.

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ArchiveRecoveryTests/*|/*/*/LocalFileEnumeratorTests/*"
```

Expected:
- FAIL or compile break because archive code still assumes `string` relative paths

- [ ] **Step 2: Keep `HashedFilePair` carrying the typed `FilePair` only**

`HashedFilePair` should continue to rely on `FilePair.RelativePath` rather than introducing a second redundant path field.

Minimal expected shape remains:

```csharp
public sealed record HashedFilePair(
    FilePair    FilePair,
    ContentHash ContentHash,
    string      LocalRootPath);
```

No new path string should be added here.

- [ ] **Step 3: Run focused archive tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ArchiveRecoveryTests/*|/*/*/LocalFileEnumeratorTests/*"
```

Expected:
- PASS if only compile fallout needed fixing

- [ ] **Step 4: Commit**

```bash
git add src/Arius.Core/Features/ArchiveCommand/Models.cs src/Arius.Core/Shared/LocalFile/LocalFileEnumerator.cs src/Arius.Core.Tests/Shared/LocalFile/LocalFileEnumeratorTests.cs
git commit -m "refactor: carry typed relative paths through archive models"
```

---

### Task 3: Update `ArchiveCommandHandler` To Use `RelativePath` Internally

**Files:**
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`
- Modify: `src/Arius.Core/Features/ArchiveCommand/Events.cs`
- Test: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs`

- [ ] **Step 1: Write failing archive-focused tests for event/log boundary behavior if needed**

If current archive recovery tests already cover the changed code paths, use them as the red step. If not, add one narrow archive test asserting that a typed archive path still shows up in logs/events as the canonical string form.

Suggested narrow test shape if needed:

```csharp
[Test]
public async Task Archive_TypedRelativePath_StillEmitsCanonicalPathTextInLogs()
{
    using var env = new ArchiveTestEnvironment();
    env.WriteRandomFile("docs/readme.txt", 128);

    var result = await env.ArchiveAsync(BlobTier.Cool);

    result.Success.ShouldBeTrue(result.ErrorMessage);
    env.ArchiveLogs.GetSnapshot(clear: false)
        .Select(static record => record.Message)
        .ShouldContain(message => message.Contains("docs/readme.txt", StringComparison.Ordinal));
}
```

- [ ] **Step 2: Change archive internal path usage to stay typed**

Expected transformations:

```csharp
var relativePathText = pair.RelativePath.ToString();

var fullPath = pair.BinaryExists
    ? Path.Combine(opts.RootDirectory, relativePathText.Replace('/', Path.DirectorySeparatorChar))
    : null;

await _mediator.Publish(new FileScannedEvent(relativePathText, fileSize), cancellationToken);
```

Rules:
- keep `RelativePath` typed in pipeline state
- convert to string only when:
  - publishing current string-based events
  - logging
  - joining with local OS path roots
  - invoking existing progress callbacks that still accept string

- [ ] **Step 3: Leave `ArchiveCommandOptions.CreateHashProgress` string-based in this slice**

Do not type CLI/event/progress surfaces yet. Convert with `relativePath.ToString()` at the boundary.

- [ ] **Step 4: Run focused archive tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ArchiveRecoveryTests/*|/*/*/LocalFileEnumeratorTests/*|/*/*/PathSegmentTests/*|/*/*/RelativePathTests/*"
```

Expected:
- PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs src/Arius.Core/Features/ArchiveCommand/Events.cs src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs src/Arius.Core/Shared/LocalFile/LocalFileEnumerator.cs src/Arius.Core.Tests/Shared/LocalFile/LocalFileEnumeratorTests.cs
git commit -m "refactor: use typed relative paths in archive pipeline"
```

---

### Task 4: Verify Archive Slice Against Existing Filetree Integration

**Files:**
- Verify only:
  - `src/Arius.Core/Shared/LocalFile/LocalFileEnumerator.cs`
  - `src/Arius.Core/Features/ArchiveCommand/Models.cs`
  - `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`
  - `src/Arius.Core/Features/ArchiveCommand/Events.cs`
  - related tests

- [ ] **Step 1: Run focused archive-plus-filetree regression tests**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ArchiveRecoveryTests/*|/*/*/LocalFileEnumeratorTests/*|/*/*/FileTreeStagingWriterTests/*|/*/*/FileTreeSerializerTests/*|/*/*/RelativePathTests/*|/*/*/PathSegmentTests/*"
```

Expected:
- PASS

- [ ] **Step 2: Run full core tests**

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

- `RelativePath` is the archive-domain type in this slice.
- Local OS paths remain strings and are joined explicitly with `Path.Combine`.
- Event payloads, CLI progress callbacks, and logs may remain string-based for now.
- Do not add typed list/restore/public query models in this slice.
- Do not type `ArchiveCommandOptions.RootDirectory`; it is a local filesystem boundary, not a repository path.
- Do not introduce new path helper methods if a boundary conversion plus `RelativePath` operation is sufficient.

### Self-Review

**Spec coverage**
- archive boundary converts to `RelativePath` early: Tasks 1-3.
- archive pipeline stays typed internally: Task 3.
- filetree/list/restore remain out of scope: enforced by file list and notes.
- boundary string conversion remains explicit for logs/events/progress: Task 3.

**Placeholder scan**
- No `TODO` / `TBD` placeholders.
- Each task has concrete files, commands, and code shapes.
- The plan avoids vague instructions like "handle fallout" without naming verification commands.

**Type consistency**
- `RelativePath` remains the archive-domain type.
- local OS path boundaries remain `string`.
- progress and event payload boundaries remain string-based only in this slice.
