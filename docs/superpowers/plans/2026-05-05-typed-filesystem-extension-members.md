# Typed Filesystem Extension Members Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a typed local-filesystem API using C# 14 extension members and migrate the selected production and test call sites away from raw `string`-based `System.IO` usage.

**Architecture:** Keep the existing path/domain types pure and layer typed filesystem behavior on top through extension members for `RootedPath`, `LocalRootPath`, and `PathSegment`. Expose async-only copy APIs (`CopyToAsync`, `CopyDirectoryAsync`) and migrate the real Arius callers that benefit from them rather than keeping synchronous duplicates.

**Tech Stack:** C# 14 extension members on .NET 10, TUnit, Arius path types (`RelativePath`, `LocalRootPath`, `RootedPath`, `PathSegment`), `System.IO` async stream operations

---

### Task 1: Enable And Prove Extension Members

**Files:**
- Modify: any repo-level project config needed to enable C# 14 extension-member syntax
- Create or Modify: focused path tests in `src/Arius.Core.Tests/Shared/Paths/`
- Test: `src/Arius.Core.Tests/**/*.cs`

- [ ] **Step 1: Write the failing test**

Add or update a focused Core test that uses the intended extension-member syntax on `RootedPath` or `PathSegment`, such as `path.Extension` or `path.ExistsFile`, so the compiler must prove the syntax and receiver shape.

- [ ] **Step 2: Run test to verify it fails**

Run:
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RootedPath*Tests/*"`

Expected: FAIL or compile failure because the extension-member syntax or members are not implemented yet.

- [ ] **Step 3: Write minimal implementation**

Make the smallest change required to let the repo compile and recognize C# 14 extension members. If the current SDK/toolchain requires explicit language-version enablement, add only that minimal configuration.

- [ ] **Step 4: Run tests to verify they pass**

Run:
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RootedPath*Tests/*"`

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src docs
git commit -m "build: enable filesystem extension members"
```

### Task 2: Add Core Typed Filesystem Extension Members

**Files:**
- Create: `src/Arius.Core/Shared/Paths/RootedPathFileSystemExtensions.cs`
- Create: `src/Arius.Core/Shared/Paths/LocalRootPathFileSystemExtensions.cs`
- Create: `src/Arius.Core/Shared/Paths/PathSegmentExtensions.cs`
- Modify: `src/Arius.Core.Tests/Shared/Paths/**/*.cs`
- Test: `src/Arius.Core.Tests/Shared/Paths/**/*.cs`

- [ ] **Step 1: Write the failing test**

Add focused Core tests for:

- `RootedPath.ExistsFile`
- `RootedPath.ExistsDirectory`
- `RootedPath.Extension`
- `RootedPath.CreationTimeUtc` getter/setter
- `RootedPath.LastWriteTimeUtc` getter/setter
- `LocalRootPath.ExistsDirectory`
- `PathSegment.Extension`

Prefer one small test class per receiver type.

- [ ] **Step 2: Run test to verify it fails**

Run:
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RootedPathFileSystemExtensionsTests/*"`
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/LocalRootPathFileSystemExtensionsTests/*"`

Expected: FAIL or compile failure because the extension members do not exist yet.

- [ ] **Step 3: Write minimal implementation**

Implement the non-copy extension members only:

- `ExistsFile`
- `ExistsDirectory`
- `Extension`
- `Length`
- `CreationTimeUtc { get; set; }`
- `LastWriteTimeUtc { get; set; }`
- `OpenRead()`
- `OpenWrite()`
- `ReadAllText()`
- `ReadAllTextAsync(...)`
- `ReadAllBytesAsync(...)`
- `WriteAllTextAsync(...)`
- `DeleteFile()`
- `CreateDirectory()`
- `DeleteDirectory(...)`

For the generic timestamp properties, fail fast when the target path does not exist.

- [ ] **Step 4: Run tests to verify they pass**

Run:
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RootedPathFileSystemExtensionsTests/*"`
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/LocalRootPathFileSystemExtensionsTests/*"`
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/PathSegment*Tests/*"`

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core src/Arius.Core.Tests docs
git commit -m "feat: add typed filesystem extension members"
```

### Task 3: Add Async Copy APIs

**Files:**
- Modify: `src/Arius.Core/Shared/Paths/RootedPathFileSystemExtensions.cs`
- Modify: `src/Arius.Tests.Shared/IO/FileSystemHelper.cs`
- Modify: focused tests in `src/Arius.Core.Tests/Shared/Paths/` and `src/Arius.Core.Tests/` or `src/Arius.Integration.Tests/`
- Test: `src/Arius.Core.Tests/**/*.cs`
- Test: `src/Arius.Integration.Tests/**/*.cs`

- [ ] **Step 1: Write the failing test**

Add focused tests for:

- `RootedPath.CopyToAsync(...)`
- `FileSystemHelper.CopyDirectoryAsync(LocalRootPath, LocalRootPath, ...)`

The tests should verify file content and timestamp preservation.

- [ ] **Step 2: Run test to verify it fails**

Run:
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RootedPathFileSystemExtensionsTests/*"`
- `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RestorePointerTimestampTests/*"`

Expected: FAIL or compile failure because the async copy APIs do not exist yet.

- [ ] **Step 3: Write minimal implementation**

Implement:

- `RootedPath.CopyToAsync(...)` using async stream copy
- `FileSystemHelper.CopyDirectoryAsync(...)` using typed roots plus async file copies

Do not add sync `CopyTo` or sync `CopyDirectory`.

- [ ] **Step 4: Run tests to verify they pass**

Run:
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RootedPathFileSystemExtensionsTests/*"`
- `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RestorePointerTimestampTests/*"`

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core src/Arius.Tests.Shared src/Arius.Core.Tests src/Arius.Integration.Tests docs
git commit -m "feat: add async typed file copy APIs"
```

### Task 4: Migrate Production Filesystem Call Sites

**Files:**
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`
- Modify: `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`
- Modify: `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs`
- Modify: `src/Arius.Core/Shared/LocalFile/LocalFileEnumerator.cs`
- Modify: fallout tests in `src/Arius.Core.Tests/` and `src/Arius.Cli.Tests/`
- Test: `src/Arius.Core.Tests/**/*.cs`
- Test: `src/Arius.Cli.Tests/**/*.cs`

- [ ] **Step 1: Write the failing test**

Update focused tests so production call sites are expected to use the typed filesystem surface rather than raw `File.*` / `Directory.*` plus `.FullPath` string escape patterns.

- [ ] **Step 2: Run test to verify it fails**

Run:
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/LocalFileEnumeratorTests/*"`
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/*"`
- `dotnet test --project "src/Arius.Cli.Tests/Arius.Cli.Tests.csproj"`

Expected: FAIL or compile failure because the new typed filesystem API is not yet wired into production callers.

- [ ] **Step 3: Write minimal implementation**

Migrate the selected production call sites to the new extension members. Use async APIs where available and where the call flow is already async. Convert to raw strings only at true outer boundaries such as logs or APIs that still inherently require plain text.

- [ ] **Step 4: Run tests to verify they pass**

Run:
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/LocalFileEnumeratorTests/*"`
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/*"`
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/*"`
- `dotnet test --project "src/Arius.Cli.Tests/Arius.Cli.Tests.csproj"`

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core src/Arius.Core.Tests src/Arius.Cli.Tests docs
git commit -m "refactor: migrate production filesystem call sites"
```

### Task 5: Migrate Shared Test, Benchmark, Integration, And E2E Call Sites

**Files:**
- Modify: `src/Arius.Tests.Shared/Fixtures/RepositoryTestFixture.cs`
- Modify: `src/Arius.Benchmarks/ArchiveStepBenchmarks.cs`
- Modify: relevant files in `src/Arius.Integration.Tests/`
- Modify: relevant files in `src/Arius.E2E.Tests/`
- Modify: `Usings.cs` files if needed for ergonomic extension-member consumption
- Test: `src/Arius.Integration.Tests/**/*.cs`
- Test: `src/Arius.E2E.Tests/**/*.cs`
- Test: benchmark build

- [ ] **Step 1: Write the failing test**

Update one representative integration/E2E path so async directory copy and typed timestamp/file operations are required by the test code itself.

- [ ] **Step 2: Run test to verify it fails**

Run:
- `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RoundtripTests/*"`
- `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/Canonical_Representative_Workflow_Runs_On_Supported_Backends"`

Expected: FAIL or compile failure because the test/shared callers still depend on string-based filesystem APIs.

- [ ] **Step 3: Write minimal implementation**

Migrate the selected shared-test, integration, benchmark, and E2E call sites to the typed filesystem extension members and async `CopyDirectoryAsync(...)`.

- [ ] **Step 4: Run tests to verify they pass**

Run:
- `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RestorePointerTimestampTests/*"`
- `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj" --treenode-filter "/*/*/RoundtripTests/*"`
- `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --treenode-filter "/*/*/*/Canonical_Representative_Workflow_Runs_On_Supported_Backends"`
- `dotnet build "src/Arius.Benchmarks/Arius.Benchmarks.csproj"`

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Tests.Shared src/Arius.Benchmarks src/Arius.Integration.Tests src/Arius.E2E.Tests docs
git commit -m "test: migrate typed filesystem helpers"
```

### Task 6: Broad Verification And Final Review

**Files:**
- Modify: any small fallout files needed from previous tasks only
- Test: `src/Arius.Core.Tests/Arius.Core.Tests.csproj`
- Test: `src/Arius.Integration.Tests/Arius.Integration.Tests.csproj`
- Test: `src/Arius.E2E.Tests/Arius.E2E.Tests.csproj`

- [ ] **Step 1: Run broader verification**

Run sequentially:

- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj"`
- `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj"`
- `dotnet test --project "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj"`
- `dotnet test --project "src/Arius.Cli.Tests/Arius.Cli.Tests.csproj"`

Expected: PASS

- [ ] **Step 2: Run slopwatch if available**

Run:
- `dotnet tool run slopwatch analyze`

Expected: `0 issue(s) found` or a clear tool-not-found note if the manifest still lacks the command.

- [ ] **Step 3: Commit final fallout if any**

```bash
git add src docs
git commit -m "test: verify typed filesystem extensions"
```
