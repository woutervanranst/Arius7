# Typed Hash Conversion Overloads Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add first-class typed-hash conversion overloads so call sites stop converting through strings when moving between compatible hash identities.

**Architecture:** Extend the existing hash value objects with overloads that accept other typed hashes, keeping the current string-based APIs intact. Then replace the existing production and test occurrences that spell the conversion as `Parse(x.ToString())` with the typed overloads.

**Tech Stack:** C#/.NET, TUnit, Arius typed hash value objects

---

### Task 1: Add Typed Hash Conversion Overloads

**Files:**
- Modify: `src/Arius.Core/Shared/Hashes/ChunkHash.cs`
- Modify: `src/Arius.Core/Shared/Hashes/ContentHash.cs`
- Modify: `src/Arius.Core/Shared/Hashes/FileTreeHash.cs`
- Test: `src/Arius.Core.Tests/Shared/Hashes/HashValueObjectTests.cs`

- [ ] Add failing tests that prove typed overloads exist and preserve the canonical lowercase hex value.
- [ ] Implement only the overloads required by current call sites.

### Task 2: Replace Production String-Hop Conversions

**Files:**
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`
- Modify: `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs`
- Modify: `src/Arius.Cli/Commands/Archive/ArchiveProgressHandlers.cs`

- [ ] Replace `Parse(x.ToString())` production conversions with the new typed overloads.

### Task 3: Replace Matching Test Conversions

**Files:**
- Modify only test files that currently use the same string-hop pattern.

- [ ] Replace test/helper conversions with the new typed overloads.

### Task 4: Verify Affected Behavior

**Files:**
- Modify only files above if verification exposes issues.

- [ ] Run focused Core and CLI test coverage for the overload and archive/restore call sites.
- [ ] Run any additional targeted tests touched by refactoring.
