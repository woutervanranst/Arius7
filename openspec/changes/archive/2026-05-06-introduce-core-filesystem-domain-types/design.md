## Context

Arius.Core uses raw strings for several different path concepts: archive-relative file paths, pointer-file paths, filetree entry names, local filesystem paths, blob virtual names, and local cache paths. The most visible pain is in archive/list/restore flows, where `File.*`, `Directory.*`, `Path.*`, separator normalization, pointer suffix handling, and root stripping/re-rooting are repeated in feature handlers and shared services.

The earlier `feat/path-helpers` branch proved that `RelativePath`, `PathSegment`, `LocalRootPath`, `RootedPath`, and typed `FilePair` concepts improve correctness and developer experience, especially the `/` path composition operator. It also exposed too many typed path objects through public contracts and introduced a broad filesystem wrapper surface. This design keeps the useful domain model, makes it internal, and avoids a Zio-style filesystem abstraction.

## Goals / Non-Goals

**Goals:**

- Make `RelativePath` the internal domain coordinate system for Arius archive paths, list traversal, restore candidates, filetree staging, blob virtual names, and logical cache paths.
- Keep public Arius.Core command/query/result/event contracts string-based unless a future change explicitly changes them.
- Introduce `BinaryFile`, `PointerFile`, and `FilePair` for archive-time local file state only.
- Use a concrete `RelativeFileSystem` rooted at a `LocalDirectory` to quarantine host filesystem string paths and direct `System.IO` usage.
- Preserve the pleasant path composition developer experience with `RelativePath.Root / "photos" / "pic.jpg"` while validating each appended segment.
- Reject archive paths that collide under case-insensitive comparison before publishing repository state, so archives remain cross-OS restorable.
- Refactor path-like strings broadly across Arius.Core, including blob names and cache paths, when those strings represent slash-normalized logical paths.

**Non-Goals:**

- Do not introduce `RootedPath` or `LocalRootPath` as central domain types.
- Do not expose filesystem domain types from public command/query/result contracts.
- Do not add a third-party strongly-typed-id/codegen dependency unless implementation proves hand-written value objects are insufficient.
- Do not build a replaceable virtual filesystem or broad `ILocalFileSystem` abstraction up front.
- Do not reuse archive-time `BinaryFile`/`PointerFile`/`FilePair` as restore-time models.

## Decisions

### `RelativePath` is the generic internal slash-normalized path primitive

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

### `PathSegment` stays internal and mostly invisible

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

Feature handlers and domain services should not call `File.*`, `Directory.*`, or `Path.*` directly for local archive/restore/list filesystem work. A concrete internal `RelativeFileSystem` rooted at a `LocalDirectory` provides the needed operations using `RelativePath` arguments:

```csharp
internal sealed class RelativeFileSystem
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

`LocalDirectory` is a typed root token, not a domain path. It parses and normalizes an absolute local directory root. `RelativeFileSystem` converts `LocalDirectory + RelativePath` to host paths, enforces root containment, and is the only place that should use direct `System.IO` for local filesystem work.

No interface is introduced initially. If tests or alternate filesystem implementations later require substitution, extract an interface from the concrete class at that point.

### Cross-OS path collisions fail before snapshot publication

`RelativePath` equality remains ordinal and case-preserving. Linux can enumerate both `photos/pic.jpg` and `Photos/pic.jpg`, but Arius must reject such input before publishing a snapshot because it cannot restore both paths safely on Windows or common macOS configurations.

Collision checks should use ordinal case-insensitive comparison over complete `RelativePath` values and over sibling filetree entry names where appropriate. The failure should identify the colliding paths and stop the archive before snapshot creation.

### Public contracts convert at the boundary

Public command/query/result contracts remain strings. Handlers parse incoming strings into `LocalDirectory` and `RelativePath` near the start of the operation and convert internal paths back to strings when publishing public events or returning public result DTOs.

This avoids the previous branch's public API ripple while still removing internal stringly path behavior.

### Blob and cache path strings use `RelativePath` where they are slash-normalized logical paths

Blob names and prefixes such as `chunks/<hash>`, `filetrees/<hash>`, `snapshots/<name>`, and `chunk-index/<prefix>` are slash-normalized logical paths. Internal helper methods should return `RelativePath` or consume it where useful, converting to strings at `IBlobContainerService` and Azure SDK boundaries.

Local cache directories use `LocalDirectory` and `RelativeFileSystem` where they interact with disk. Hash-derived cache filenames can be represented as `RelativePath` segments when built under a known cache root.

## Risks / Trade-offs

- Refactor scope is broad → Implement in phases with tests at each boundary and keep public contracts string-based to contain ripple.
- `RelativePath / string` could look like unchecked string usage → The operator only accepts one validated segment and tests must prove multi-segment strings throw.
- One generic `RelativePath` can still mix repository and blob concepts → Start simple, then add semantic wrappers only if real mistakes appear.
- Removing direct `System.IO` calls from feature code may centralize too much in `RelativeFileSystem` → Keep it concrete and limited to operations Arius already needs; do not add broad virtual filesystem APIs speculatively.
- Case-collision detection can reject valid Linux-only file trees → This is deliberate because Arius archives are intended to be cross-OS compatible and restorable.
- Existing repository/cache format compatibility is not preserved → Accepted for this change; tests should assert the new canonical behavior instead of preserving old hashes or layout.

## Migration Plan

1. Add `Arius.Core.Shared.FileSystem` types and focused unit tests.
2. Refactor local file enumeration to use `RelativeFileSystem` and archive-time file models.
3. Refactor archive internals and filetree staging to consume `RelativePath`.
4. Refactor list and restore traversal/merge/materialization internals to use `RelativePath` and `RelativeFileSystem`.
5. Refactor blob-name and cache-path helpers where they represent slash-normalized logical paths.
6. Sweep Arius.Core for remaining path-like raw strings and direct `File.*` / `Directory.*` / `Path.*` usage outside the filesystem boundary.

## Open Questions

- Whether every blob/cache helper should return `RelativePath` immediately, or whether some storage-boundary helpers should remain string-based until their callers are refactored.
- The exact restore candidate type name and shape (`FileToRestore`, `RestoreFile`, or similar) should be chosen during implementation based on current restore code.
