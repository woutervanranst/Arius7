---
status: "accepted"
date: 2026-05-06
decision-makers: "Wouter Van Ranst"
consulted: "OpenCode"
---

# Introduce internal filesystem domain types in Arius.Core

## Context and Problem Statement

Arius.Core currently uses raw strings for several different path concepts: archive-relative binary file paths, pointer file paths, filetree entry names, local filesystem paths, blob virtual names, and local cache paths. The result is repeated separator normalization, pointer suffix manipulation, root stripping/re-rooting, and direct `File.*`, `Directory.*`, and `Path.*` usage spread through archive, list, restore, filetree, storage, and cache code.

Arius archives should be portable across Windows, macOS, and Linux. That makes path handling part of the domain model, not just implementation plumbing: archive paths need one canonical representation, path prefix checks need to be segment-aware, and case-only collisions that are valid on Linux but unsafe on Windows must be rejected before publishing repository state.

The earlier `feat/path-helpers` branch showed that a typed path model improves correctness and developer experience, especially with a `RelativePath` type and `/` path composition operator. It also exposed the wrong boundary: typed path values leaked into public contracts and a broad filesystem wrapper started to look like a Zio-style filesystem abstraction. This decision keeps the useful internal domain model while avoiding public API ripple and over-broad abstraction.

## Decision Drivers

* Arius.Core should make invalid archive paths unrepresentable or fail-fast close to the boundary.
* Archive/list/restore/filetree code should operate on domain-relative paths, not host full-path strings.
* Public command/query/result/event contracts should remain string-based unless explicitly changed later.
* Developer experience should stay lightweight; path construction in tests and focused Core code should not require verbose `PathSegment.Parse("...")` chains.
* Arius archives should remain cross-OS restorable, even when a source filesystem allows names another target OS cannot represent safely.
* Direct local filesystem APIs should be quarantined so they do not incentivize new stringly path code.
* The solution should be smaller than a virtual filesystem library and should not require StronglyTypedId, Vogen, Zio, or code generation unless a later implementation need proves otherwise.

## Considered Options

* Keep raw strings with shared helper methods.
* Introduce a full semantic path taxonomy, such as separate repository, blob, cache, and local rooted path types.
* Adopt or emulate a virtual filesystem abstraction such as Zio.
* Introduce a small internal filesystem domain model centered on `RelativePath`, archive-time file records, and a concrete rooted filesystem boundary.

## Decision Outcome

Chosen option: "Introduce a small internal filesystem domain model centered on `RelativePath`, archive-time file records, and a concrete rooted filesystem boundary", because it captures Arius path semantics inside Core without exposing new types externally or building a broad filesystem abstraction.

The core domain path type will be an internal `RelativePath` in `Arius.Core.Shared.FileSystem`. It represents a canonical relative path using `/` separators. It rejects rooted paths, empty non-root paths, empty segments, `.` and `..`, backslashes, control characters, and malformed separators. `RelativePath.Root` represents the empty root path. Prefix operations must be segment-aware so `photos` does not match `photoshop`.

`RelativePath` will preserve the developer experience from the earlier branch:

```csharp
var path = RelativePath.Root / "photos" / "2024" / "pic.jpg";
```

The `/ string` operator appends exactly one validated segment. It is not an implicit conversion and it does not parse multi-segment strings: `RelativePath.Root / "photos/pic.jpg"` must throw.

`PathSegment` remains internal and owns single-segment validation, but most callers should not need to use it directly. C# 14 extension members may be used for cheap derived facts, such as pointer path helpers or segment extension lookup. They must not hide IO or expensive operations.

Pointer-file naming will be centralized near `RelativePath`. The `.pointer.arius` suffix must not be scattered through handlers. Core should expose helpers such as:

```csharp
path.IsPointerPath
path.ToPointerPath()
path.ToBinaryPath()
```

Archive-time local file state will be modeled with internal `BinaryFile`, `PointerFile`, and `FilePair` records. These are archive-time objects only and must not carry host full-path strings.

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

`FilePair.Path` is always the binary path, including pointer-only files. A pair may be binary-only, pointer-only, or contain both.

Restore-time files must not reuse `BinaryFile`, `PointerFile`, or `FilePair`. Restore should use its own restore candidate model based on `RelativePath`, `ContentHash`, timestamps, and chunk metadata. Restore can still use centralized pointer path derivation when writing pointer files.

Local filesystem access will be quarantined behind a concrete internal `RelativeFileSystem` rooted at a `LocalDirectory`. This avoids the confusing `LocalRootPath`/`RootedPath` model from the earlier branch and avoids introducing an `ILocalFileSystem` interface until there is a real need for substitution.

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

`LocalDirectory` is a typed root token, not a domain path. `RelativeFileSystem` converts `LocalDirectory + RelativePath` to host paths, enforces containment under the root, and is the only place that should use direct `System.IO` APIs for Arius local filesystem domain work.

`RelativePath` will also be used internally for slash-normalized logical paths such as blob virtual names and cache-relative paths where it improves correctness. Public storage interfaces and SDK boundaries may remain string-based, converting at the boundary.

Public Arius.Core command/query/result/event contracts remain string-based. Handlers parse incoming strings into `LocalDirectory` and `RelativePath` near the start of an operation and convert internal paths back to strings when publishing events or returning result DTOs.

### Consequences

* Good, because archive, list, restore, filetree, blob, and cache code can share one canonical relative path model instead of repeating string normalization.
* Good, because public contracts do not ripple through CLI, UI, tests, or external consumers.
* Good, because `RelativeFileSystem` makes direct host filesystem calls visible and centralized without committing Arius to a virtual filesystem abstraction.
* Good, because `RelativePath.Root / "photos" / "pic.jpg"` gives tests and focused Core code readable path construction while still validating every segment.
* Good, because case-insensitive collision checks make unsafe cross-OS archives fail before snapshot publication.
* Bad, because one generic `RelativePath` can still mix repository paths and blob/cache paths. If real mistakes appear, add semantic wrappers later rather than front-loading ceremony.
* Bad, because the refactor is broad and will touch archive, restore, list, filetree, storage helper, cache helper, and tests.
* Bad, because Linux-only repositories with case-only path differences will be rejected. This is deliberate because Arius archives are intended to be portable.

### Confirmation

Implementation must be confirmed by tests and code review:

* Unit tests for `RelativePath` parsing, root handling, segment-aware prefix behavior, `/ string` composition, invalid segment rejection, and pointer path derivation.
* Tests for `BinaryFile`, `PointerFile`, and `FilePair` enumeration cases: binary-only, pointer-only, binary plus pointer, invalid pointer content, inaccessible files, and streaming/no-materialization behavior.
* Tests proving `RelativeFileSystem` strips the configured root, prevents root escape, and centralizes local filesystem operations behind `RelativePath` arguments.
* Archive tests proving case-insensitive path collisions fail before snapshot publication.
* List and restore tests proving prefix traversal is segment-aware and public path outputs remain strings.
* A code sweep of `src/Arius.Core` for path-like raw strings and direct `File.*`, `Directory.*`, and `Path.*` usage outside the filesystem boundary, with intentional exceptions documented.

## Pros and Cons of the Options

### Keep raw strings with shared helper methods

* Good, because it is the smallest immediate change.
* Good, because it avoids new value objects and refactor scope.
* Bad, because invalid path values can still travel through Core as ordinary strings.
* Bad, because string prefix checks, separator normalization, pointer suffix handling, and root containment remain discipline problems rather than type-level boundaries.
* Bad, because direct `System.IO` usage remains easy to add anywhere.

### Full semantic path taxonomy

Examples include separate `RepositoryPath`, `BlobPath`, `CachePath`, `LocalRootPath`, and `RootedPath` types.

* Good, because it gives stronger compile-time separation between path worlds.
* Good, because repository paths cannot accidentally be passed where blob paths are expected.
* Bad, because it adds many names and conversions before Arius has evidence that those mixups are common.
* Bad, because the previous branch showed that many path types can ripple widely and make everyday code harder to read.
* Bad, because `LocalRootPath` and `RootedPath` were mechanically useful but confusing as central concepts.

### Adopt or emulate a virtual filesystem abstraction

Examples include using Zio or building an `ILocalFileSystem`-style interface with broad filesystem operations.

* Good, because it isolates all filesystem behavior and can make tests highly controllable.
* Good, because it can model path spaces explicitly.
* Bad, because Arius does not currently need a replaceable virtual filesystem.
* Bad, because a broad API would hide IO costs and make the abstraction larger than the problem.
* Bad, because the earlier Zio-inspired approach felt too drastic for the current codebase.

### Small internal filesystem domain model

This is the chosen option.

* Good, because it strengthens Core path correctness without changing public contracts.
* Good, because it keeps `RelativePath` as the domain coordinate system: archive strips the root, filetrees are built from relative paths, list traverses relative paths, and restore adds a root back only at the filesystem boundary.
* Good, because it removes scattered `System.IO` usage without pretending filesystem behavior belongs on domain value objects.
* Good, because it preserves the useful parts of the previous branch while avoiding its public API and abstraction-size problems.
* Bad, because it still requires a substantial refactor and careful staged implementation.

## More Information

This ADR is reflected in the OpenSpec change `introduce-core-filesystem-domain-types` under `openspec/changes/introduce-core-filesystem-domain-types/`. The OpenSpec proposal, design, specs, and tasks describe the planned implementation sequence, but this ADR is intended to stand on its own as the architectural decision.
