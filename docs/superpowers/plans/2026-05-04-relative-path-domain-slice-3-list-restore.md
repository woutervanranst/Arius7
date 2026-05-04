# Relative Path Domain Slice 3 List And Restore Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move restore and list internal repository-path state from raw strings to `RelativePath` while keeping current user-facing string surfaces only where they are still intentional boundaries.

**Architecture:** This slice types the restore pipeline model (`FileToRestore`) and the list query result model (`RepositoryEntry` and derived records) around `RelativePath`. It also moves list/restore path comparisons, subtree traversal, and local-path joins onto typed or boundary-owned APIs in `Shared/Paths`, instead of continuing ad hoc string concatenation and slash replacement in feature handlers.

**Tech Stack:** C# / .NET / TUnit

---

### File Structure

**Modify**
- `src/Arius.Core/Features/RestoreCommand/Models.cs`
- `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs`
- `src/Arius.Core/Features/ListQuery/ListQuery.cs`
- `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`
- `src/Arius.Core/Shared/Paths/RelativePath.cs`
- `src/Arius.Core.Tests/Features/RestoreCommand/` relevant tests
- `src/Arius.Core.Tests/Features/ListQuery/` relevant tests

**Potentially modify if compile fallout requires it**
- `src/Arius.Core/Features/RestoreCommand/Events.cs`
- `src/Arius.Core/Features/RestoreCommand/RestoreCommand.cs`
- `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`
- `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`

**Do not modify in this slice**
- `src/Arius.Core/Shared/FileTree/FileTreeModels.cs`
- `src/Arius.Core/Features/ArchiveCommand/`
- `src/Arius.Tests.Shared/`
- `src/Arius.E2E.Tests/`

Reason: this slice is about typed path state in list/restore feature flows. Filetree entry-name typing and broader fixture/E2E migration belong to later slices.

---

### Slice Intent

At the end of this slice:

- `FileToRestore.RelativePath` is typed as `RelativePath`
- `RepositoryEntry.RelativePath` and derived list result models are typed as `RelativePath`
- restore path filters, tree traversal, and local-path joins operate on typed values or `Shared/Paths` boundary APIs
- list path building and subtree traversal operate on typed values internally
- current user-facing filter/progress/event/query option surfaces may remain string-based where they are still true boundaries

---

### Task 1: Type Restore Pipeline Models

**Files:**
- Modify: `src/Arius.Core/Features/RestoreCommand/Models.cs`
- Test: `src/Arius.Core.Tests/Features/RestoreCommand/` relevant tests

- [ ] **Step 1: Write failing restore tests for typed `FileToRestore.RelativePath` usage**

Update or add focused tests so restore assertions use `RelativePath` rather than raw strings where they inspect internal restore models or behavior that now exposes typed paths.

Representative assertion shape:

```csharp
file.RelativePath.ShouldBe(RelativePath.Parse("docs/readme.txt"));
```

- [ ] **Step 2: Run focused restore tests to verify RED**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/*"
```

Expected:
- FAIL or compile break because `FileToRestore.RelativePath` is still `string`

- [ ] **Step 3: Change `FileToRestore.RelativePath` to `RelativePath`**

Target shape:

```csharp
using Arius.Core.Shared.Paths;

internal sealed record FileToRestore(
    RelativePath    RelativePath,
    ContentHash     ContentHash,
    DateTimeOffset  Created,
    DateTimeOffset  Modified);
```

- [ ] **Step 4: Run focused restore tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/*|/*/*/RelativePathTests/*|/*/*/PathSegmentTests/*"
```

Expected:
- PASS

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Core/Features/RestoreCommand/Models.cs src/Arius.Core.Tests/Features/RestoreCommand/
git commit -m "refactor: type restore pipeline paths"
```

---

### Task 2: Type Restore Traversal And Local Path Resolution

**Files:**
- Modify: `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs`
- Potentially modify: `src/Arius.Core/Features/RestoreCommand/Events.cs`
- Test: `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`

- [ ] **Step 1: Write failing tests for canonical restore path behavior at current boundaries**

Add or adjust one focused restore test that proves typed internal paths still produce canonical forward-slash text in the current string-based events/progress callbacks, and one focused test that proves local file writes still land at the expected OS path.

- [ ] **Step 2: Run focused restore tests to verify RED**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/*"
```

Expected:
- FAIL or compile break because restore handler still assumes string paths

- [ ] **Step 3: Convert restore traversal and matching to typed paths internally**

Required changes:
- parse normalized `targetPath` into `RelativePath` at the boundary when present
- keep tree walk internal `currentPath` as `RelativePath`
- construct child paths with typed composition instead of string concatenation where practical
- use `RelativePath.ToPlatformPath(opts.RootDirectory)` for local writes
- keep current event/progress payloads string-based only at publish/callback boundaries if those types are still string-based in this slice

- [ ] **Step 4: Prefer typed comparisons over string prefix logic**

Where current semantics are true canonical subtree semantics, use typed APIs (`StartsWith`, equality, parent/child composition) instead of ordinal-ignore-case string prefix checks.

If a comparison remains intentionally loose/user-input based, keep that looseness explicit at the boundary.

- [ ] **Step 5: Run focused restore tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/*|/*/*/RelativePathTests/*|/*/*/PathSegmentTests/*"
```

Expected:
- PASS

- [ ] **Step 6: Commit**

```bash
git add src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs src/Arius.Core/Features/RestoreCommand/Models.cs src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs
git commit -m "refactor: use typed paths in restore flow"
```

---

### Task 3: Type List Result Models

**Files:**
- Modify: `src/Arius.Core/Features/ListQuery/ListQuery.cs`
- Test: `src/Arius.Core.Tests/Features/ListQuery/` relevant tests

- [ ] **Step 1: Write failing list tests for typed `RepositoryEntry.RelativePath` usage**

Update or add focused list tests so emitted repository entries are asserted with `RelativePath` values rather than raw strings.

Representative shape:

```csharp
entry.RelativePath.ShouldBe(RelativePath.Parse("docs/readme.txt"));
```

- [ ] **Step 2: Run focused list tests to verify RED**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/*"
```

Expected:
- FAIL or compile break because list result models still expose `string`

- [ ] **Step 3: Change `RepositoryEntry.RelativePath` and derived records to `RelativePath`**

Target shape:

```csharp
using Arius.Core.Shared.Paths;

public abstract record RepositoryEntry(RelativePath RelativePath);
```

Derived file and directory records should follow that typed base model.

- [ ] **Step 4: Keep `ListQueryOptions.Prefix` string-based in this slice**

Do not type the public query options yet unless required. Parse canonical values at the handler boundary.

- [ ] **Step 5: Run focused list tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/*|/*/*/RelativePathTests/*|/*/*/PathSegmentTests/*"
```

Expected:
- PASS

- [ ] **Step 6: Commit**

```bash
git add src/Arius.Core/Features/ListQuery/ListQuery.cs src/Arius.Core.Tests/Features/ListQuery/
git commit -m "refactor: type list query result paths"
```

---

### Task 4: Type List Traversal And Path Building

**Files:**
- Modify: `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`
- Test: `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`

- [ ] **Step 1: Write failing tests for typed list traversal and canonical string boundaries**

Add or adjust focused tests that prove:
- directory and file entries are built from typed internal paths
- current external formatting remains canonical
- local merge behavior still works

- [ ] **Step 2: Run focused list tests to verify RED**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/*"
```

Expected:
- FAIL or compile break because list handler still builds and compares raw strings internally

- [ ] **Step 3: Replace string path-building helpers with typed path operations**

Required changes:
- keep `currentRelativeDirectory` as `RelativePath`
- build child file/directory paths with typed composition rather than string concatenation
- parse canonical prefix filters at the boundary when possible
- keep any intentionally loose prefix/filter semantics explicit and localized

- [ ] **Step 4: Use `Shared/Paths` boundary APIs for local OS path joins**

When merging local filesystem state or resolving local directories/files, prefer `RelativePath.ToPlatformPath(root)` or another `Shared/Paths` boundary API rather than inline `Replace('/', Path.DirectorySeparatorChar)` logic.

- [ ] **Step 5: Run focused list tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ListQueryHandlerTests/*|/*/*/RelativePathTests/*|/*/*/PathSegmentTests/*"
```

Expected:
- PASS

- [ ] **Step 6: Commit**

```bash
git add src/Arius.Core/Features/ListQuery/ListQueryHandler.cs src/Arius.Core/Features/ListQuery/ListQuery.cs src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs
git commit -m "refactor: use typed paths in list traversal"
```

---

### Task 5: Verify Slice 3 End-To-End

**Files:**
- Verify only:
  - `src/Arius.Core/Features/RestoreCommand/`
  - `src/Arius.Core/Features/ListQuery/`
  - `src/Arius.Core/Shared/Paths/RelativePath.cs`
  - related tests

- [ ] **Step 1: Run focused restore/list/path regressions**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/*|/*/*/ListQueryHandlerTests/*|/*/*/RelativePathTests/*|/*/*/PathSegmentTests/*"
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

- Prefer `RelativePath` for internal repository-path state as soon as the path enters list/restore domain code.
- Keep public query options, current event payloads, and current progress callback payloads string-based only if they are still intentional boundaries in this slice.
- Prefer `Shared/Paths` boundary APIs for local filesystem conversion rather than feature-local slash replacement.
- Do not type `RestoreOptions.RootDirectory` or `ListQueryOptions.LocalPath`; those are host filesystem boundaries.
- Do not touch `FileTreeEntry.Name` typing in this slice.

### Self-Review

**Spec coverage**
- restore internal path state becomes typed: Tasks 1-2.
- list internal/result path state becomes typed: Tasks 3-4.
- local-path joins move to shared path boundary APIs: Tasks 2 and 4.
- filetree model typing remains deferred: enforced by file list and notes.

**Placeholder scan**
- No `TODO` / `TBD` placeholders.
- Each task has concrete files, verification commands, and scope.
- The plan avoids generic “fix fallout” instructions without naming the focused test commands.

**Type consistency**
- `RelativePath` is the internal repository-path type throughout the slice.
- string remains only at explicit query/event/progress/user-input boundaries where intentionally preserved.
