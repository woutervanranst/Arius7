## Why

Arius.Core currently represents archive-relative paths, pointer paths, local filesystem paths, blob names, and cache paths with raw strings, which spreads normalization and `System.IO` usage across feature handlers and shared services. This makes cross-OS archive behavior harder to reason about and lets invalid or ambiguously-cased paths travel too far before failing.

## What Changes

- Introduce internal filesystem domain types under `Arius.Core.Shared.FileSystem`, centered on a generic slash-normalized `RelativePath` value object.
- Add archive-time file model types: `BinaryFile`, `PointerFile`, and `FilePair`.
- Add a small `RelativeFileSystem` boundary rooted at a local directory so feature code no longer calls `File.*`, `Directory.*`, or `Path.*` directly.
- Refactor archive, list, restore, filetree, blob-name, and cache-path internals away from path-like raw strings while keeping public command/query/result contracts string-based.
- Detect case-insensitive `RelativePath` collisions before publishing repository state so archives remain restorable on Windows, macOS, and Linux.
- Treat restore-time files as `RelativePath`-based restore candidates rather than reusing archive-time `BinaryFile`/`PointerFile`/`FilePair` objects.

## Capabilities

### New Capabilities

- `filesystem-domain-types`: Internal path and file-domain type system for Arius.Core, including `RelativePath`, pointer path derivation, archive-time file pairs, local filesystem quarantine, and cross-OS path collision handling.

### Modified Capabilities

- `archive-pipeline`: Archive enumeration and filetree staging operate on validated relative domain paths and reject case-insensitive path collisions before snapshot publication.
- `restore-pipeline`: Restore resolves snapshot entries through relative domain paths and uses the filesystem boundary to materialize files and pointer files under the requested root.
- `list-query`: Listing traversal and local/cloud merge use relative domain paths internally while preserving string output contracts.
- `file-tree-service`: Filetree staging and traversal consume validated relative paths and filetree entry names rather than ad hoc string path manipulation.

## Impact

- Affected code: `src/Arius.Core/Shared/FileSystem/`, `Features/ArchiveCommand`, `Features/RestoreCommand`, `Features/ListQuery`, `Shared/FileTree`, `Shared/ChunkStorage`, `Shared/ChunkIndex`, `Shared/Snapshot`, `Shared/Storage`, and related tests.
- Public contracts in Arius.Core remain string-based unless a later explicit change says otherwise.
- Remote and local repository formats do not need backward compatibility for this change; the implementation may simplify persisted representations when that improves the domain model.
- No third-party strong-id/codegen dependency is required unless the design later identifies a clear advantage over small hand-written value objects.
