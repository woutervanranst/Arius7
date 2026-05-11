## Context

Arius.Core uses raw strings for several different path concepts: archive-relative file paths, pointer-file paths, filetree entry names, local filesystem paths, blob virtual names, and local cache paths. The most visible pain is in archive/list/restore flows, where `File.*`, `Directory.*`, `Path.*`, separator normalization, pointer suffix handling, and root stripping/re-rooting are repeated in feature handlers and shared services.

The earlier `feat/path-helpers` branch proved that `RelativePath`, `PathSegment`, `LocalRootPath`, `RootedPath`, and typed `FilePair` concepts improve correctness and developer experience, especially the `/` path composition operator. The current implementation keeps the useful domain model, allows `RelativePath` and `PathSegment` to be public, and avoids a Zio-style filesystem abstraction.

## Goals / Non-Goals

**Goals:**

- Make `RelativePath` the main slash-normalized domain coordinate system for Arius archive paths, list traversal, restore candidates, filetree staging, blob virtual names, and logical cache paths.
- Allow public Arius.Core contracts to expose `RelativePath` and `PathSegment` where that fits the implemented API.
- Introduce `BinaryFile`, `PointerFile`, and `FilePair` for archive-time local file state only.
- Use a concrete `RelativeFileSystem` rooted at a `LocalDirectory` to quarantine host filesystem string paths and direct `System.IO` usage.
- Preserve the pleasant path composition developer experience with `RelativePath.Root / "photos" / "pic.jpg"` while validating each appended segment.
- Refactor path-like strings broadly across Arius.Core, including blob names and cache paths, when those strings represent slash-normalized logical paths.

**Non-Goals:**

- Do not introduce `RootedPath` or `LocalRootPath` as central domain types.
- Do not add a third-party strongly-typed-id/codegen dependency unless implementation proves hand-written value objects are insufficient.
- Do not build a replaceable virtual filesystem or broad `ILocalFileSystem` abstraction up front.
- Do not reuse archive-time `BinaryFile`/`PointerFile`/`FilePair` as restore-time models.

## Decisions

### `RelativePath` is the generic slash-normalized path primitive

`RelativePath` represents a canonical relative path using `/` separators. It rejects rooted paths, empty non-root paths, empty segments, `.` and `..`, backslashes, control characters, and malformed separators. `RelativePath.Root` represents the empty root path.

It has `Name`, `Parent`, `Segments`, `StartsWith`, `Parse`, `TryParse`, and `FromPlatformRelativePath`. Segment-aware prefix checks must be implemented on `RelativePath` rather than raw string prefixes so `photos` does not accidentally match `photoshop`.

Path composition supports both typed and string segment appends:

```csharp
var path = RelativePath.Root / "photos" / "2024" / "pic.jpg";
```

The `/ string` operator validates the string as exactly one `PathSegment`; it does not parse multi-segment strings. `RelativePath.Root / "photos/pic.jpg"` throws.

Alternatives considered:

- Separate `RepositoryPath`, `BlobPath`, and `CachePath` types. This gives stronger semantic separation, but it adds ceremony across the whole codebase and risks overengineering. Start with one slash-normalized primitive and add semantic wrappers only where actual mixups are found.
- Keep using strings with helper methods. This is simpler initially but does not stop invalid paths from flowing through Core.

### `PathSegment` is public and lightweight

`PathSegment` owns single-segment validation and string rendering. Production code can use it when a filetree entry name or directory name is explicitly a segment, but most call sites should use `RelativePath / "segment"` for readability.

C# 14 extension members can be used for cheap derived facts such as pointer-path helpers or segment extension lookup. They must not hide IO or expensive operations.

### Pointer path behavior belongs near `RelativePath`

Pointer-file naming is a path convention, not a responsibility of every feature handler. The suffix `.pointer.arius` should live in one helper, exposed as extension members or explicit static helpers:

```csharp
path.IsPointerPath
path.ToPointerPath()
path.ToBinaryPath()
```

`ToPointerPath` appends the pointer suffix to the final binary path. `ToBinaryPath` requires that the current path is a pointer path and returns the corresponding binary path.

### Archive-time file state uses `BinaryFile`, `PointerFile`, and `FilePair`

`BinaryFile`, `PointerFile`, and `FilePair` are internal archive-time models. They do not carry full OS paths.

```csharp
internal sealed record BinaryFile
{
    public required RelativePath Path { get; init; }
    public required long Size { get; init; }
    public required DateTimeOffset Created { get; init; }
    public required DateTimeOffset Modified { get; init; }
}

internal sealed record PointerFile
{
    public required RelativePath Path { get; init; }
    public required RelativePath BinaryPath { get; init; }
    public ContentHash? Hash { get; init; }
}

internal sealed record FilePair
{
    public required RelativePath Path { get; init; }
    public BinaryFile? Binary { get; init; }
    public PointerFile? Pointer { get; init; }
}
```

`FilePair.Path` is always the binary path, including pointer-only files. A `FilePair` may be binary-only, pointer-only, or contain both.

Restore-time should use a separate restore candidate model, such as `FileToRestore`, based on `RelativePath`, `ContentHash`, timestamps, and chunk metadata. Restore can use pointer path helpers when it writes pointer files, but a file being restored is not an archive-time `FilePair`.

### Local filesystem access is quarantined in `RelativeFileSystem`

Feature handlers and domain services should not call `File.*`, `Directory.*`, or `Path.*` directly for local archive/restore/list filesystem work. `Arius.Core.Shared.FileSystem` is the only namespace where those APIs should appear for Arius path-domain work. A concrete `RelativeFileSystem` rooted at a `LocalDirectory` provides the needed operations using `RelativePath` arguments:

```csharp
sealed class RelativeFileSystem
{
    public RelativeFileSystem(LocalDirectory root);

    public IEnumerable<LocalFileEntry> EnumerateFiles();
    public bool FileExists(RelativePath path);
    public bool DirectoryExists(RelativePath path);
    public Stream OpenRead(RelativePath path);
    public Stream CreateFile(RelativePath path);
    public string ReadAllText(RelativePath path);
    public Task<string> ReadAllTextAsync(RelativePath path, CancellationToken cancellationToken);
    public Task WriteAllTextAsync(RelativePath path, string content, CancellationToken cancellationToken);
    public void CreateDirectory(RelativePath path);
    public void DeleteFile(RelativePath path);
}
```

`LocalDirectory` is a typed root token, not a domain path. It parses and normalizes an absolute local directory root. `RelativeFileSystem` converts `LocalDirectory + RelativePath` to host paths, enforces root containment, and `Arius.Core.Shared.FileSystem` is the only place that should use direct `System.IO` for local filesystem work.

No interface is introduced initially. If tests or alternate filesystem implementations later require substitution, extract an interface from the concrete class at that point.

### Public contracts may expose validated path types

Public command/query/result/event contracts may expose `RelativePath` and `PathSegment` directly when that improves the API. Handlers still parse incoming string roots and other host-path inputs into `LocalDirectory` and `RelativePath` near the start of the operation.

### Blob and cache path strings use `RelativePath` where they are slash-normalized logical paths

Blob names and prefixes such as `chunks/<hash>`, `filetrees/<hash>`, `snapshots/<name>`, and `chunk-index/<prefix>` are slash-normalized logical paths. Internal helper methods should return `RelativePath` or consume it where useful, converting to strings at `IBlobContainerService` and Azure SDK boundaries.

Local cache directories use `LocalDirectory` and `RelativeFileSystem` where they interact with disk. Hash-derived cache filenames can be represented as `RelativePath` segments when built under a known cache root.

## Risks / Trade-offs

- Refactor scope is broad → Implement in phases with tests at each boundary while keeping public API changes deliberate.
- `RelativePath / string` could look like unchecked string usage → The operator only accepts one validated segment and tests must prove multi-segment strings throw.
- One generic `RelativePath` can still mix repository and blob concepts → Start simple, then add semantic wrappers only if real mistakes appear.
- Removing direct `System.IO` calls from feature code may centralize too much in `RelativeFileSystem` → Keep it concrete and limited to operations Arius already needs; do not add broad virtual filesystem APIs speculatively.
- Public path types in contracts increase API coupling to Arius.Core path semantics → Accepted because the current implementation already uses `RelativePath` directly in several public contracts.
- Existing repository/cache format compatibility is not preserved → Accepted for this change; tests should assert the new canonical behavior instead of preserving old hashes or layout.

## Migration Plan

1. Add `Arius.Core.Shared.FileSystem` types and focused unit tests.
2. Refactor local file enumeration to use `RelativeFileSystem` and archive-time file models.
3. Refactor archive internals and filetree staging to consume `RelativePath`.
4. Refactor list and restore traversal/merge/materialization internals to use `RelativePath` and `RelativeFileSystem`.
5. Refactor blob-name and cache-path helpers where they represent slash-normalized logical paths.
6. Sweep Arius.Core for remaining path-like raw strings and direct `File.*` / `Directory.*` / `Path.*` usage outside the filesystem boundary.

## Open Questions

- The exact restore candidate type name and shape (`FileToRestore`, `RestoreFile`, or similar) should be chosen during implementation based on current restore code.

## Notes From Implementation

- `RelativePath` and `PathSegment` are allowed to be public.
- Case-insensitive path-collision rejection is intentionally not part of this change.
- Tar assembly is allowed to use in-memory buffering when that matches the implementation and runtime goals.
- `FileTreeService` and `SnapshotService` details are not part of this change's core filesystem-domain design.
