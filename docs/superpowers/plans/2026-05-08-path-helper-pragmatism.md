# Path Helper Pragmatism Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep path typing inside `Arius.Core` while making tests and Explorer callers more ergonomic and less string-surgery-heavy.

**Architecture:** Preserve the typed-only `RepositoryPaths` and typed `IBlobContainerService` APIs in core. Move string convenience helpers that are now test-only into `Arius.Tests.Shared`, and simplify Explorer view models to consume `RelativePath.Name` instead of converting typed paths back to strings and re-parsing them mentally.

**Tech Stack:** .NET, TUnit, WPF/MVVM, CommunityToolkit.Mvvm

---

### Task 1: Rename The Repository Test String Adapter

**Files:**
- Create: none
- Modify: `src/Arius.Tests.Shared/RepositoryCachePaths.cs`
- Modify: all test call sites that reference `RepositoryCachePaths`
- Test: `src/Arius.Core.Tests/Shared/RepositoryPathsTests.cs`

- [ ] **Step 1: Write the failing test expectation**

Update `src/Arius.Core.Tests/Shared/RepositoryPathsTests.cs` so the assertions use the new adapter name instead of `RepositoryCachePaths`.

```csharp
RepositoryPathStrings.GetRepositoryDirectory("account", "container").ShouldBe(root);
RepositoryPathStrings.GetChunkIndexCacheDirectory("account", "container").ShouldBe(Path.Combine(root, "chunk-index"));
RepositoryPathStrings.GetFileTreeCacheDirectory("account", "container").ShouldBe(Path.Combine(root,   "filetrees"));
RepositoryPathStrings.GetSnapshotCacheDirectory("account", "container").ShouldBe(Path.Combine(root,   "snapshots"));
```

- [ ] **Step 2: Run the focused test to verify it fails**

Run: `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/RepositoryPathsTests/*"`

Expected: FAIL with compile errors because `RepositoryPathStrings` does not exist yet.

- [ ] **Step 3: Rename the adapter to match its actual role**

Edit `src/Arius.Tests.Shared/RepositoryCachePaths.cs` so it becomes a clearly test-only string adapter.

```csharp
using Arius.Core.Shared;

namespace Arius.Tests.Shared;

public static class RepositoryPathStrings
{
    public static string GetRepositoryDirectory(string accountName, string containerName) =>
        RepositoryPaths.GetRepositoryRoot(accountName, containerName).ToString();

    public static string GetChunkIndexCacheDirectory(string accountName, string containerName) =>
        RepositoryPaths.GetChunkIndexCacheRoot(accountName, containerName).ToString();

    public static string GetFileTreeCacheDirectory(string accountName, string containerName) =>
        RepositoryPaths.GetFileTreeCacheRoot(accountName, containerName).ToString();

    public static string GetSnapshotCacheDirectory(string accountName, string containerName) =>
        RepositoryPaths.GetSnapshotCacheRoot(accountName, containerName).ToString();
}
```

- [ ] **Step 4: Update all test call sites to the new adapter name**

Replace `RepositoryCachePaths` with `RepositoryPathStrings` everywhere under `src/`.

Representative examples to update:

```csharp
var cacheDir = RepositoryPathStrings.GetFileTreeCacheDirectory(Account, containerName);
var l2Path = Path.Combine(RepositoryPathStrings.GetChunkIndexCacheDirectory(Account, containerName), prefix.ToString());
var cacheDir = RepositoryPathStrings.GetRepositoryDirectory(accountName, containerName);
```

- [ ] **Step 5: Run the focused tests to verify they pass**

Run: `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/RepositoryPathsTests/*"`

Expected: PASS.

- [ ] **Step 6: Run broader tests that cover shared test infrastructure**

Run: `dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/*Repository*/*"`

Expected: PASS for the affected test tree, or zero relevant failures from the rename.

- [ ] **Step 7: Commit**

```bash
git add src/Arius.Tests.Shared/RepositoryCachePaths.cs src/Arius.Core.Tests/Shared/RepositoryPathsTests.cs src/Arius.Integration.Tests src/Arius.Core.Tests src/Arius.E2E.Tests src/Arius.Tests.Shared
git commit -m "refactor(tests): name repository path string adapter explicitly"
```

### Task 2: Move Blob String Convenience Helpers Out Of Core

**Files:**
- Create: `src/Arius.Tests.Shared/BlobPathStrings.cs`
- Modify: `src/Arius.Core/Shared/Storage/BlobConstants.cs`
- Modify: test projects that currently use `BlobPaths.Chunk(...)`, `BlobPaths.FileTree(...)`, `BlobPaths.Snapshot(...)`, `BlobPaths.ThinChunk(...)`, `BlobPaths.ChunkRehydrated(...)`, or `BlobPaths.ChunkIndexShard(...)`
- Test: `src/Arius.Core.Tests/Shared/Storage/BlobPathsTests.cs`

- [ ] **Step 1: Write the failing test expectation**

Update `src/Arius.Core.Tests/Shared/Storage/BlobPathsTests.cs` so it only validates the typed `...Path(...)` helpers from core and stops asserting the string helpers on `BlobPaths`.

```csharp
BlobPaths.ChunkPath(chunkHash).ShouldBe(RelativePath.Parse($"chunks/{chunkHash}"));
BlobPaths.FileTreePath(fileTreeHash).ShouldBe(RelativePath.Parse($"filetrees/{fileTreeHash}"));
```

Add a new test file in shared test infrastructure for the string adapters:

```csharp
BlobPathStrings.Chunk(chunkHash).ShouldBe($"chunks/{chunkHash}");
BlobPathStrings.FileTree(fileTreeHash).ShouldBe($"filetrees/{fileTreeHash}");
```

- [ ] **Step 2: Run the focused tests to verify they fail**

Run: `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/BlobPathsTests/*"`

Expected: FAIL because `BlobPathStrings` does not exist yet.

- [ ] **Step 3: Create the test-side blob string adapter**

Create `src/Arius.Tests.Shared/BlobPathStrings.cs` with string helpers that delegate to the typed core helpers.

```csharp
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;

namespace Arius.Tests.Shared;

public static class BlobPathStrings
{
    public static string Chunk(ChunkHash hash) => BlobPaths.ChunkPath(hash).ToString();
    public static string ThinChunk(ContentHash hash) => BlobPaths.ThinChunkPath(hash).ToString();
    public static string ChunkRehydrated(ChunkHash hash) => BlobPaths.ChunkRehydratedPath(hash).ToString();
    public static string FileTree(FileTreeHash hash) => BlobPaths.FileTreePath(hash).ToString();
    public static string Snapshot(string name) => BlobPaths.SnapshotPath(name).ToString();
    public static string ChunkIndexShard(string prefix) => BlobPaths.ChunkIndexShardPath(PathSegment.Parse(prefix)).ToString();
}
```

- [ ] **Step 4: Remove the public string helpers from core**

Edit `src/Arius.Core/Shared/Storage/BlobConstants.cs` so `BlobPaths` keeps only the typed helpers and typed prefixes.

Remove these members:

```csharp
public static string Chunk(ChunkHash hash) => ChunkPath(hash).ToString();
public static string ThinChunk(ContentHash hash) => ThinChunkPath(hash).ToString();
public static string ChunkRehydrated(ChunkHash hash) => ChunkRehydratedPath(hash).ToString();
public static string FileTree(FileTreeHash hash) => FileTreePath(hash).ToString();
public static string Snapshot(string name) => SnapshotPath(name).ToString();
public static string ChunkIndexShard(string prefix) => ChunkIndexShardPath(PathSegment.Parse(prefix)).ToString();
```

- [ ] **Step 5: Update tests to use either typed helpers directly or `BlobPathStrings`**

Use this rule consistently:
- if the API under test takes `RelativePath`, switch to `BlobPaths.*Path(...)`
- if the fake/test storage expects string keys, switch to `BlobPathStrings.*(...)`

Representative replacements:

```csharp
await svc.UploadAsync(BlobPaths.ChunkPath(chunkHash), new MemoryStream(content), meta, BlobTier.Hot);
blobs.SeedBlob(BlobPathStrings.FileTree(payload.Hash), [], contentType: null);
blobs.Metadata[BlobPathStrings.Chunk(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Hot };
```

- [ ] **Step 6: Run focused tests for the storage path split**

Run:
- `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/BlobPathsTests/*"`
- `dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/BlobStorageServiceTests/*"`

Expected: PASS.

- [ ] **Step 7: Run a wider affected set**

Run:
- `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/*ChunkStorage*/*"`
- `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/*FileTree*/*"`

Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Arius.Core/Shared/Storage/BlobConstants.cs src/Arius.Tests.Shared/BlobPathStrings.cs src/Arius.Core.Tests src/Arius.Integration.Tests src/Arius.E2E.Tests src/Arius.Tests.Shared
git commit -m "refactor(tests): move blob string helpers out of core"
```

### Task 3: Remove Explorer String Surgery On RelativePath

**Files:**
- Create: none
- Modify: `src/Arius.Explorer/RepositoryExplorer/FileItemViewModel.cs`
- Modify: `src/Arius.Explorer/RepositoryExplorer/RepositoryExplorerViewModel.cs`
- Modify: `src/Arius.Explorer.Tests/RepositoryExplorer/FileItemViewModelTests.cs`
- Modify: `src/Arius.Explorer.Tests/RepositoryExplorer/RepositoryExplorerViewModelTests.cs`

- [ ] **Step 1: Write the failing tests**

Add a test to `src/Arius.Explorer.Tests/RepositoryExplorer/FileItemViewModelTests.cs` that verifies a nested repository path uses the final `RelativePath` segment as the display name.

```csharp
[Test]
public void Constructor_UsesRelativePathNameForDisplayName()
{
    var file = new RepositoryFileEntry(
        RelativePath: RelativePath.Parse("folder1/folder2/file.txt"),
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

    viewModel.Name.ShouldBe("file.txt");
}
```

Extend `src/Arius.Explorer.Tests/RepositoryExplorer/RepositoryExplorerViewModelTests.cs` so the existing directory case still asserts `folder2` when the repository returns `folder1/folder2`.

- [ ] **Step 2: Run the focused Explorer tests to verify they fail or protect the current behavior**

Run:
- `dotnet test --project src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj --treenode-filter "/*/*/FileItemViewModelTests/*"`
- `dotnet test --project src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj --treenode-filter "/*/*/RepositoryExplorerViewModelTests/*"`

Expected: either FAIL before the implementation change, or PASS and provide protection for the refactor.

- [ ] **Step 3: Replace the string-based extraction with `RelativePath.Name`**

In `src/Arius.Explorer/RepositoryExplorer/FileItemViewModel.cs` change:

```csharp
Name = Path.GetFileName(file.RelativePath.ToString());
```

to:

```csharp
Name = file.RelativePath.Name.ToString();
```

In `src/Arius.Explorer/RepositoryExplorer/RepositoryExplorerViewModel.cs` change `ExtractDirectoryName` to:

```csharp
private static string ExtractDirectoryName(RelativePath relativeName) =>
    relativeName.Name.ToString();
```

Also update the stale comment so it no longer refers to slash-terminated paths.

- [ ] **Step 4: Run the focused Explorer tests to verify they pass**

Run:
- `dotnet test --project src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj --treenode-filter "/*/*/FileItemViewModelTests/*"`
- `dotnet test --project src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj --treenode-filter "/*/*/RepositoryExplorerViewModelTests/*"`

Expected: PASS.

- [ ] **Step 5: Run the full Explorer test project**

Run: `dotnet test --project src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj`

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Arius.Explorer/RepositoryExplorer/FileItemViewModel.cs src/Arius.Explorer/RepositoryExplorer/RepositoryExplorerViewModel.cs src/Arius.Explorer.Tests/RepositoryExplorer/FileItemViewModelTests.cs src/Arius.Explorer.Tests/RepositoryExplorer/RepositoryExplorerViewModelTests.cs
git commit -m "refactor(explorer): use relative path names directly"
```

### Task 4: Final Verification

**Files:**
- Create: none
- Modify: none
- Test: affected projects only

- [ ] **Step 1: Run the core affected suites**

Run:
- `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/RepositoryPathsTests/*"`
- `dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/BlobPathsTests/*"`

Expected: PASS.

- [ ] **Step 2: Run integration coverage for path adapter changes**

Run:
- `dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/BlobStorageServiceTests/*"`
- `dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/ChunkIndexServiceIntegrationTests/*"`

Expected: PASS.

- [ ] **Step 3: Run Explorer tests**

Run: `dotnet test --project src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj`

Expected: PASS.

- [ ] **Step 4: Review the diff for architectural consistency**

Check these outcomes in the diff:
- `Arius.Core` keeps typed repository and blob path APIs only
- string path adapters live in `Arius.Tests.Shared`
- Explorer no longer uses `Path.GetFileName(...)` or slash trimming for `RelativePath`

- [ ] **Step 5: Commit the final verification-only changes if needed**

```bash
git status --short
```

Expected: no unexpected modified files beyond the intended implementation.

## Self-Review

Spec coverage for this plan:
- Repository path string convenience stays out of `Arius.Core`: covered by Task 1.
- Blob string convenience moves out of `Arius.Core` while keeping typed `IBlobContainerService`: covered by Task 2.
- Explorer removes `Path.*` string surgery on `RelativePath`: covered by Task 3.

Placeholder scan:
- No `TODO`, `TBD`, or "similar to" placeholders remain.
- All code-changing steps name exact files and show the target code shape.

Type consistency:
- `RepositoryPathStrings` is used consistently as the test-side repository string adapter.
- `BlobPathStrings` is used consistently as the test-side blob string adapter.
- `BlobPaths.*Path(...)` remains the typed core API.

Plan complete and saved to `docs/superpowers/plans/2026-05-08-path-helper-pragmatism.md`. Two execution options:

**1. Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?
