# Production Typed IO Cleanup Design

## Context

The main typed-path migration is largely complete, but a small production slice still crosses back into raw `File.*` calls and ad hoc string path composition in core workflows:

- `FileTreeBuilder` still reads staged node lines with `File.ReadLinesAsync(path.FullPath, ...)`
- `FileTreeStagingWriter` still appends staged lines with `File.AppendAllLinesAsync(path.FullPath, ...)`
- `ArchiveCommandHandler` still opens and deletes tar temp files through raw string paths
- archive and restore pointer-file creation still reparses `RelativePath` values by concatenating `".pointer.arius"` onto strings

These remaining spots weaken the path model in the exact production surfaces where the repository is trying to stay strongly typed and avoid raw filesystem APIs outside the typed wrappers.

## Goals

- Remove the remaining production raw line-based file IO from `FileTreeBuilder` and `FileTreeStagingWriter`.
- Remove ad hoc string-based pointer-file path composition in archive and restore.
- Keep tar temp-file handling in `ArchiveCommandHandler` on typed paths for open/read/delete operations once the temp file has been created.
- Extend the typed filesystem surface only where production still lacks required capabilities.

## Non-Goals

- Do not eliminate all `File.*` and `Directory.*` calls from tests, benchmarks, or true host-boundary utilities in this slice.
- Do not introduce production string convenience overloads on `LocalRootPath`.
- Do not redesign tar staging, archive routing, or restore behavior.
- Do not refactor unrelated path parsing that already occurs at valid boundaries such as CLI/settings input or OS directory enumeration.

## Design

### 1. Add the missing typed line-based file APIs on `RootedPath`

`RootedPathFileSystemExtensions` already owns the typed filesystem boundary for file and directory operations. The missing gap is line-oriented text IO used by the staged filetree pipeline.

Add the smallest surface needed there:

- `IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken = default)`
- `Task WriteAllLinesAsync(IEnumerable<string> lines, CancellationToken cancellationToken = default)`
- `Task AppendAllLinesAsync(IEnumerable<string> lines, CancellationToken cancellationToken = default)`

These methods should delegate to the corresponding BCL APIs using `path.FullPath`, just like the existing typed methods already do for bytes, text, open-read, open-write, and directory enumeration.

Why here:

- this is the existing typed IO boundary
- it removes raw `File.*` calls from production consumers without introducing new helper layers
- tests can reuse the same typed APIs instead of reading `.FullPath` just to call BCL methods

### 2. Add an explicit typed helper for pointer-file derivation on `RelativePath`

Archive and restore both currently derive pointer-file paths by concatenating `".pointer.arius"` onto a repository-relative path string and reparsing it.

Add one typed helper on `RelativePath`, for example:

```csharp
public RelativePath ToPointerFilePath()
```

Behavior:

- append the pointer suffix to the current path text
- return a new canonical `RelativePath`
- keep all validation inside the existing `RelativePath` model

Why this is preferred over a feature-local helper:

- pointer files are a cross-feature repository concept
- the helper removes repeated string reparsing from archive and restore
- it keeps the derivation rule on the owning path type instead of duplicating it in handlers

This helper should stay narrow. It is specifically for the repository pointer-file suffix rule, not a generic string suffix API.

### 3. Refactor production consumers to stay on typed APIs

Use the new typed APIs in the remaining production hotspots:

- `FileTreeBuilder`
  - replace `File.ReadLinesAsync(path.FullPath, ...)` with `path.ReadLinesAsync(...)`
- `FileTreeStagingWriter`
  - replace `File.AppendAllLinesAsync(path.FullPath, ...)` with `path.AppendAllLinesAsync(...)`
- `ArchiveCommandHandler`
  - keep temp tar open/read/delete operations on typed rooted paths after temp file creation
  - replace pointer-file construction with `relativePath.ToPointerFilePath()` rooted at `opts.RootDirectory`
- `RestoreCommandHandler`
  - replace pointer-file construction with `file.RelativePath.ToPointerFilePath().RootedAt(opts.RootDirectory)`

The temp tar-file creation boundary may still start from `Path.GetTempFileName()` because that is a host filesystem API returning a new OS path. That is acceptable. The cleanup here is to parse it once into a typed rooted path and then stay typed afterward.

### 4. Update focused tests to drive and verify the new surface

Add or adjust tests in these areas:

- `RootedPathTests`
  - cover the new line-based typed filesystem APIs
- `RelativePathTests`
  - cover pointer-file derivation behavior
- `FileTreeBuilderTests`
  - stop using raw `File.WriteAllLinesAsync(...FullPath, ...)` where a typed API can now express the same setup
- `FileTreeStagingWriterTests`
  - stop using raw `File.ReadAllLinesAsync(...FullPath)` where a typed API can now express the same assertions
- archive/restore tests only if needed to directly pin the pointer-file helper behavior through production flows

## File Map

- `src/Arius.Core/Shared/Paths/RootedPathFileSystemExtensions.cs`: add typed line-based file APIs
- `src/Arius.Core/Shared/Paths/RelativePath.cs`: add typed pointer-file derivation helper
- `src/Arius.Core/Shared/FileTree/FileTreeBuilder.cs`: switch staged node reading to typed line IO
- `src/Arius.Core/Shared/FileTree/FileTreeStagingWriter.cs`: switch staged node appends to typed line IO
- `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`: switch tar temp-file open/delete and pointer-file composition to typed paths
- `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs`: switch pointer-file composition to typed paths
- `src/Arius.Core.Tests/Shared/RootedPathTests.cs`: add typed line IO tests
- `src/Arius.Core.Tests/Shared/RelativePathTests.cs`: add pointer-file derivation tests
- `src/Arius.Core.Tests/Shared/FileTree/FileTreeBuilderTests.cs`: update test setup to use typed line IO
- `src/Arius.Core.Tests/Shared/FileTree/FileTreeStagingWriterTests.cs`: update assertions to use typed line IO

## Verification

Focused verification:

- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RootedPathTests/*"`
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RelativePathTests/*"`
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeBuilderTests/*"`
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/FileTreeStagingWriterTests/*"`
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/ArchiveRecoveryTests/*"`
- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj" --treenode-filter "/*/*/RestoreCommandHandlerTests/*"`

Broader verification after implementation:

- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj"`
- `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj"`
- `dotnet test --project "src/Arius.Cli.Tests/Arius.Cli.Tests.csproj"`
- `dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --no-restore`
- `slopwatch analyze`

## Self-Review

- Scope check: this slice stays limited to production typed IO gaps and the tests required to drive them.
- Placeholder scan: all target files, API shapes, and verification commands are concrete.
- Boundary check: host-created temp-file names remain valid boundaries, while all subsequent filesystem actions stay on typed paths.
