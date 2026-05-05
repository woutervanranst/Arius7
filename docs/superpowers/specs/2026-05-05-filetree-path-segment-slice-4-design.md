# Filetree PathSegment Slice 4 Design

## Context

The original relative-path domain design intentionally deferred filetree model cleanup until a later slice. That deferred work is still open.

Today the in-memory filetree model still stores child names as raw `string` values:

- `FileTreeEntry.Name`
- `DirectoryEntry.Name`
- `StagedDirectoryEntry.Name`

That leaks serializer formatting into the domain model because directory names are represented in memory as values like `"photos/"` instead of as one typed child segment plus directory entry kind.

This creates three problems:

- directory identity still depends on trailing-slash text in memory
- filetree code has to trim and rebuild serializer-shaped names repeatedly
- tests and helper code continue to construct filetree entries with stringly names rather than typed path-domain values

The next step is to finish the original slice 4 by making in-memory filetree entry names typed.

For this slice, backward compatibility with existing archives is not required. We can therefore simplify both the in-memory model and the persisted/staged filetree text format together instead of carrying compatibility-oriented directory-slash conventions forward.

## Goals

- Change in-memory filetree entry names from `string` to `PathSegment`.
- Remove trailing slash formatting from both the in-memory filetree model and filetree persistence.
- Keep filetree child-name identity separate from directory/file kind.
- Update filetree writer, serializer, builder, and nearby tests/helpers to operate on typed single-segment names.
- Remove trailing-slash directory naming from persisted and staged filetree text where entry kind already identifies directories.

## Non-Goals

- Do not redesign list or restore prefix semantics.
- Do not expand local-root or rooted-path work in this slice.
- Do not redesign broad E2E fixture APIs unless compile fallout forces a minimal update.
- Do not introduce a separate `FileName` type.

## Decision

Adopt `PathSegment` as the in-memory child-name type for all filetree entry models:

- `FileTreeEntry.Name : PathSegment`
- `FileEntry.Name : PathSegment`
- `DirectoryEntry.Name : PathSegment`
- `StagedDirectoryEntry.Name : PathSegment`

Directory-ness remains represented by the entry type, not by embedding `"/"` into `Name`.

Serializer simplification becomes the rule:

- file entries serialize as their segment text
- directory entries serialize as their segment text with the `D` marker carrying directory kind
- staged directory entries serialize and parse bare segment text with no trailing slash

## Architecture

### In-Memory Model

`FileTreeEntry` represents one child entry under a known parent directory. Because the parent path is already external to the entry model, `Name` should represent only one child segment.

That means the in-memory model should align with the path-domain types already adopted elsewhere:

- parent directory path: separate state outside the entry model
- child entry name: `PathSegment`
- child entry kind: `FileEntry` vs `DirectoryEntry`

This removes the current mismatch where directory entries carry serializer-shaped names such as `"docs/"` in memory even though the real child identity is just `docs`.

### Serializer Boundary

`FileTreeSerializer` should stop encoding directory identity through trailing-slash text.

Required behavior:

- `SerializePersistedFileEntryLine(...)` emits `entry.Name.ToString()`
- `SerializePersistedDirectoryEntryLine(...)` emits the directory segment text with no trailing slash
- persisted directory parsing accepts one canonical `PathSegment` name with no trailing slash
- staged directory parsing follows the same rule

The `F` / `D` type marker already carries file-vs-directory meaning, so the extra slash convention is redundant. Removing it makes both in-memory and on-disk representations align around the same child-name identity.

### Writer And Builder Ownership

`FileTreeStagingWriter` and `FileTreeBuilder` should operate on typed child names in memory.

For `FileTreeStagingWriter`:

- derive file name from `filePath.Name`
- derive directory names from `RelativePath` segment structure, not from stored slash-shaped directory names
- create `FileEntry` and staged directory lines using typed `PathSegment` values

For `FileTreeBuilder`:

- duplicate detection keys use typed child-name identity
- staged directory entries parse into typed names immediately
- recursive conversion from staged directory entries to final directory entries preserves typed `PathSegment` names throughout

Any directory slash formatting that still exists for user-facing display belongs in list/display code, not in filetree persistence.

## Scope

### Included

- `src/Arius.Core/Shared/FileTree/FileTreeModels.cs`
- `src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs`
- `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`
- `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`
- natural compile-fallout updates in filetree-adjacent callers and tests
- cleanup of helper code and tests that directly construct filetree entries with raw names

### Included Test Fallout

- filetree serializer tests
- filetree builder tests
- filetree staging/writer tests
- archive/filetree regression tests that assert filetree child names
- narrow shared test helpers that construct filetree entries directly

### Excluded

- unrelated CLI or Explorer behavior changes
- new public path abstractions beyond already-approved `PathSegment`
- broad test-fixture API redesign outside the natural fallout of typed filetree names

## File Guidance

Expected primary files:

- `src/Arius.Core/Shared/FileTree/FileTreeModels.cs`
- `src/Arius.Core/Shared/FileTree/FileTreeSerializer.cs`
- `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`
- `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`
- `src/Arius.Core.Tests/Shared/FileTree/FileTreeSerializerTests.cs`
- `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`

Likely cleanup candidates if compile fallout reaches them:

- `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveRecoveryTests.cs`
- `src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs`
- small helper code in `src/Arius.Tests.Shared/` or filetree-adjacent test fakes

## Persistence Format

Backward compatibility with existing archives is not required for this slice.

That means:

- persisted directory lines may change to bare segment names
- staged directory lines may change to bare segment names
- file entries remain unchanged on disk
- the in-memory and on-disk child-name representations can now align on the same bare `PathSegment` identity

Because we assume there are no existing archives to preserve, no migration or dual-format parsing requirement is needed.

## Testing Strategy

Coverage should prove:

- filetree entries hold `PathSegment` names in memory
- serializer round-trips still preserve canonical filetree behavior with the new bare-segment format
- directory slash formatting is absent from both the in-memory filetree model and persisted/staged filetree text
- staged directory parsing rejects non-canonical names and returns typed names for canonical ones
- filetree builder duplicate detection and deterministic hashing remain correct with typed names
- archive/filetree regression coverage still passes after the typed-name migration

The migration should follow TDD:

- add or update failing tests first
- verify they fail for the expected reason
- implement the smallest production change to make them pass
- rerun focused suites, then broader affected suites

## Success Criteria

This slice is complete when:

- `FileTreeEntry.Name` is `PathSegment`
- no in-memory filetree model uses trailing slash text as directory identity
- `FileTreeSerializer` no longer encodes directory identity through trailing slash formatting
- `FileTreeStagingWriter` and `FileTreeBuilder` operate on typed child names internally
- persisted and staged filetree text no longer depend on trailing slash to identify directories
- focused filetree and affected regression tests pass

## Out Of Scope Follow-Up

After this slice, any remaining test-fixture or dataset helper cleanup that is larger than natural compile fallout belongs to the original slice 5 follow-up rather than to this filetree model change.
