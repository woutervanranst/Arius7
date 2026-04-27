# Shared Hash Test Helper Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Consolidate repeated typed-hash test helpers into `Arius.Tests.Shared` without changing test behavior.

**Architecture:** Add one narrow static helper in `Arius.Tests.Shared` for creating deterministic 64-character hash values from a repeated character. Reference that helper from test projects that currently duplicate identical local methods.

**Tech Stack:** C#/.NET, TUnit, existing `Arius.Tests.Shared` test infrastructure

---

### Task 1: Add Shared Hash Test Helper

**Files:**
- Create: `src/Arius.Tests.Shared/Hashes/HashTestData.cs`
- Modify: `src/Arius.Tests.Shared/AssemblyMarker.cs`

- [ ] Add a single static helper with `Content(char c)`, `Chunk(char c)`, and `FileTree(char c)` methods.
- [ ] Expose it to the test assemblies through `InternalsVisibleTo`.

### Task 2: Reference The Shared Helper From Test Projects

**Files:**
- Modify: `src/Arius.Cli.Tests/Arius.Cli.Tests.csproj`
- Modify: `src/Arius.Core.Tests/Arius.Core.Tests.csproj`

- [ ] Add the minimal `Arius.Tests.Shared` project references needed for the shared helper.

### Task 3: Replace Duplicate Local Hash Helper Methods

**Files:**
- Modify: test files that currently declare identical `Content(char c)` / `Chunk(char c)` helpers.

- [ ] Remove only exact duplicate local helper methods.
- [ ] Replace their call sites with the shared helper.

### Task 4: Verify Affected Tests

**Files:**
- Modify only files above if verification exposes issues.

- [ ] Run focused CLI, Core, and Integration test coverage for files touched by the helper consolidation.
- [ ] Fix any failures minimally and rerun the affected suites.
