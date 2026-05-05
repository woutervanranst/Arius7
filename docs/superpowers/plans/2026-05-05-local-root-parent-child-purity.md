# LocalRootPath Parent/Child Purity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add typed parent/child root operations to `LocalRootPath`, replace nearby root string round-trips, and define the remaining typed-path cleanup scope.

**Architecture:** Extend `LocalRootPath` with parent lookup and child-root composition so code can remain inside typed root boundaries. Update the small set of immediate production, E2E, and test callers that currently rebuild roots through strings, then verify with focused path and workflow coverage before broader suites.

**Tech Stack:** C# 14, .NET 10, TUnit, existing Arius path types (`LocalRootPath`, `RootedPath`, `RelativePath`, `PathSegment`)

---

### Task 1: Add typed parent/child root operations

**Files:**
- Modify: `src/Arius.Core/Shared/Paths/LocalRootPath.cs`
- Modify: `src/Arius.Core.Tests/Shared/LocalRootPathTests.cs`

- [ ] **Step 1: Write the failing tests**

Add tests for the new API surface and behavior.

```csharp
[Test]
public void Parent_ReturnsContainingRoot()
{
    var root = LocalRootPath.Parse(Path.Combine(Path.GetTempPath(), "arius-local-root", "child"));

    root.Parent.ShouldBe(LocalRootPath.Parse(Path.Combine(Path.GetTempPath(), "arius-local-root")));
}

[Test]
public void Parent_OfFilesystemRoot_IsNull()
{
    var root = LocalRootPath.Parse(Path.GetPathRoot(Path.GetTempPath())!);

    root.Parent.ShouldBeNull();
}

[Test]
public void Slash_PathSegment_ReturnsChildRoot()
{
    var root = LocalRootPath.Parse(Path.Combine(Path.GetTempPath(), "arius-local-root"));

    (root / PathSegment.Parse("snapshots"))
        .ShouldBe(LocalRootPath.Parse(Path.Combine(root.ToString(), "snapshots")));
}
```

- [ ] **Step 2: Run the focused tests to verify they fail**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/LocalRootPathTests/*"`

Expected: FAIL with compile fallout because `LocalRootPath` does not yet expose `Parent` or the `PathSegment` overload.

- [ ] **Step 3: Write the minimal implementation**

Add `Parent` and `operator /(LocalRootPath, PathSegment)`.

```csharp
public LocalRootPath? Parent
{
    get
    {
        var parent = Path.GetDirectoryName(Value);
        return string.IsNullOrEmpty(parent) ? null : Parse(parent);
    }
}

public static LocalRootPath operator /(LocalRootPath left, PathSegment right) =>
    Parse(Path.Combine(left.Value, right.ToString()));
```

- [ ] **Step 4: Run the focused tests to verify they pass**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/LocalRootPathTests/*"`

Expected: PASS

### Task 2: Replace impure root reconstruction in shared and E2E callers

**Files:**
- Modify: `src/Arius.Core/Shared/RepositoryPaths.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/RepresentativeWorkflowRunner.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/MaterializeVersionStep.cs`
- Test: `src/Arius.Core.Tests/Shared/RepositoryPathsTests.cs`

- [ ] **Step 1: Write the failing test adjustment**

Replace root-child assertions to use typed root composition.

```csharp
RepositoryPaths.GetChunkIndexCacheDirectory("account", "container")
    .ShouldBe(root / PathSegment.Parse("chunk-index"));
```

- [ ] **Step 2: Run the focused tests to verify they fail**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RepositoryPathsTests/*"`

Expected: FAIL if `RepositoryPaths` and callers still rebuild roots through `RelativePath` or string round-trips.

- [ ] **Step 3: Write the minimal implementation**

Use the new typed root APIs directly.

```csharp
public static LocalRootPath GetChunkIndexCacheDirectory(string accountName, string containerName)
    => GetRepositoryDirectory(accountName, containerName) / PathSegment.Parse("chunk-index");
```

```csharp
var workflowRoot = state.VersionedSourceRoot.Parent
    ?? throw new InvalidOperationException($"{Name}: representative workflow root is not available.");
var readyRestoreRoot = workflowRoot / PathSegment.Parse("archive-tier-ready");
```

```csharp
var versionRootPath = state.VersionedSourceRoot / PathSegment.Parse(nameof(SyntheticRepositoryVersion.V1));
return await SyntheticRepositoryMaterializer.MaterializeV1Async(state.Definition, state.Seed, versionRootPath, state.Fixture.Encryption);
```

- [ ] **Step 4: Run focused verification to confirm pass**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RepositoryPathsTests/*"
dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --no-restore
```

Expected: PASS

### Task 3: Replace similar test and fixture caller patterns

**Files:**
- Modify: `src/Arius.Integration.Tests/Pipeline/RoundtripTests.cs`
- Modify: any immediate fallout file revealed by compile verification in the same area

- [ ] **Step 1: Write the failing test adjustment**

Change restore-root child directories to typed composition.

```csharp
var v1Dir = fix.RestoreRoot / PathSegment.Parse("v1");
var v2Dir = fix.RestoreRoot / PathSegment.Parse("v2");
var latestDir = fix.RestoreRoot / PathSegment.Parse("latest");
```

- [ ] **Step 2: Run the focused tests to verify they fail**

Run: `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RoundtripTests/*"`

Expected: FAIL with compile fallout while string-based child roots remain.

- [ ] **Step 3: Write the minimal implementation**

Replace `LocalRootPath.Parse(Path.Combine(fix.RestoreRoot.ToString(), ...))` with `fix.RestoreRoot / PathSegment.Parse(...)` and pass those typed roots through restore calls unchanged.

- [ ] **Step 4: Run the focused tests to verify they pass**

Run: `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RoundtripTests/*"`

Expected: PASS

### Task 4: Broader verification and remaining-scope summary

**Files:**
- Modify: only fallout files directly caused by the purity cleanup

- [ ] **Step 1: Run broader verification**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj"
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj"
dotnet test --project "src/Arius.Cli.Tests/Arius.Cli.Tests.csproj"
dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --no-restore
slopwatch analyze
```

Expected: PASS, with the existing recover-script integration skips only.

- [ ] **Step 2: Summarize remaining slice 3 scope**

Call out the next remaining typed-path cleanup categories:

```text
- CLI/update/install temp-root composition still built from strings
- config/settings/viewmodel local-root values still carried as strings past true persistence/UI boundaries
- remaining test/fixture callers rebuilding child roots from root.ToString()
```

## Self-Review

- Spec coverage: the plan covers `LocalRootPath.Parent`, `LocalRootPath / PathSegment`, immediate production and E2E fallout, related tests, and the remaining slice 3 scope summary.
- Placeholder scan: all tasks contain concrete files, commands, and intended code shape.
- Type consistency: the plan keeps `LocalRootPath` for directory roots, `RootedPath` for rooted entries, and `PathSegment` for child-root composition.
