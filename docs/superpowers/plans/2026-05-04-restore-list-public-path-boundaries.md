# Restore/List Public Path Boundaries Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move restore/list public repository-relative selector options to `RelativePath?`, remove feature-local normalization helpers, and keep local filesystem roots string-based.

**Architecture:** Re-type `RestoreOptions.TargetPath` and `ListQueryOptions.Prefix` at the public boundary, then update handlers to consume typed values directly. Keep CLI and Explorer as string-input boundaries that parse user-facing text before constructing commands and queries.

**Tech Stack:** C# 13, .NET 10, Mediator, TUnit, WPF MVVM, System.CommandLine

---

### Task 1: Retype Restore Public Selector Boundary

**Files:**
- Modify: `src/Arius.Core/Features/RestoreCommand/RestoreCommand.cs`
- Modify: `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
[Test]
public async Task Handle_TargetPathCaseMismatch_DoesNotRestoreFiles()
{
    var restoreResult = await fixture.CreateRestoreHandler().Handle(
        new RestoreCommand(new RestoreOptions
        {
            RootDirectory = fixture.RestoreRoot,
            Overwrite = true,
            TargetPath = PathOf("docs"),
        }),
        CancellationToken.None);
}

[Test]
public async Task Handle_TargetPathWithTypedRootFile_RestoresSelectedRootFile()
{
    var restoreResult = await fixture.CreateRestoreHandler().Handle(
        new RestoreCommand(new RestoreOptions
        {
            RootDirectory = fixture.RestoreRoot,
            Overwrite = true,
            TargetPath = PathOf("file-a.txt"),
        }),
        CancellationToken.None);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/Handle_TargetPathCaseMismatch_DoesNotRestoreFiles|/*/*/RestoreCommandHandlerTests/Handle_TargetPathWithTypedRootFile_RestoresSelectedRootFile"`
Expected: FAIL at compile time because `RestoreOptions.TargetPath` still expects `string?`.

- [ ] **Step 3: Write minimal implementation**

```csharp
using Arius.Core.Shared.Paths;

public sealed record RestoreOptions
{
    public required string RootDirectory { get; init; }
    public RelativePath? TargetPath { get; init; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/Handle_TargetPathCaseMismatch_DoesNotRestoreFiles|/*/*/RestoreCommandHandlerTests/Handle_TargetPathWithTypedRootFile_RestoresSelectedRootFile"`
Expected: PASS.

### Task 2: Retype List Public Selector Boundary

**Files:**
- Modify: `src/Arius.Core/Features/ListQuery/ListQuery.cs`
- Modify: `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Test]
public async Task Handle_PrefixAndNonRecursive_StreamsOnlyImmediateChildrenOfPrefix()
{
    await foreach (var entry in handler.Handle(
        new ListQuery(new ListQueryOptions { Prefix = PathOf("docs"), Recursive = false }),
        CancellationToken.None))
    {
        results.Add(entry);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/Handle_PrefixAndNonRecursive_StreamsOnlyImmediateChildrenOfPrefix"`
Expected: FAIL at compile time because `ListQueryOptions.Prefix` still expects `string?`.

- [ ] **Step 3: Write minimal implementation**

```csharp
using Arius.Core.Shared.Paths;

public sealed record ListQueryOptions
{
    public RelativePath? Prefix { get; init; }
    public string? LocalPath { get; init; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/Handle_PrefixAndNonRecursive_StreamsOnlyImmediateChildrenOfPrefix"`
Expected: PASS.

### Task 3: Remove Feature-Local Selector Normalization

**Files:**
- Modify: `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs`
- Modify: `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`
- Test: `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`
- Test: `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
[Test]
public async Task Handle_PrefixAndNonRecursive_UsesTypedPrefixWithoutStringNormalization()
{
    var results = await handler.Handle(
        new ListQuery(new ListQueryOptions { Prefix = PathOf("docs"), Recursive = false }),
        CancellationToken.None).ToListAsync();

    results.ShouldContain(entry => entry.RelativePath == PathOf("docs/guide.txt"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/Handle_PrefixAndNonRecursive_StreamsOnlyImmediateChildrenOfPrefix|/*/*/RestoreCommandHandlerTests/Handle_TargetPathWithTypedRootFile_RestoresSelectedRootFile"`
Expected: FAIL if handlers still rely on string normalization helpers or old signatures.

- [ ] **Step 3: Write minimal implementation**

```csharp
private async Task<List<FileToRestore>> CollectFilesAsync(FileTreeHash rootHash, RelativePath? targetPath, CancellationToken cancellationToken)
{
    await WalkTreeAsync(rootHash, RelativePath.Root, targetPath, result, cancellationToken, onFileDiscovered);
}

var prefix = opts.Prefix;

foreach (var segment in prefix.Value.Segments)
{
    // traverse typed segments directly
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/Handle_PrefixAndNonRecursive_StreamsOnlyImmediateChildrenOfPrefix|/*/*/RestoreCommandHandlerTests/Handle_TargetPathWithTypedRootFile_RestoresSelectedRootFile"`
Expected: PASS.

### Task 4: Retype String-Input Boundaries

**Files:**
- Modify: `src/Arius.Cli/Commands/Ls/LsVerb.cs`
- Modify: `src/Arius.Explorer/RepositoryExplorer/RepositoryExplorerViewModel.cs`
- Modify: `src/Arius.Explorer.Tests/RepositoryExplorer/RepositoryExplorerViewModelTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
command.Options.TargetPath.ShouldBe(PathOf("file-a.txt"));
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj" --treenode-filter "/*/*/RepositoryExplorerViewModelTests/RestoreCommand_WhenConfirmed_SendsRestoreCommandAndRefreshesSelection"`
Expected: FAIL because Explorer still builds string `TargetPath`.

- [ ] **Step 3: Write minimal implementation**

```csharp
TargetPath = selectedFile.File.RelativePath,

Prefix = string.IsNullOrWhiteSpace(node.Prefix)
    ? null
    : RelativePath.Parse(node.Prefix),

Prefix = string.IsNullOrWhiteSpace(prefix)
    ? null
    : RelativePath.Parse(prefix.Trim('/').Replace('\\', '/')),
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --project "src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj" --treenode-filter "/*/*/RepositoryExplorerViewModelTests/RestoreCommand_WhenConfirmed_SendsRestoreCommandAndRefreshesSelection"`
Expected: PASS.

### Task 5: Verify Focused Suites And Cleanup Commit

**Files:**
- Modify: any touched tests/callers from Tasks 1-4

- [ ] **Step 1: Run focused core verification**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/*|/*/*/ListQueryHandlerTests/*"`
Expected: PASS.

- [ ] **Step 2: Run focused explorer verification**

Run: `dotnet test --project "src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj" --treenode-filter "/*/*/RepositoryExplorerViewModelTests/*"`
Expected: PASS.

- [ ] **Step 3: Run slopwatch**

Run: `slopwatch analyze`
Expected: `0 issue(s) found`.

- [ ] **Step 4: Commit**

```bash
git add "src/Arius.Core/Features/RestoreCommand/RestoreCommand.cs" "src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs" "src/Arius.Core/Features/ListQuery/ListQuery.cs" "src/Arius.Core/Features/ListQuery/ListQueryHandler.cs" "src/Arius.Cli/Commands/Ls/LsVerb.cs" "src/Arius.Explorer/RepositoryExplorer/RepositoryExplorerViewModel.cs" "src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs" "src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs" "src/Arius.Explorer.Tests/RepositoryExplorer/RepositoryExplorerViewModelTests.cs"
git commit -m "refactor: type restore and list selector boundaries"
```
