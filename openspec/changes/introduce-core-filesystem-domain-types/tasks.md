## 1. Core Filesystem Domain Types

- [x] 1.1 Add `Arius.Core.Shared.FileSystem.RelativePath` with root, parse/try-parse, platform normalization, segment enumeration, name, parent, and segment-aware prefix operations.
- [x] 1.2 Add `PathSegment` and `/` operators so `RelativePath.Root / "photos" / "pic.jpg"` works while multi-segment or unsafe string appends throw.
- [x] 1.3 Add centralized pointer path helpers for `.pointer.arius` detection, binary-to-pointer derivation, and pointer-to-binary derivation.
- [x] 1.4 Add unit tests for relative path validation, root handling, segment-aware prefix behavior, `/ string` developer experience, and pointer path derivation.

## 2. Local Filesystem Boundary

- [x] 2.1 Add `LocalDirectory` as an internal absolute local root token with normalization and root-containment support.
- [x] 2.2 Add concrete internal `RelativeFileSystem` rooted at `LocalDirectory`, including `EnumerateFiles()`, existence checks, stream open/create, pointer text read/write, directory creation, and file deletion operations needed by Arius.Core.
- [x] 2.3 Add tests proving `RelativeFileSystem` strips the root into `RelativePath`, prevents root escape, and performs filesystem operations without exposing full-path strings to callers.
- [ ] 2.4 Update Arius.Core local filesystem code paths to route direct `File.*`, `Directory.*`, and `Path.*` domain operations through `RelativeFileSystem`.

## 3. Archive-Time File Model

- [ ] 3.1 Add internal `BinaryFile`, `PointerFile`, and `FilePair` records with relative paths only and no host full-path fields.
- [ ] 3.2 Refactor local file enumeration to produce archive-time `FilePair` values using `RelativeFileSystem.EnumerateFiles()` and pointer path helpers.
- [ ] 3.3 Add tests for binary-only, pointer-only, binary+pointer, invalid pointer content, inaccessible file handling, and streaming/no-materialization enumeration behavior.
- [ ] 3.4 Add archive path collision validation that rejects ordinal case-insensitive relative path collisions before snapshot publication, with tests for Linux-only casing conflicts.

## 4. Archive And Filetree Refactor

- [ ] 4.1 Refactor `ArchiveCommandHandler` internals to consume `RelativePath`, `BinaryFile`, `PointerFile`, `FilePair`, and `RelativeFileSystem` while preserving public string contracts and event payload shapes.
- [ ] 4.2 Refactor file hashing, upload, pointer writing, local binary removal, and progress callbacks to use relative paths internally and convert to strings only at public/logging boundaries.
- [ ] 4.3 Refactor `FileTreeStagingWriter`, filetree entry construction, and traversal helpers to accept validated `RelativePath` and `PathSegment` values instead of ad hoc path strings.
- [ ] 4.4 Add/update archive and filetree tests for canonical path staging, invalid staged path rejection, case-collision failure, and filetree traversal by relative path composition.

## 5. List And Restore Refactor

- [ ] 5.1 Refactor `ListQueryHandler` to parse prefix/local path strings at the boundary, traverse with `RelativePath`, and perform local/cloud merge through `RelativeFileSystem` while returning existing string DTOs.
- [ ] 5.2 Add/update list tests for segment-aware prefix matching, local/cloud merge with relative paths, pointer presence reporting, and no-local-path behavior.
- [ ] 5.3 Refactor `RestoreCommandHandler` to parse target/root strings at the boundary and represent restore candidates with relative paths, content hashes, timestamps, and chunk metadata rather than archive-time file-pair objects.
- [ ] 5.4 Refactor restore conflict checks, streaming writes, directory creation, local hashing, pointer-file creation, and progress identifiers to use `RelativePath` and `RelativeFileSystem` internally.
- [ ] 5.5 Add/update restore tests for file restore, directory restore, segment-aware target traversal, pointer path creation, no-pointer restore, and streaming writes through the filesystem boundary.

## 6. Blob, Cache, And Storage Path Cleanup

- [ ] 6.1 Refactor blob path helper internals for chunks, rehydrated chunks, filetrees, snapshots, and chunk-index shards to use `RelativePath` where they represent slash-normalized logical paths, converting to strings at storage-interface boundaries.
- [ ] 6.2 Refactor repository cache path helpers to use `LocalDirectory`, `RelativePath`, and `RelativeFileSystem` where they interact with disk cache layout.
- [ ] 6.3 Add/update tests for blob-name rendering, cache path rendering, snapshot path rendering, and chunk-index shard path rendering.

## 7. Sweep And Verification

- [ ] 7.1 Sweep `src/Arius.Core` for remaining path-like raw strings and direct `File.*`, `Directory.*`, and `Path.*` usage outside the filesystem boundary; refactor or document intentional exceptions.
- [ ] 7.2 Run Arius.Core-focused unit tests and update any affected tests to assert behavior through public string contracts and internal relative path behavior where appropriate.
- [ ] 7.3 Run broader relevant test projects after the refactor (`Arius.Core.Tests`, `Arius.Cli.Tests`, `Arius.Architecture.Tests`, and integration tests if affected).
- [ ] 7.4 Run the .NET slopwatch quality gate after code changes and address any shortcuts or suppressed failures it reports.
