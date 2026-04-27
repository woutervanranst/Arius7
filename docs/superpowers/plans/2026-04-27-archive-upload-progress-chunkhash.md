# Archive Upload Progress ChunkHash Callback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace archive upload progress callback string identifiers with a `ChunkHash`-typed callback while keeping hash progress path-based.

**Architecture:** Refactor `ArchiveCommandOptions` in place so upload progress is keyed by `ChunkHash` in both the large-file and tar upload branches. Then update CLI wiring and focused tests to consume the typed callback directly.

**Tech Stack:** C#/.NET, TUnit, Arius typed hash value objects

---

### Task 1: Refactor Archive Upload Progress Callback API

**Files:**
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommand.cs`
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`

- [ ] Write a failing usage against the renamed typed callback.
- [ ] Replace the upload callback property with a `ChunkHash`-typed version and update both upload branches.

### Task 2: Update CLI Wiring And Tests

**Files:**
- Modify: `src/Arius.Cli/Commands/Archive/ArchiveVerb.cs`
- Modify: affected archive CLI tests

- [ ] Update CLI command construction to provide the new typed upload callback.
- [ ] Update focused tests to match the renamed property and typed argument.

### Task 3: Verify Focused Archive Behavior

**Files:**
- Modify only files above if verification exposes issues.

- [ ] Run focused Core and CLI archive tests for upload progress behavior.
