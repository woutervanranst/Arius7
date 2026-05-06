# Broad Typed Filesystem Sweep Design

## Context

The typed-path migration has already moved most production flows onto `LocalRootPath`, `RootedPath`, `RelativePath`, and typed filesystem extension members. What remains is no longer just one small production cleanup. There are still scattered direct uses of:

- `File.*`
- `Directory.*`
- `Path.*`
- `DirectoryInfo` / `FileInfo`
- host-path string reparsing after the code has already crossed into typed local filesystem modeling

These leftovers now appear across two categories:

1. production code that still drops back to raw filesystem APIs for local enumeration, temp-file handling, or atomic publish operations
2. non-production code such as tests, shared test infrastructure, E2E helpers, and benchmarks that still model local roots and files through raw strings even when the code is really exercising typed-path behavior

The user explicitly wants the broader sweep rather than a narrow production-only finish. That means the design should aggressively remove remaining raw local-filesystem usage outside the typed wrapper layers, while still preserving true boundary cases where strings remain correct.

## Goals

- Remove nearly all remaining direct local-filesystem API usage outside the typed wrapper/extension layers.
- Replace caller-side `DirectoryInfo` / `FileInfo` enumeration patterns with typed enumeration and typed metadata access where the code is modeling local filesystem state.
- Move tests, shared test infrastructure, E2E helpers, and benchmarks onto typed roots and typed filesystem helpers as early as practical.
- Keep the typed path/domain model as the default internal representation for local filesystem addresses.
- Finish the sweep without introducing broad backwards-compatibility overloads or speculative abstractions.

## Non-Goals

- Do not eliminate strings from true boundaries such as persisted settings, CLI/progress payloads, logs, raw blob names, or dataset declaration text.
- Do not redesign Explorer settings persistence or UI-facing models just to remove stored path strings.
- Do not rewrite storage/blob naming code that is not modeling local filesystem paths.
- Do not add new path kinds such as `FilePath` or `DirectoryPath`.
- Do not perform unrelated refactors or broad naming cleanup outside the typed-filesystem sweep.

## Boundary Rules

### Strings that should remain

The following remain legitimate string boundaries and should not be mechanically erased:

- persisted settings values such as Explorer repository settings
- CLI/progress/event payloads that render relative paths for output
- logs and exception messages
- dataset declaration text and synthetic repository definitions expressed as textual relative paths
- raw blob/storage names and other remote identifier strings
- host APIs that inherently return strings such as environment-folder lookup or temp-file creation, as long as the code parses once into a typed path and stays typed afterward

### Raw filesystem APIs that should remain

Direct `File.*`, `Directory.*`, `Path.*`, `DirectoryInfo`, and `FileInfo` usage is still valid in the typed wrapper layer itself, where those APIs are being encapsulated behind typed extension members or path value objects.

Primary acceptable locations:

- `src/Arius.Core/Shared/FileSystem/RootedPathExtensions.cs`
- `src/Arius.Core/Shared/FileSystem/LocalRootPathExtensions.cs`
- path value objects that necessarily bridge to host OS path rules

Outside those layers, callers should prefer typed APIs unless a concrete host-boundary exception is documented in this design.

## Design

### 1. Extend typed filesystem helpers only where repeated caller-side raw usage still exists

The sweep should not replace every raw call with a bespoke one-off helper. The standard for adding a typed helper is:

- the same raw BCL pattern appears in multiple callers, or
- a caller is forced to drop to `DirectoryInfo` / `FileInfo` only because the typed layer is missing one obvious operation

Expected additions are modest and directly driven by remaining call sites. Likely examples:

- typed directory enumeration that returns typed paths instead of `DirectoryInfo` / `FileInfo`
- typed metadata access that removes the need for callers to hang onto `FileInfo`
- a small typed helper for atomic file replacement/publish if `FileTreeService` cannot otherwise avoid `File.Replace` / `File.Move`
- a typed temp-file or temp-root helper only if it meaningfully removes repeated string reparsing

The design principle is to add the smallest typed surface that eliminates repeated caller-side raw filesystem escape hatches.

### 2. Finish remaining production callers

The production cleanup should aggressively remove the remaining typed-path leaks in local filesystem workflows.

#### `FilePairEnumerator`

`FilePairEnumerator` still performs depth-first walking through `DirectoryInfo` and returns typed paths only after the raw enumeration has already happened.

The cleanup rule is:

- keep the public surface typed with `LocalRootPath` / `RootedPath` / `RelativePath`
- move the directory/file walking onto typed filesystem helpers as far as practical
- stop using `DirectoryInfo` / `FileInfo` directly in the enumerator if typed helpers can express the same traversal and metadata access

This is a real remaining production gap because the type model already knows the root and relative path while the implementation still falls back to raw directory objects.

#### `ListQueryHandler`

`ListQueryHandler` still builds local directory snapshots using `DirectoryInfo(...).EnumerateDirectories()` and `.EnumerateFiles()`.

The cleanup rule is:

- keep local directory discovery and file discovery typed
- derive local directory/file state from typed entries rather than raw directory/file info objects where possible
- continue to preserve current behavior for pointer-file suppression, metadata reads, logging, and ordering

#### `ArchiveCommandHandler`

`ArchiveCommandHandler` still crosses through `Path.GetTempFileName()`, then decomposes the resulting OS path back into directory/file-name string pieces before rebuilding a typed path.

The cleanup rule is:

- keep host temp-file creation as an allowed boundary if needed
- parse it once into the appropriate typed path form
- avoid repeated string decomposition/recomposition afterward

If a small typed helper can simplify temp-file handling without spreading new abstractions, prefer that.

#### `FileTreeService`

`FileTreeService` still uses raw `File.Replace(...)` / `File.Move(...)` for atomic cache publication.

This is the one production area where a raw call may still be justified because atomic replace is a very specific filesystem boundary. The decision rule is:

- if a typed `ReplaceFile` / atomic publish helper can be added cleanly to the wrapper layer and remove the caller-side raw operations, do that
- otherwise, allow this single raw boundary to remain, but keep it localized and explicitly justified as wrapper-adjacent atomic publish logic rather than normal caller IO

The sweep should still remove easier caller-side leftovers first and only keep this one if forcing a typed wrapper would be more ceremony than value.

### 3. Sweep non-production local-filesystem modeling onto typed roots and typed IO

The non-production cleanup includes:

- `src/Arius.Tests.Shared/`
- `src/Arius.Core.Tests/`
- `src/Arius.Integration.Tests/`
- `src/Arius.E2E.Tests/`
- `src/Arius.Benchmarks/`

Rule:

- when the code is modeling a temp root, restore root, repository root, cache root, file under test, or directory under test, use typed roots and typed paths as early as possible
- use typed extension members for existence checks, read/write, delete, copy, enumerate, and timestamps
- keep raw strings only when a test intentionally verifies rendered path text, textual dataset input, or external process/API contracts

This is broader than previous slices by design. The aim is not just to make production look clean while leaving all supporting code on raw BCL paths.

### 4. Preserve textual dataset definitions and other explicit string boundaries

The broad sweep is not permission to retcon every path-like string into a domain value at declaration time.

The following should stay textual until they cross a materialization or parsing boundary:

- synthetic repository definition paths
- mutation declarations
- benchmark option text coming from command-line or raw configuration surfaces
- string-based output/report paths that are fundamentally external artifacts until typed local modeling begins

This keeps the cleanup from becoming a mechanical “remove all strings” exercise that would fight the code’s actual boundaries.

### 5. Verification by grep and by behavior

This slice should be verified in two ways.

#### Structural verification

Run grep-based checks for remaining raw local-filesystem usage outside the allowed wrapper/boundary areas. The goal is not literally zero matches across the repo. The goal is that any remaining matches are either:

- inside the typed wrapper/path layer, or
- a deliberate documented boundary that this design explicitly allows

#### Behavioral verification

Run sequential verification for the touched projects. At minimum, expect:

- `Arius.Core.Tests`
- `Arius.Integration.Tests`
- `Arius.Cli.Tests`
- `Arius.E2E.Tests` build
- benchmark build or relevant benchmark project verification if touched
- `slopwatch analyze`

Targeted test runs should be used first for heavily touched files, especially if new typed enumeration or typed metadata helpers are introduced.

## Recommended Execution Strategy

Execute the sweep in this order:

1. add only the missing typed helper surface required by repeated raw caller patterns
2. clean remaining production callers
3. clean shared test infrastructure and benchmarks
4. clean direct test callers that still model typed roots through raw strings
5. run grep-based structural verification
6. run sequential behavioral verification

This ordering keeps the helper surface demand-driven and avoids rewriting many callers before the typed primitives they need exist.

## File Map

Likely touched production files:

- `src/Arius.Core/Shared/FileSystem/RootedPathExtensions.cs`
- `src/Arius.Core/Shared/FileSystem/LocalRootPathExtensions.cs`
- `src/Arius.Core/Shared/FileSystem/FilePairEnumerator.cs`
- `src/Arius.Core/Features/ListQuery/ListQueryHandler.cs`
- `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`
- optionally `src/Arius.Core/Shared/FileTree/FileTreeService.cs`

Likely touched non-production areas:

- `src/Arius.Tests.Shared/`
- `src/Arius.Core.Tests/`
- `src/Arius.Integration.Tests/`
- `src/Arius.E2E.Tests/`
- `src/Arius.Benchmarks/`

Likely untouched by design:

- Explorer persisted settings string storage
- CLI/progress relative-path payload strings
- dataset declaration models that intentionally remain textual until materialization

## Verification

Structural verification:

- grep for `File.*`, `Directory.*`, `Path.*`, `DirectoryInfo`, and `FileInfo` outside approved wrapper and boundary areas
- inspect remaining matches and confirm each one is either wrapper-layer code or an allowed boundary

Behavioral verification:

- `dotnet test --project "src/Arius.Core.Tests/Arius.Core.Tests.csproj"`
- `dotnet test --project "src/Arius.Integration.Tests/Arius.Integration.Tests.csproj"`
- `dotnet test --project "src/Arius.Cli.Tests/Arius.Cli.Tests.csproj"`
- `dotnet build "src/Arius.E2E.Tests/Arius.E2E.Tests.csproj" --no-restore`
- benchmark project build/verification for touched benchmark code
- `slopwatch analyze`

All of these should be run sequentially in this repository to avoid the known shared-output file-lock issues.

## Self-Review

- Placeholder check: no TBD sections remain.
- Scope check: broad by request, but still bounded to local filesystem/path cleanup rather than unrelated refactors.
- Ambiguity check: true string boundaries are explicitly named so the implementation does not drift into mechanical string eradication.
- Consistency check: the design prefers minimal typed-helper additions and demand-driven caller cleanup rather than speculative abstraction.
