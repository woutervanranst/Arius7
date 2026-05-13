# Typed Filesystem Existence Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the remaining repository-relative `File.Exists` checks in fixture-driven tests and helpers with the existing `RelativeFileSystem.FileExists(...)` API while preserving real absolute-path boundary code.

**Architecture:** Keep `LocalDirectory.Resolve(...)` only at true host-path boundaries such as `File.OpenRead(...)`, `Directory.Delete(...)`, and external path-oriented APIs. Expose the already-constructed rooted filesystem from `ArchiveTestEnvironment`, then update the remaining Core, Integration, and E2E repository-relative existence assertions to use typed filesystem members directly.

**Tech Stack:** C# 13/.NET 10, TUnit, Shouldly, existing Arius typed filesystem abstractions (`LocalDirectory`, `RelativeFileSystem`, `RelativePath`)

---

### Task 1: Expose Archive Test Root FileSystem

**Files:**
- Modify: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveTestEnvironment.cs`
- Test: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs`

- [ ] **Step 1: Write the failing test usage**

Update the test in `ArchiveRecoveryTests` to use the API that does not exist yet:

```csharp
env.RootFileSystem.FileExists(relativePath).ShouldBeFalse();
env.RootFileSystem.FileExists(relativePath.AppendSuffix(".pointer.arius")).ShouldBeTrue();
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" -p:UseAppHost=false --treenode-filter "/*/*/ArchiveRecoveryTests/Archive_RemoveLocal_WritesPointerAndDeletesBinaryAtRelativePath"`
Expected: FAIL to compile because `ArchiveTestEnvironment` does not expose `RootFileSystem`.

- [ ] **Step 3: Write minimal implementation**

Expose the already-existing field from `ArchiveTestEnvironment`:

```csharp
public LocalDirectory RootDirectoryInfo => _rootDirectoryInfo;

internal RelativeFileSystem RootFileSystem => _rootFileSystem;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" -p:UseAppHost=false --treenode-filter "/*/*/ArchiveRecoveryTests/Archive_RemoveLocal_WritesPointerAndDeletesBinaryAtRelativePath"`
Expected: PASS

### Task 2: Convert Remaining Integration Relative Existence Assertions

**Files:**
- Modify: `src/Arius.Integration.Tests/Pipeline/RoundtripTests.cs`
- Test: `src/Arius.Integration.Tests/Pipeline/RoundtripTests.cs`

- [ ] **Step 1: Write the cleanup edits in tests**

Replace the repository-relative assertions:

```csharp
fix.LocalFileSystem.FileExists(relativePath.AppendSuffix(".pointer.arius")).ShouldBeTrue();
fix.LocalFileSystem.FileExists(relativePath).ShouldBeFalse();
fix.LocalFileSystem.FileExists(relativePath.AppendSuffix(".pointer.arius")).ShouldBeTrue();
```

in place of the current `File.Exists(fix.LocalDirectory.Resolve(...))` usages.

- [ ] **Step 2: Run tests to verify they still pass**

Run: `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" -p:UseAppHost=false --treenode-filter "/*/*/RoundtripTests/*"`
Expected: PASS

### Task 3: Convert Remaining E2E Relative Existence Assertions

**Files:**
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/Helpers.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs`
- Test: `src/Arius.E2E.Tests/Arius.E2E.Tests.csproj`

- [ ] **Step 1: Write the cleanup edit in workflow helpers**

Replace the pointer assertion in `Helpers.cs`:

```csharp
fixture.RestoreFileSystem.FileExists(RelativePath.Parse(relativePath).AppendSuffix(".pointer.arius")).ShouldBeTrue($"Expected pointer file for {relativePath}");
```

instead of resolving an absolute path and calling `File.Exists(...)`.

- [ ] **Step 2: Split the boundary check in archive tier restore assertions**

Keep the absolute path only for hashing:

```csharp
var restoredPath = readyRestoreDirectory.Resolve(targetChunk.TargetRelativePath);
await using var stream = File.OpenRead(restoredPath);
```

and add the rooted filesystem for pointer existence:

```csharp
readyRestoreFileSystem.FileExists(targetChunk.TargetRelativePath.AppendSuffix(".pointer.arius")).ShouldBeTrue(...);
```

- [ ] **Step 3: Run build to verify compilation**

Run: `dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" -p:UseAppHost=false`
Expected: PASS

### Task 4: Optional Consistency Cleanup in Restore Tests

**Files:**
- Modify: `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`
- Test: `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`

- [ ] **Step 1: Replace the direct existence assertion**

Replace:

```csharp
File.Exists(fixture.LocalDirectory.Resolve(relativePath)).ShouldBeTrue();
```

with:

```csharp
fixture.LocalFileSystem.FileExists(relativePath).ShouldBeTrue();
```

- [ ] **Step 2: Run targeted restore tests**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" -p:UseAppHost=false --treenode-filter "/*/*/RestoreCommandHandlerTests/*"`
Expected: PASS

### Task 5: Final Verification

**Files:**
- Verify only

- [ ] **Step 1: Run targeted Core verification**

Run: `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" -p:UseAppHost=false --treenode-filter "/*/*/ArchiveRecoveryTests/*"`
Expected: PASS

- [ ] **Step 2: Run targeted Integration verification**

Run: `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" -p:UseAppHost=false --treenode-filter "/*/*/RoundtripTests/*"`
Expected: PASS

- [ ] **Step 3: Run E2E project build**

Run: `dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" -p:UseAppHost=false`
Expected: PASS

- [ ] **Step 4: Run broader confidence verification**

Run:

```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" -p:UseAppHost=false
dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" -p:UseAppHost=false
```

Expected: PASS (with any known skips)
