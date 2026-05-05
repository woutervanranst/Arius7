# Slice 3 Typed Path Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish the remaining typed-path cleanup by executing three explicit sub-slices: fixture temp-root typing, Explorer/settings typed boundaries, and residual test-only cleanup.

**Architecture:** The umbrella plan keeps one path-boundary rule per sub-slice. `3A` types shared fixture temp-root APIs, `3B` centralizes Explorer local-root parsing near the owning settings model, and `3C` cleans up lower-leverage test callers that still rebuild typed roots from raw strings.

**Tech Stack:** C# 14, .NET 10, TUnit, WPF/MVVM for Arius Explorer, existing Arius path types (`LocalRootPath`, `RootedPath`, `RelativePath`, `PathSegment`)

---

## File Map

- `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`: shared integration/E2E fixture root creation and cleanup
- `src/Arius.E2E.Tests/Fixtures/E2EFixture.cs`: E2E wrapper over `RepositoryTestFixture`
- `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowRunner.cs`: representative workflow fixture creation entry point
- `src/Arius.Explorer/Settings/RepositoryOptions.cs`: persisted repository settings model
- `src/Arius.Explorer/Settings/ApplicationSettings.cs`: settings collection and recent-repository manager
- `src/Arius.Explorer/RepositoryExplorer/RepositoryExplorerViewModel.cs`: consumer that currently reparses repository local directories ad hoc
- selected tests in `src/Arius.Core.Tests/`, `src/Arius.Integration.Tests/`, and `src/Arius.Explorer.Tests/`: verification and residual cleanup

### Task 1 (3A): Type shared fixture temp-root APIs

**Files:**
- Modify: `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`
- Modify: `src/Arius.E2E.Tests/Fixtures/E2EFixture.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowRunner.cs`
- Test: fixture/E2E callers that compile against those signatures

- [ ] **Step 1: Write the failing test or compile-boundary adjustment**

Update one direct caller to pass a typed temp root instead of a string.

```csharp
var fixtureRoot = workflowRoot / PathSegment.Parse("fixture");

return await E2EFixture.CreateAsync(
    context.BlobContainer,
    context.AccountName,
    context.ContainerName,
    BlobTier.Cool,
    tempRoot: fixtureRoot,
    deleteTempRoot: static _ => { },
    cancellationToken: cancellationToken);
```

- [ ] **Step 2: Run focused verification to confirm it fails**

Run: `dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --no-restore`

Expected: FAIL because fixture APIs still require `string? tempRoot` and `Action<string>? deleteTempRoot`.

- [ ] **Step 3: Write the minimal implementation**

Change fixture APIs to typed temp-root boundaries.

```csharp
public static Task<RepositoryTestFixture> CreateWithPassphraseAsync(
    IBlobContainerService blobContainer,
    string accountName,
    string containerName,
    string? passphrase = null,
    LocalRootPath? tempRoot = null,
    Action<LocalRootPath>? deleteTempRoot = null,
    CancellationToken cancellationToken = default)
```

```csharp
private static (LocalRootPath TempRoot, LocalRootPath LocalRoot, LocalRootPath RestoreRoot) CreateTempRoots(LocalRootPath? tempRoot = null)
{
    var tempRootBase = LocalRootPath.Parse(Path.Combine(Path.GetTempPath(), TempRootFolderName));
    tempRootBase.CreateDirectory();

    var resolvedTempRoot = tempRoot ?? (tempRootBase / PathSegment.Parse($"arius-test-{Guid.NewGuid():N}"));
    var localRoot = resolvedTempRoot / PathSegment.Parse("source");
    var restoreRoot = resolvedTempRoot / PathSegment.Parse("restore");

    if (resolvedTempRoot.ExistsDirectory)
        resolvedTempRoot.DeleteDirectory(recursive: true);

    resolvedTempRoot.CreateDirectory();
    localRoot.CreateDirectory();
    restoreRoot.CreateDirectory();
    return (resolvedTempRoot, localRoot, restoreRoot);
}
```

- [ ] **Step 4: Run focused verification to confirm it passes**

Run:

```bash
dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --no-restore
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RoundtripTests/*"
```

Expected: PASS

### Task 2 (3B): Centralize Explorer local-root parsing at the owning settings boundary

**Files:**
- Modify: `src/Arius.Explorer/Settings/RepositoryOptions.cs`
- Modify: `src/Arius.Explorer/RepositoryExplorer/RepositoryExplorerViewModel.cs`
- Test: `src/Arius.Explorer.Tests/Settings/RepositoryOptionsTests.cs`
- Test: any Explorer ViewModel test covering repository load/restore/list behavior

- [ ] **Step 1: Write the failing test**

Add a test proving the owning settings model exposes a typed root.

```csharp
[Test]
public void LocalRoot_ParsesLocalDirectoryPath()
{
    var fullPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "arius-explorer-root"));
    var options = new RepositoryOptions { LocalDirectoryPath = fullPath };

    options.LocalRoot.ShouldBe(LocalRootPath.Parse(fullPath));
}
```

- [ ] **Step 2: Run the focused tests to verify they fail**

Run: `dotnet test --project "src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj" --treenode-filter "/*/*/RepositoryOptionsTests/*"`

Expected: FAIL because `RepositoryOptions` does not yet expose a typed root.

- [ ] **Step 3: Write the minimal implementation**

Add one typed property near the owning persisted model and switch consumers to it.

```csharp
[JsonIgnore, XmlIgnore]
public LocalRootPath LocalRoot => LocalRootPath.Parse(LocalDirectoryPath);
```

```csharp
var query = new ListQuery(new ListQueryOptions
{
    Prefix = node.Prefix,
    Recursive = false,
    LocalPath = Repository.LocalRoot,
});
```

```csharp
RootDirectory = Repository.LocalRoot,
```

- [ ] **Step 4: Run focused verification to confirm it passes**

Run:

```bash
dotnet test --project "src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj" --treenode-filter "/*/*/RepositoryOptionsTests/*"
dotnet test --project "src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj"
```

Expected: PASS

### Task 3 (3C): Clean residual test callers that still rebuild typed roots from strings

**Files:**
- Modify: `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/ContainerCreationTests.cs`
- Modify: any directly related test file where the root is clearly a typed repository/temp root rather than a string-format assertion

- [ ] **Step 1: Write the failing test or compile-boundary adjustment**

Update one targeted test to keep the root typed earlier.

```csharp
var tempRoot = LocalRootPath.Parse(Path.Combine(Path.GetTempPath(), $"arius-ls-{Guid.NewGuid():N}"));
tempRoot.CreateDirectory();

await (tempRoot / RelativePath.Parse("shared.txt")).WriteAllTextAsync("local-shared");
```

- [ ] **Step 2: Run focused verification to confirm it fails or exposes fallout**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/*"
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/ContainerCreationTests/*"
```

Expected: initial compile fallout or focused behavioral fallout where raw strings are still used to model typed roots.

- [ ] **Step 3: Write the minimal implementation**

Retype only the setup roots that are truly local-root values; keep raw strings in tests that intentionally verify full-path rendering.

- [ ] **Step 4: Run focused verification to confirm it passes**

Run the same focused suites again.

Expected: PASS

### Task 4: Broader verification after each executed sub-slice

**Files:**
- Modify: only immediate fallout files caused by the sub-slice being executed

- [ ] **Step 1: Run broader verification**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj"
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj"
dotnet test --project "src/Arius.Cli.Tests/Arius.Cli.Tests.csproj"
dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --no-restore
slopwatch analyze
```

Expected: PASS, with only the known recover-script integration skips.

## Self-Review

- Spec coverage: the plan explicitly breaks the umbrella into `3A`, `3B`, and `3C`, each with concrete files, rules, and verification.
- Placeholder scan: all tasks include concrete files, commands, and intended code shape.
- Type consistency: `LocalRootPath` stays the internal local-root carrier, persisted Explorer settings stay string-based, and residual test cleanup is limited to real typed-root modeling rather than string-format assertions.
