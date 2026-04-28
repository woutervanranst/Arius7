# Typed List Query Hashes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace string hash fields in internal list and hydration query result models with typed hash value objects, and update the direct consumers and tests that depend on those models.

**Architecture:** Keep hash identities strongly typed inside `Arius.Core` query/result models and throughout in-repo consumers such as Explorer tests. Only stringify at true presentation boundaries. Make the smallest change that removes reparsing from `ChunkHydrationStatusQueryHandler` and keeps current null semantics for local-only or unknown entries.

**Tech Stack:** C# 13 / .NET 10, Mediator, TUnit, existing Arius hash value objects (`ContentHash`, `FileTreeHash`, `ChunkHash`)

---

## File Map

- Modify: `src/Arius.Core/Features/ListQuery/ListQuery.cs`
  Responsibility: result record definitions for repository listing entries.
- Modify: `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`
  Responsibility: construct typed list result records directly from typed file-tree and local state.
- Modify: `src/Arius.Core/Features/ChunkHydrationStatusQuery/ChunkHydrationStatusQuery.cs`
  Responsibility: typed hydration status result record and handler logic that should stop reparsing string hashes.
- Modify: `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`
  Responsibility: verify list query emits typed hash results and preserves null semantics.
- Modify: `src/Arius.Core.Tests/Features/ChunkHydrationStatusQuery/ResolveFileHydrationStatusesHandlerTests.cs`
  Responsibility: verify hydration status resolution with typed `RepositoryFileEntry` inputs and typed result hashes.
- Modify: `src/Arius.Explorer.Tests/RepositoryExplorer/RepositoryExplorerViewModelTests.cs`
  Responsibility: update test fixtures that construct Core list/hydration result models.
- Modify: `src/Arius.Explorer.Tests/RepositoryExplorer/FileItemViewModelTests.cs`
  Responsibility: update test fixtures that construct `RepositoryFileEntry` directly.

### Task 1: Type The List And Hydration Result Records

**Files:**
- Modify: `src/Arius.Core/Features/ListQuery/ListQuery.cs`
- Modify: `src/Arius.Core/Features/ChunkHydrationStatusQuery/ChunkHydrationStatusQuery.cs`
- Test: `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`

- [ ] **Step 1: Write the failing list-query test expectation update**

Change the existing assertions in `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs` from string expectations to typed expectations:

```csharp
var directory = results.OfType<RepositoryDirectoryEntry>().Single();
directory.TreeHash.ShouldBe(TreeHashFor("docs"));

var file = results.OfType<RepositoryFileEntry>().Single();
file.ContentHash.ShouldBe(ContentHashFor("readme"));
```

- [ ] **Step 2: Run the focused list-query tests to verify they fail at compile time**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/*"`

Expected: build/test failure because `RepositoryFileEntry.ContentHash` and `RepositoryDirectoryEntry.TreeHash` are still `string?`.

- [ ] **Step 3: Change the result record property types**

Update `src/Arius.Core/Features/ListQuery/ListQuery.cs` and `src/Arius.Core/Features/ChunkHydrationStatusQuery/ChunkHydrationStatusQuery.cs` to:

```csharp
using Arius.Core.Shared.Hashes;
using Mediator;

namespace Arius.Core.Features.ListQuery;

public sealed record ListQuery(ListQueryOptions Options) : IStreamQuery<RepositoryEntry>;

public abstract record RepositoryEntry(string RelativePath);

public sealed record RepositoryFileEntry(
    string RelativePath,
    ContentHash? ContentHash,
    long? OriginalSize,
    DateTimeOffset? Created,
    DateTimeOffset? Modified,
    bool ExistsInCloud,
    bool ExistsLocally,
    bool? HasPointerFile,
    bool? BinaryExists,
    bool? Hydrated = null)
    : RepositoryEntry(RelativePath);

public sealed record RepositoryDirectoryEntry(
    string RelativePath,
    FileTreeHash? TreeHash,
    bool ExistsInCloud,
    bool ExistsLocally)
    : RepositoryEntry(RelativePath);
```

And:

```csharp
public sealed record ChunkHydrationStatusResult(
    string RelativePath,
    ContentHash? ContentHash,
    ChunkHydrationStatus Status);
```

- [ ] **Step 4: Run the focused list-query tests again to verify the next set of constructor call sites fail**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/*"`

Expected: build failure in handlers/tests still constructing the records with strings.

- [ ] **Step 5: Commit the record type changes**

```bash
git add src/Arius.Core/Features/ListQuery/ListQuery.cs src/Arius.Core/Features/ChunkHydrationStatusQuery/ChunkHydrationStatusQuery.cs src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs
git commit -m "refactor: type list query hash results"
```

### Task 2: Update Handlers To Stop Stringifying And Reparsing

**Files:**
- Modify: `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`
- Modify: `src/Arius.Core/Features/ChunkHydrationStatusQuery/ChunkHydrationStatusQuery.cs`
- Test: `src/Arius.Core.Tests/Features\/ChunkHydrationStatusQuery/ResolveFileHydrationStatusesHandlerTests.cs`

- [ ] **Step 1: Write the failing hydration-status test fixture update**

In `src/Arius.Core.Tests/Features/ChunkHydrationStatusQuery/ResolveFileHydrationStatusesHandlerTests.cs`, change repository file fixture construction to typed hashes:

```csharp
var files = new[]
{
    new RepositoryFileEntry($"{chunkType}.bin", contentHash, 100, null, null, true, false, null, null)
};
```

And for the multi-file case:

```csharp
var files = new[]
{
    new RepositoryFileEntry("thin.bin", thinContentHash, 50, null, null, true, false, null, null),
    new RepositoryFileEntry("tar.bin", tarContentHash, 75, null, null, true, false, null, null)
};
```

- [ ] **Step 2: Run the focused hydration-status tests to verify the handler still assumes strings**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ResolveFileHydrationStatusesHandlerTests/*"`

Expected: build/test failure because the handler still calls `string.IsNullOrWhiteSpace` and `ContentHash.TryParse` on `RepositoryFileEntry.ContentHash`.

- [ ] **Step 3: Write the minimal handler updates**

In `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`, remove string conversion when constructing results:

```csharp
yield return new RepositoryDirectoryEntry(relativePath, entry.FileTreeHash, ExistsInCloud: true, ExistsLocally: existsLocally);
```

```csharp
yield return new RepositoryFileEntry(
    RelativePath: relativePath,
    ContentHash: candidate.Entry.ContentHash,
    OriginalSize: originalSize,
    Created: candidate.Entry.Created,
    Modified: candidate.Entry.Modified,
    ExistsInCloud: true,
    ExistsLocally: localFile is not null,
    HasPointerFile: localFile?.PointerExists,
    BinaryExists: localFile?.BinaryExists,
    Hydrated: null);
```

In `src/Arius.Core/Features/ChunkHydrationStatusQuery/ChunkHydrationStatusQuery.cs`, replace the reparsing logic with typed filtering:

```csharp
var cloudFiles = query.Files
    .Where(file => file.ExistsInCloud && file.ContentHash is not null)
    .Select(file => (File: file, ContentHash: file.ContentHash!.Value))
    .ToList();

if (cloudFiles.Count == 0)
{
    yield break;
}

var indexEntries = await _chunkIndex.LookupAsync(
    cloudFiles.Select(file => file.ContentHash).Distinct(),
    cancellationToken).ConfigureAwait(false);

var statusByChunkHash = new Dictionary<ChunkHash, ChunkHydrationStatus>();

foreach (var (file, contentHash) in cloudFiles)
{
    cancellationToken.ThrowIfCancellationRequested();

    if (!indexEntries.TryGetValue(contentHash, out var entry))
    {
        _logger.LogWarning("Content hash not found in chunk index while resolving hydration status: {ContentHash}", file.ContentHash);
        yield return new ChunkHydrationStatusResult(file.RelativePath, file.ContentHash, ChunkHydrationStatus.Unknown);
        continue;
    }

    if (!statusByChunkHash.TryGetValue(entry.ChunkHash, out var status))
    {
        status = await _chunkStorage.GetHydrationStatusAsync(entry.ChunkHash, cancellationToken).ConfigureAwait(false);
        statusByChunkHash[entry.ChunkHash] = status;
    }

    yield return new ChunkHydrationStatusResult(file.RelativePath, file.ContentHash, status);
}
```

- [ ] **Step 4: Run the focused Core tests to verify the handlers now pass**

Run these commands:

`dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/*"`

`dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ResolveFileHydrationStatusesHandlerTests/*"`

Expected: PASS.

- [ ] **Step 5: Commit the handler updates**

```bash
git add src/Arius.Core/Features/ListQuery/ListQueryHandler.cs src/Arius.Core/Features/ChunkHydrationStatusQuery/ChunkHydrationStatusQuery.cs src/Arius.Core.Tests/Features/ChunkHydrationStatusQuery/ResolveFileHydrationStatusesHandlerTests.cs
git commit -m "refactor: keep list query hashes typed"
```

### Task 3: Update In-Repo Consumers And Cross-Assembly Tests

**Files:**
- Modify: `src/Arius.Explorer.Tests/RepositoryExplorer/RepositoryExplorerViewModelTests.cs`
- Modify: `src/Arius.Explorer.Tests/RepositoryExplorer/FileItemViewModelTests.cs`
- Modify: `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`

- [ ] **Step 1: Write the failing Explorer fixture updates**

Update Explorer test fixtures to use valid typed hashes instead of ad hoc strings. Use real hash parsing rather than placeholder values:

```csharp
new RepositoryFileEntry(
    "/folder1/file-a.txt",
    ContentHash.Parse(new string('a', 64)),
    1024,
    null,
    null,
    true,
    true,
    true,
    true,
    null)
```

```csharp
new RepositoryDirectoryEntry(
    "/folder1/folder2/",
    FileTreeHash.Parse(new string('b', 64)),
    true,
    true)
```

```csharp
new ChunkHydrationStatusResult(
    "/folder1/file-a.txt",
    ContentHash.Parse(new string('c', 64)),
    ChunkHydrationStatus.Available)
```

- [ ] **Step 2: Run the focused Explorer tests to verify any remaining string assumptions fail**

Run: `dotnet test --project "src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj"`

Expected: compile/test failures in test fixtures or assertions that still treat these Core model hashes as strings.

- [ ] **Step 3: Write the minimal consumer/test updates**

Update all direct fixture construction sites found by `new RepositoryFileEntry(...)`, `new RepositoryDirectoryEntry(...)`, and `new ChunkHydrationStatusResult(...)` to use typed hashes or `null`.

Where tests specifically need display text, stringify explicitly in the assertion rather than the fixture:

```csharp
fileItem.ContentHash.ShouldBe(ContentHash.Parse(new string('a', 64)).ToString());
```

But where the test is about the Core model itself, assert typed equality:

```csharp
entry.ContentHash.ShouldBe(ContentHash.Parse(new string('a', 64)));
entry.TreeHash.ShouldBe(FileTreeHash.Parse(new string('b', 64)));
```

- [ ] **Step 4: Run the full focused verification set**

Run these commands:

`dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/*|/*/*/ResolveFileHydrationStatusesHandlerTests/*"`

`dotnet test --project "src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj"`

`slopwatch analyze`

Expected:
- Core focused tests PASS
- Explorer tests PASS
- `slopwatch analyze` reports `0 issue(s) found`

- [ ] **Step 5: Commit the consumer/test updates**

```bash
git add src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs src/Arius.Explorer.Tests/RepositoryExplorer/RepositoryExplorerViewModelTests.cs src/Arius.Explorer.Tests/RepositoryExplorer/FileItemViewModelTests.cs
git add src/Arius.Core.Tests/Features/ChunkHydrationStatusQuery/ResolveFileHydrationStatusesHandlerTests.cs
git commit -m "test: update typed list query hashes"
```

## Self-Review

Spec coverage:
- typed `RepositoryFileEntry.ContentHash`: covered in Tasks 1-3
- typed `RepositoryDirectoryEntry.TreeHash`: covered in Tasks 1 and 3
- typed `ChunkHydrationStatusResult.ContentHash`: covered in Tasks 1-3
- remove reparsing in hydration handler: covered in Task 2
- focused verification and explicit output-boundary behavior: covered in Task 3

Placeholder scan:
- no `TODO`, `TBD`, or implied “figure it out later” steps remain
- each code-changing step names exact files and example code to apply

Type consistency:
- `RepositoryFileEntry.ContentHash` is consistently `ContentHash?`
- `RepositoryDirectoryEntry.TreeHash` is consistently `FileTreeHash?`
- `ChunkHydrationStatusResult.ContentHash` is consistently `ContentHash?`
