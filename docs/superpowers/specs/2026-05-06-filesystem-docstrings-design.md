# FileSystem Docstrings Design

## Context

`src/Arius.Core/Shared/FileSystem/` now holds the typed local-filesystem vocabulary used throughout Arius, but XML documentation coverage is uneven. Some types such as `FilePair` and `FilePairEnumerator` already explain their role, while core path types and typed filesystem extension members are either undocumented or only partially documented.

Because these types encode Arius-specific meaning beyond raw `string` and raw `File`/`Directory` APIs, missing docs increase the chance that future changes treat them as thin wrappers instead of domain and boundary types.

## Goal

Add concise XML documentation in existing Arius style for every type in `Arius.Core.Shared.FileSystem` and for the public methods and public extension members in that namespace.

## Non-Goals

- Do not change runtime behavior.
- Do not rename members or refactor the typed filesystem API.
- Do not add exhaustive `param`/`returns` commentary where a short responsibility-oriented summary is enough.
- Do not expand documentation outside `Arius.Core.Shared.FileSystem`.

## Design

### 1. Document type responsibility and meaning

Every type in the namespace should get a `<summary>` that explains:

- what the type represents
- what responsibility it owns in the typed filesystem model
- why Arius uses it instead of leaving the concept as a raw primitive or raw filesystem API

This matters most for:

- `PathSegment`
- `RelativePath`
- `LocalRootPath`
- `RootedPath`
- the `*Extensions` classes that define the typed filesystem boundary

### 2. Document public methods and extension members at the semantic level

Public methods and public extension members should get concise XML docs that describe their Arius-facing meaning, not a restatement of the BCL call they forward to.

Examples of the desired emphasis:

- parsing methods should state the canonical form they enforce
- path-composition methods should state what kind of path they produce
- root-relative conversion methods should state containment expectations
- typed IO extension members should state whether they operate on files, directories, or existing filesystem entries

### 3. Preserve existing good docs and tighten only where needed

Existing docs on `FilePair` and `FilePairEnumerator` are already close to the target style. The change should preserve those explanations and only adjust wording where coverage is missing or phrasing is inconsistent with the rest of the namespace.

### 4. Keep the change surgical

This is a documentation-only slice. The touched production code should differ only by XML comments and any required formatting changes caused by inserting those comments.

## File Map

- `src/Arius.Core/Shared/FileSystem/PathSegment.cs`
- `src/Arius.Core/Shared/FileSystem/RelativePath.cs`
- `src/Arius.Core/Shared/FileSystem/LocalRootPath.cs`
- `src/Arius.Core/Shared/FileSystem/RootedPath.cs`
- `src/Arius.Core/Shared/FileSystem/FilePair.cs`
- `src/Arius.Core/Shared/FileSystem/FilePairEnumerator.cs`
- `src/Arius.Core/Shared/FileSystem/PathSegmentExtensions.cs`
- `src/Arius.Core/Shared/FileSystem/RelativePathExtensions.cs`
- `src/Arius.Core/Shared/FileSystem/LocalRootPathExtensions.cs`
- `src/Arius.Core/Shared/FileSystem/RootedPathExtensions.cs`

## Verification

- Build `Arius.Core` to catch malformed XML comments or accidental syntax issues.
- Spot-check the touched files to confirm each type and each public method/public extension member in the namespace is documented.

Suggested command:

- `dotnet build "src/Arius.Core/Arius.Core.csproj" --no-restore`

## Self-Review

- Placeholder check: no TBD or deferred decisions remain.
- Scope check: limited to `Arius.Core.Shared.FileSystem` documentation only.
- Ambiguity check: “public methods” is interpreted as public methods and public extension members, not private helpers.
- Consistency check: the doc style is concise and domain-focused rather than verbose API-reference prose.
