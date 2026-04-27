# Typed Hashes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace stringly/raw-byte hash handling with typed hash value objects (`ContentHash`, `ChunkHash`, `FileTreeHash`), split file-tree entries into file and directory entry types, and add file-path hashing support with optional progress.

**Architecture:** Introduce small immutable hash value objects with shared validation/formatting utilities, keep persisted wire/storage formats as canonical lowercase hex strings, then migrate core services and feature handlers from `string`/`byte[]` hash plumbing to typed APIs. Replace the tagged-union `FileTreeEntry.Hash` model with explicit file and directory entry types so content hashes and tree hashes are distinct in memory.

**Tech Stack:** C#/.NET, TUnit, existing Arius.Core services, `IProgress<long>`, `ProgressStream`

---

### Task 1: Add Core Hash Value Objects And Shared Utility

**Files:**
- Create: `src/Arius.Core/Shared/Hashes/HashCodec.cs`
- Create: `src/Arius.Core/Shared/Hashes/ContentHash.cs`
- Create: `src/Arius.Core/Shared/Hashes/ChunkHash.cs`
- Create: `src/Arius.Core/Shared/Hashes/FileTreeHash.cs`
- Test: `src/Arius.Core.Tests/Shared/Hashes/ContentHashTests.cs`
- Test: `src/Arius.Core.Tests/Shared/Hashes/ChunkHashTests.cs`
- Test: `src/Arius.Core.Tests/Shared/Hashes/FileTreeHashTests.cs`

- [ ] Write failing tests for parsing, normalization, and digest formatting.
- [ ] Run the focused tests and verify they fail because the types do not exist.
- [ ] Implement the minimal hash utility and value objects.
- [ ] Run the focused tests and verify they pass.

### Task 2: Change `IEncryptionService` To Return Typed Hashes And Add File-Path Hashing

**Files:**
- Modify: `src/Arius.Core/Shared/Encryption/IEncryptionService.cs`
- Modify: `src/Arius.Core/Shared/Encryption/PlaintextPassthroughService.cs`
- Modify: `src/Arius.Core/Shared/Encryption/PassphraseEncryptionService.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/Fakes/CbcEncryptionServiceAdapter.cs`
- Test: `src/Arius.Core.Tests/Shared/Encryption/PlaintextPassthroughServiceTests.cs`
- Test: `src/Arius.Core.Tests/Shared/Encryption/PassphraseEncryptionServiceTests.cs`

- [ ] Write failing tests for typed return values and `ComputeHashAsync(string filePath, IProgress<long>?, CancellationToken)`.
- [ ] Run the focused tests and verify they fail.
- [ ] Implement the new signatures, optional progress support, and allocation reductions in passphrase hashing.
- [ ] Run the focused tests and verify they pass.

### Task 3: Migrate Pointer Files And Local Enumeration To `ContentHash`

**Files:**
- Modify: `src/Arius.Core/Shared/LocalFile/LocalFileEnumerator.cs`
- Modify: `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`
- Test: `src/Arius.Core.Tests/Shared/LocalFile/LocalFileEnumeratorTests.cs`
- Test: `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`

- [ ] Write failing tests for pointer-hash parsing through `ContentHash.TryParse`.
- [ ] Run the focused tests and verify they fail.
- [ ] Replace ad hoc hex parsing with typed content-hash parsing.
- [ ] Run the focused tests and verify they pass.

### Task 4: Type The Chunk Index And Chunk Storage APIs

**Files:**
- Modify: `src/Arius.Core/Shared/ChunkIndex/Shard.cs`
- Modify: `src/Arius.Core/Shared/ChunkIndex/ChunkIndexService.cs`
- Modify: `src/Arius.Core/Shared/ChunkStorage/IChunkStorageService.cs`
- Modify: `src/Arius.Core/Shared/ChunkStorage/ChunkStorageService.cs`
- Modify: `src/Arius.Core/Shared/Storage/BlobConstants.cs`
- Test: affected files under `src/Arius.Core.Tests/Shared/ChunkIndex/`
- Test: `src/Arius.Core.Tests/Features/ChunkHydrationStatusQuery/ResolveFileHydrationStatusesHandlerTests.cs`

- [ ] Write failing tests for typed content/chunk hash parsing and lookup.
- [ ] Run the focused tests and verify they fail.
- [ ] Migrate the chunk index and storage APIs to `ContentHash` and `ChunkHash`.
- [ ] Run the focused tests and verify they pass.

### Task 5: Split File Tree Entries Into File And Directory Entry Types

**Files:**
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeModels.cs`
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeBlobSerializer.cs`
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`
- Modify: `src/Arius.Core/Shared/FileTree/FileTreeService.cs`
- Modify: `src/Arius.Core/Shared/Snapshot/SnapshotService.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeBlobSerializerTests.cs`
- Test: `src/Arius.Core.Tests/Shared/FileTree/FileTreeServiceTests.cs`
- Test: dependent file-tree consumers in `src/Arius.Core.Tests/Features/`

- [ ] Write failing tests for explicit file vs directory entry hash types.
- [ ] Run the focused tests and verify they fail.
- [ ] Replace the tagged-union `FileTreeEntry.Hash` model with separate file and directory entry types while keeping the serialized format unchanged.
- [ ] Type snapshot root hashes as `FileTreeHash` and run the focused tests until they pass.

### Task 6: Migrate Archive And Restore Pipelines To Typed Hashes

**Files:**
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`
- Modify: `src/Arius.Core/Features/ArchiveCommand/Models.cs`
- Modify: `src/Arius.Core/Features/ArchiveCommand/Events.cs`
- Modify: `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs`
- Modify: `src/Arius.Core/Features/RestoreCommand/Models.cs`
- Modify: `src/Arius.Core/Features/RestoreCommand/Events.cs`
- Test: affected files under `src/Arius.Core.Tests/Features/ArchiveCommand/`
- Test: affected files under `src/Arius.Core.Tests/Features/RestoreCommand/`

- [ ] Write failing tests for typed archive/restore pipeline models and events.
- [ ] Run the focused tests and verify they fail.
- [ ] Migrate handlers and models to typed hashes, using file-path hashing where it removes boilerplate.
- [ ] Run the focused tests and verify they pass.

### Task 7: Update E2E, Integration, And Remaining Helper Patterns

**Files:**
- Modify: `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryMaterializer.cs`
- Modify: `src/Arius.E2E.Tests/Datasets/SyntheticRepositoryStateAssertions.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/Helpers.cs`
- Modify: `src/Arius.E2E.Tests/Workflows/Steps/ArchiveTierLifecycleStep.cs`
- Modify: `src/Arius.Integration.Tests/Pipeline/RecoveryScriptTests.cs`
- Modify: `src/Arius.Core.Tests/Features/ListQuery/ListQueryHandlerTests.cs`
- Modify: `src/Arius.Core.Tests/Features/ChunkHydrationStatusQuery/ResolveFileHydrationStatusesHandlerTests.cs`
- Modify: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs`
- Modify: `src/Arius.Core.Tests/Shared/Encryption/GoldenFileDecryptionTests.cs`

- [ ] Replace `HashFor(...)` helpers and repeated `Convert.ToHexString(...).ToLowerInvariant()` patterns with typed hashes.
- [ ] Run the affected suites and verify they fail where expected.
- [ ] Apply the minimal test/helper migrations.
- [ ] Run the affected suites and verify they pass.

### Task 8: Final Verification And Cleanup

**Files:**
- Modify only files touched above if verification exposes issues.

- [ ] Grep for obsolete hashing patterns and remove any remaining production occurrences.
- [ ] Run the relevant test projects.
- [ ] Fix any failures minimally and rerun impacted suites.
- [ ] Stop before any commit or PR work unless explicitly requested.
