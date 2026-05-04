# Relative Path Adjacent Event Boundaries Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Promote adjacent repository-path event/result surfaces from `string` to `RelativePath` where those surfaces now carry first-class domain path identity.

**Architecture:** Keep true text boundaries string-based: user input options, CLI rendering, and download-progress callback identifiers. Type adjacent domain/event boundaries instead: restore events and hydration-status results. Consumers should convert to text only at display or legacy string API boundaries.

**Tech Stack:** C# / .NET / TUnit / TUnit + NSubstitute

---

### File Structure

**Modify**
- `src/Arius.Core/Features/RestoreCommand/Events.cs`
- `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs`
- `src/Arius.Core/Features/ChunkHydrationStatusQuery/ChunkHydrationStatusQuery.cs`
- `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`
- `src/Arius.Core.Tests/Features/ChunkHydrationStatusQuery/ResolveFileHydrationStatusesHandlerTests.cs`
- `src/Arius.Integration.Tests/Pipeline/RestoreDispositionTests.cs`
- `src/Arius.Explorer/RepositoryExplorer/RepositoryExplorerViewModel.cs`
- `src/Arius.Explorer/RepositoryExplorer/FileItemViewModel.cs`
- `src/Arius.Explorer.Tests/RepositoryExplorer/FileItemViewModelTests.cs`
- `src/Arius.Explorer.Tests/RepositoryExplorer/RepositoryExplorerViewModelTests.cs`

**Potentially modify if compile fallout requires it**
- `src/Arius.Cli/Commands/Restore/RestoreProgressHandlers.cs`
- `src/Arius.Cli.Tests/Commands/Restore/*.cs`
- `src/Arius.Cli.Tests/MediatorEventRoutingIntegrationTests.cs`

**Do not modify in this follow-up**
- `src/Arius.Core/Features/RestoreCommand/RestoreCommand.cs`
- `src/Arius.Core/Features/ListQuery/ListQuery.cs`
- `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`

Reason: this pass is only about adjacent event/result path identity surfaces, not broader option or UI-command boundary redesign.

---

### Task 1: Type Restore Events

**Files:**
- Modify: `src/Arius.Core/Features/RestoreCommand/Events.cs`
- Modify: `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs`
- Test: `src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs`
- Test: `src/Arius.Integration.Tests/Pipeline/RestoreDispositionTests.cs`

- [ ] **Step 1: Write failing tests for typed restore event payloads**

Add or update focused tests so restore event assertions use `RelativePath.Parse(...)` for `FileDispositionEvent`, `FileSkippedEvent`, and `FileRestoredEvent`.

- [ ] **Step 2: Run focused restore tests to verify RED**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/*"
```

Expected:
- FAIL or compile break because restore events still expose `string`

- [ ] **Step 3: Change restore event path payloads to `RelativePath`**

Type these records:
- `FileRestoredEvent`
- `FileSkippedEvent`
- `FileDispositionEvent`

Update the restore handler to publish typed values directly.

- [ ] **Step 4: Keep callback identifiers string-based**

Do not change `RestoreOptions.CreateDownloadProgress`. It remains a text boundary and should keep receiving canonical `RelativePath.ToString()` text.

- [ ] **Step 5: Run focused restore tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/*"
```

Expected:
- PASS

---

### Task 2: Type Hydration Status Results

**Files:**
- Modify: `src/Arius.Core/Features/ChunkHydrationStatusQuery/ChunkHydrationStatusQuery.cs`
- Test: `src/Arius.Core.Tests/Features/ChunkHydrationStatusQuery/ResolveFileHydrationStatusesHandlerTests.cs`

- [ ] **Step 1: Write failing tests for typed hydration result paths**

Update focused hydration-status tests so `ChunkHydrationStatusResult.RelativePath` is asserted as `RelativePath`.

- [ ] **Step 2: Run focused hydration tests to verify RED**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ResolveFileHydrationStatusesHandlerTests/*"
```

Expected:
- FAIL or compile break because hydration status results still expose `string`

- [ ] **Step 3: Change `ChunkHydrationStatusResult.RelativePath` to `RelativePath`**

Keep the query input untouched; only type the emitted result model and the handler yield sites.

- [ ] **Step 4: Run focused hydration tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ResolveFileHydrationStatusesHandlerTests/*"
```

Expected:
- PASS

---

### Task 3: Update Explorer And Other Adjacent Consumers

**Files:**
- Modify: `src/Arius.Explorer/RepositoryExplorer/RepositoryExplorerViewModel.cs`
- Modify: `src/Arius.Explorer/RepositoryExplorer/FileItemViewModel.cs`
- Test: `src/Arius.Explorer.Tests/RepositoryExplorer/FileItemViewModelTests.cs`
- Test: `src/Arius.Explorer.Tests/RepositoryExplorer/RepositoryExplorerViewModelTests.cs`
- Potentially modify: CLI restore progress handlers/tests if compile fallout reaches them

- [ ] **Step 1: Write failing consumer tests for typed path payloads**

Update Explorer tests to construct `RepositoryFileEntry` and `RepositoryDirectoryEntry` with `RelativePath.Parse(...)`, and to compare typed hydration results where relevant.

- [ ] **Step 2: Run focused Explorer tests to verify RED**

Run:
```bash
dotnet test --project "src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj"
```

Expected:
- FAIL or compile break because Explorer still assumes string path payloads in adjacent event/result consumers

- [ ] **Step 3: Convert consumers to text only at display boundaries**

Required changes:
- use `.ToString()` when setting tree-node prefixes or command `TargetPath`
- use `.ToString()` or `Path.GetFileName(file.RelativePath.ToString())` when rendering file names
- keep dictionary keys typed where practical; use strings only if a framework API requires it

- [ ] **Step 4: Run focused Explorer tests to verify GREEN**

Run:
```bash
dotnet test --project "src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj"
```

Expected:
- PASS

---

### Task 4: Verify And Commit

**Files:**
- Verify only the files touched above

- [ ] **Step 1: Run focused regressions for restore, hydration, and Explorer**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/*|/*/*/ResolveFileHydrationStatusesHandlerTests/*"
```

Run:
```bash
dotnet test --project "src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj"
```

Expected:
- PASS

- [ ] **Step 2: Run full relevant suites**

Run:
```bash
dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj"
```

Run:
```bash
dotnet test --project "src/Arius.Explorer.Tests/Arius.Explorer.Tests.csproj"
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

- [ ] **Step 4: Commit**

```bash
git add src/Arius.Core/Features/RestoreCommand/Events.cs src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs src/Arius.Core/Features/ChunkHydrationStatusQuery/ChunkHydrationStatusQuery.cs src/Arius.Core.Tests/Features/RestoreCommand/RestoreCommandHandlerTests.cs src/Arius.Core.Tests/Features/ChunkHydrationStatusQuery/ResolveFileHydrationStatusesHandlerTests.cs src/Arius.Integration.Tests/Pipeline/RestoreDispositionTests.cs src/Arius.Explorer/RepositoryExplorer/FileItemViewModel.cs src/Arius.Explorer/RepositoryExplorer/RepositoryExplorerViewModel.cs src/Arius.Explorer.Tests/RepositoryExplorer/FileItemViewModelTests.cs src/Arius.Explorer.Tests/RepositoryExplorer/RepositoryExplorerViewModelTests.cs docs/superpowers/plans/2026-05-04-relative-path-adjacent-event-boundaries.md
git commit -m "refactor: type adjacent path event boundaries"
```

---

### Notes For The Implementer

- `RelativePath` is acceptable in command events/results because it is now a public first-class domain object.
- Do not expand this pass to user-input options or CLI callback APIs.
- Prefer direct typed flow over adding conversion helpers.
- Convert to string only where a framework/UI/display API truly requires it.

### Self-Review

**Spec coverage**
- restore adjacent events typed: Task 1.
- hydration status adjacent result typed: Task 2.
- consumers updated to render/bridge at true text boundaries: Task 3.

**Placeholder scan**
- No TODO/TBD placeholders.
- Each task includes concrete files and commands.

**Type consistency**
- `RelativePath` is the adjacent path identity type throughout this follow-up.
- text stays only at explicit rendering/callback/input boundaries.
