# Local Root And Rooted Path Design

## Context

The current typed-path model gives Arius a strong repository-relative domain centered on `PathSegment` and `RelativePath`, but local filesystem rooting is still mostly represented as raw `string` values.

Examples of the remaining stringly rooted-local-path seams:

- `RestoreOptions.RootDirectory`
- `ListQueryOptions.LocalPath`
- `ArchiveOptions.RootDirectory`
- `NormalizeLocalRoot(string? path)`
- `Path.Combine(localDirectory, segment.ToString())`
- `RelativePath.ToPlatformPath(string rootDirectory)`

Those seams matter because Arius does not operate on repository-relative paths in isolation. Archive, list, and restore frequently work with repository-relative paths _against a local root_. The root is therefore not incidental plumbing. It is a first-class local boundary concept.

At the same time, Arius should keep repository-path identity separate from host-local-path identity:

- `RelativePath` is stable repository identity
- local rooted paths are host filesystem addresses
- those two concepts need different equality semantics and different ownership of boundary operations

This design extends the existing path model with explicit local-root concepts instead of continuing to pass rooted local paths through the core as raw strings.

## Goals

- Make the local filesystem root a first-class type.
- Make rooted local paths a first-class type.
- Keep repository identity (`RelativePath`) distinct from local filesystem address (`RootedPath`).
- Move rooted local join and relativization operations onto owning types instead of keeping them as string helpers.
- Adopt the local-root model broadly across archive, list, and restore.
- Keep repository file and directory leaf names modeled as `PathSegment`, not a separate `FileName` type.

## Non-Goals

- Do not make local-path types depend on local directory existence.
- Do not make local-path types cross-machine persistent identity.
- Do not infer a `RootedPath` from a full absolute path without an explicit root.
- Do not introduce a separate `FileName` type in this slice.
- Do not weaken `RelativePath` equality into host-path semantics.

## Decision

Introduce two additional path types in `Arius.Core.Shared.Paths`:

- `LocalRootPath`
- `RootedPath`

The resulting model is:

- `PathSegment`: one canonical repository path segment
- `RelativePath`: an unrooted canonical repository path
- `LocalRootPath`: an absolute canonical local filesystem root
- `RootedPath`: a `RelativePath` anchored at a `LocalRootPath`

This keeps repository identity and local filesystem addressing separate, while making the local root explicit and reusable throughout archive, list, and restore.

## Core Model

### `LocalRootPath`

`LocalRootPath` represents an absolute canonical local filesystem anchor.

Responsibilities:

- parse and validate a rooted local path at the host boundary
- normalize it to a canonical absolute form
- represent the local root independently from any repository-relative suffix
- own root-relative conversion operations

Rules:

- must be absolute
- must be canonicalized with `Path.GetFullPath(...)`
- does not require the directory to exist
- is a local boundary type, not a repository identity type

Suggested operations:

- `Parse` / `TryParse`
- `GetRelativePath(string fullPath)`
- `TryGetRelativePath(string fullPath, out RelativePath path)`

`GetRelativePath(...)` and `TryGetRelativePath(...)` should accept only full absolute local paths. They should not accept already-relative local path text because that weakens the meaning of the root boundary and reintroduces ambiguity.

### `RootedPath`

`RootedPath` represents a repository-relative path rooted at one `LocalRootPath`.

Responsibilities:

- keep the root and repository-relative suffix explicit
- provide the full local path string for OS APIs
- act as the primary internal carrier for local filesystem addresses under a known root

Representation:

- store `LocalRootPath Root`
- store `RelativePath RelativePath`
- derive `FullPath` on demand

`RootedPath` should not be just another wrapped absolute path string. Its value is the explicit decomposition into root plus repository-relative path.

Suggested operations:

- `FullPath`
- `Name` forwarding to `RelativePath.Name` if that proves useful
- child composition only if a concrete call site needs it

### `RelativePath`

`RelativePath` remains the repository-domain identity type.

Additional responsibility in this design:

- `RootedAt(LocalRootPath root) -> RootedPath`

`RelativePath.Name` remains the last `PathSegment` of the path and therefore works for both files and directories.

Examples:

- `RelativePath.Parse("docs/readme.txt").Name` -> `readme.txt`
- `RelativePath.Parse("photos/2024").Name` -> `2024`
- `RelativePath.Root.Name` -> `null`

No separate `FileName` type is introduced because Arius still treats file and directory paths as kind-neutral, and the current leaf-name semantics already match `PathSegment`.

## Equality And Hashing

`RelativePath` keeps its existing semantics:

- ordinal
- case-sensitive
- cross-OS stable

`LocalRootPath` and `RootedPath` should use host-path equality semantics as closely as practical:

- Windows: effectively ordinal-ignore-case
- Linux: effectively ordinal

Why:

- `RelativePath` is repository identity
- `RootedPath` is local filesystem address
- those are different concepts, so different equality rules are acceptable

Useful consequences:

- on Windows, `C:\repo` and `c:/repo/` compare equal as `LocalRootPath`
- on Linux, `/repo` and `/Repo` compare different as `LocalRootPath`
- if two different `RelativePath` values root to the same Windows path, that is a real restore hazard, so collapsing them under `RootedPath` host equality is useful rather than a bug

Important caveat:

- host equality is only an approximation of host filesystem semantics
- exact filesystem behavior, mounts, and per-directory case-sensitivity still belong to the I/O boundary, not to the value-object contract

These local-path types must not be treated as cross-machine persistent identity.

## Construction Rules

`RootedPath` should only be constructed through explicit root-aware APIs.

Allowed construction shapes:

- `relativePath.RootedAt(localRoot)`
- `RootedPath.Create(localRoot, relativePath)` if a factory is needed
- construction paths that always carry an explicit `LocalRootPath`

Rejected construction shape:

- `RootedPath.Parse(string fullPath)`

Reason:

An absolute local path alone does not uniquely determine the intended root decomposition.

Example:

- full path: `C:\repo\docs\readme.txt`
- possible roots: `C:\repo`, `C:\repo\docs`, or even `C:\`

If Arius wants `RootedPath` to preserve the root as a first-class concept, that root must stay explicit at construction time.

## Boundary Ownership

### Move rooted local-path operations off `RelativePath`

The current `RelativePath.ToPlatformPath(string rootDirectory)` operation should move away from `RelativePath`.

Reason:

- rooted local joining is not repository identity behavior
- it belongs to the local-root / rooted-local-path model

Preferred replacement:

- `relativePath.RootedAt(localRoot).FullPath`

`RelativePath.FromPlatformRelativePath(...)` is less problematic because it parses an unrooted local relative path into repository-relative identity, but the new design should still reevaluate whether that boundary belongs better on local-root-owned APIs over time.

### Root-relative relativization belongs to `LocalRootPath`

When Arius has a full local path and wants the repository-relative suffix under a known root, that operation belongs to the root:

- `localRoot.GetRelativePath(fullPath)`
- `localRoot.TryGetRelativePath(fullPath, out var relativePath)`

This keeps containment and root-relative interpretation owned by the root boundary instead of pretending raw strings are self-describing.

## Feature Adoption

### Archive

- change archive root options from `string` to `LocalRootPath`
- update `LocalFileEnumerator` to accept `LocalRootPath`
- replace raw rooted local path strings with `RootedPath` in archive code that touches disk
- move `Path.Combine(rootDirectory, ...)` seams onto the rooted path model

### Restore

- change `RestoreOptions.RootDirectory` from `string` to `LocalRootPath`
- use `RootedPath` as the primary internal carrier for restore output paths
- replace `file.RelativePath.ToPlatformPath(opts.RootDirectory)` with `file.RelativePath.RootedAt(opts.RootDirectory).FullPath`

### List

- change `ListQueryOptions.LocalPath` from `string?` to `LocalRootPath?`
- replace `NormalizeLocalRoot(...)`
- re-type `ResolveStartingPointAsync(...)` and recursion state so local directory state is `RootedPath?` rather than `string?`
- replace `Path.Combine(localDirectory, segment.ToString())` with rooted composition through `RootedPath` or `RelativePath.RootedAt(...)`

### Explorer, CLI, Tests, Fixtures

- outer boundaries parse local-root strings into `LocalRootPath`
- tests and fixtures stop constructing typed options from raw local-root strings where possible
- `FileItemViewModel` should use `file.RelativePath.Name` rather than `Path.GetFileName(file.RelativePath.ToString())`

## File Name Guidance

The leaf-name concept for repository paths remains `PathSegment`.

This design explicitly chooses not to introduce a dedicated `FileName` type because:

- Arius paths are still kind-neutral
- the last path segment already expresses the needed identity
- a separate `FileName` type would duplicate semantics without adding clear domain constraints in this slice

The recommended usage is:

- keep `RelativePath.Name : PathSegment?`
- optionally expose convenience forwarding APIs on models that already own a `RelativePath`
- call `.ToString()` only at the final string boundary such as UI display, logs, or serializer output

## Migration Shape

This should proceed in broad but still explicit slices.

### Slice 1: introduce local-root types

- add `LocalRootPath`
- add `RootedPath`
- add `RelativePath.RootedAt(LocalRootPath root)`
- add root-owned relativization APIs
- update tests for equality, parsing, canonicalization, and decomposition

### Slice 2: move rooted-path ownership

- remove or retire `RelativePath.ToPlatformPath(...)`
- update callers onto `LocalRootPath` / `RootedPath`
- reevaluate whether `RelativePath.FromPlatformRelativePath(...)` remains in the right place

### Slice 3: broad feature adoption

- archive
- restore
- list
- Explorer
- CLI
- tests and fixtures

The goal is not to leave `LocalRootPath` and `RootedPath` as isolated helpers. The goal is to make them normal typed carriers through the local-path portions of the archive, list, and restore flows.

## Tests

Coverage should include:

- `LocalRootPath` canonicalization and absolute-path validation
- no existence requirement for `LocalRootPath`
- host-path equality expectations for `LocalRootPath` and `RootedPath`
- `RelativePath.RootedAt(...)` composition
- `LocalRootPath.GetRelativePath(...)` containment and rejection behavior
- round-tripping between `RelativePath`, `LocalRootPath`, and `RootedPath`
- archive, list, and restore flows updated to use typed local-root boundaries
- Explorer UI name rendering through `RelativePath.Name`

## Success Criteria

This design is being followed when:

- Arius models rooted local filesystem addresses as `RootedPath`
- Arius models local roots as `LocalRootPath`
- archive, list, and restore carry typed local-root state instead of raw rooted path strings where the root is semantically known
- `RelativePath` remains repository identity with ordinal, case-sensitive equality
- `LocalRootPath` and `RootedPath` use host-path equality semantics as closely as practical
- `RelativePath.Name` is used as the native leaf-name concept for both files and directories
- rooted local joining and root-relative relativization no longer live as ad hoc feature-local string helpers

This design is not being followed when:

- new `Path.Combine(root, relative.ToString())` patterns appear in core feature logic where typed rooted-path operations could be used instead
- `RootedPath.Parse(string fullPath)` appears and hides root decomposition
- local-root options remain raw strings in newly touched archive, list, or restore boundaries without a deliberate compatibility reason
- UI code converts `RelativePath` back to host path text just to discover the last name segment
