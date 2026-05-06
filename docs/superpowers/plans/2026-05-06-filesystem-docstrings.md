# FileSystem Docstrings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add concise Arius-style XML docs to every type in `Arius.Core.Shared.FileSystem` and to the namespace's public methods and public extension members.

**Architecture:** This is a documentation-only change. Keep the current typed filesystem API intact and add summaries that explain the semantic role of each type and public member in Arius rather than restating raw BCL behavior.

**Tech Stack:** C# 14, .NET 10, XML documentation comments, Arius typed filesystem model

---

## File Map

- `docs/superpowers/specs/2026-05-06-filesystem-docstrings-design.md`: design note for the documentation slice
- `src/Arius.Core/Shared/FileSystem/PathSegment.cs`: type and parsing/member docs for canonical repository path segments
- `src/Arius.Core/Shared/FileSystem/RelativePath.cs`: type and public method docs for canonical repository-relative paths
- `src/Arius.Core/Shared/FileSystem/LocalRootPath.cs`: type and public method docs for absolute local archive roots
- `src/Arius.Core/Shared/FileSystem/RootedPath.cs`: type and public member docs for a local root plus repository-relative path pair
- `src/Arius.Core/Shared/FileSystem/FilePair.cs`: preserve and tighten existing archive-time file/pointer docs where needed
- `src/Arius.Core/Shared/FileSystem/FilePairEnumerator.cs`: preserve and tighten existing enumerator docs where needed
- `src/Arius.Core/Shared/FileSystem/PathSegmentExtensions.cs`: type and extension-member docs for path-segment metadata
- `src/Arius.Core/Shared/FileSystem/RelativePathExtensions.cs`: type and extension-member docs for pointer-file path helpers
- `src/Arius.Core/Shared/FileSystem/LocalRootPathExtensions.cs`: type and extension-member docs for typed root-directory IO
- `src/Arius.Core/Shared/FileSystem/RootedPathExtensions.cs`: type and extension-member docs for typed file and directory IO

### Task 1: Add XML docs to the core path value types

**Files:**
- Modify: `src/Arius.Core/Shared/FileSystem/PathSegment.cs`
- Modify: `src/Arius.Core/Shared/FileSystem/RelativePath.cs`
- Modify: `src/Arius.Core/Shared/FileSystem/LocalRootPath.cs`
- Modify: `src/Arius.Core/Shared/FileSystem/RootedPath.cs`

- [ ] **Step 1: Add concise type summaries**

Document what each type means in Arius and why it exists.

```csharp
/// <summary>
/// Represents one canonical repository path segment.
///
/// This type keeps repository-internal path composition out of raw strings so
/// callers cannot accidentally smuggle separators, dot-segments, or control
/// characters into Arius path identities.
/// </summary>
public readonly record struct PathSegment
```

- [ ] **Step 2: Add public method summaries**

Document parse, conversion, containment, and composition methods with concise semantic summaries.

```csharp
/// <summary>Parses a canonical repository-relative path.</summary>
public static RelativePath Parse(string value, bool allowEmpty = false)

/// <summary>Converts an absolute local path under this root into a repository-relative path.</summary>
public RelativePath GetRelativePath(string fullPath)
```

- [ ] **Step 3: Keep unchanged behavior and signatures**

Do not change any runtime logic while adding the docs.

### Task 2: Add XML docs to archive-time filesystem types

**Files:**
- Modify: `src/Arius.Core/Shared/FileSystem/FilePair.cs`
- Modify: `src/Arius.Core/Shared/FileSystem/FilePairEnumerator.cs`

- [ ] **Step 1: Preserve the existing domain explanation**

Retain the current archive-time meaning of binary and pointer files.

```csharp
/// <summary>
/// Represents the archive-time view of one repository path, combining the
/// binary file and its optional pointer file.
/// </summary>
public sealed record FilePair
```

- [ ] **Step 2: Tighten any missing public-member coverage**

If a public constructor or helper lacks a summary, add one in the same concise style.

### Task 3: Add XML docs to typed filesystem extension surfaces

**Files:**
- Modify: `src/Arius.Core/Shared/FileSystem/PathSegmentExtensions.cs`
- Modify: `src/Arius.Core/Shared/FileSystem/RelativePathExtensions.cs`
- Modify: `src/Arius.Core/Shared/FileSystem/LocalRootPathExtensions.cs`
- Modify: `src/Arius.Core/Shared/FileSystem/RootedPathExtensions.cs`

- [ ] **Step 1: Add type summaries for each extension container**

Explain that these files are the typed bridge between Arius path values and local filesystem operations or derived path metadata.

- [ ] **Step 2: Add summaries to public extension members**

Document what filesystem meaning each extension member carries.

```csharp
/// <summary>Returns the pointer-file path for this repository-relative binary path.</summary>
public RelativePath ToPointerFilePath()

/// <summary>Copies this file to another rooted path and preserves timestamps.</summary>
public async ValueTask CopyToAsync(RootedPath destination, bool overwrite = false, CancellationToken cancellationToken = default)
```

- [ ] **Step 3: Keep obvious wrappers concise**

Use one-line summaries where the member is an intentionally thin typed wrapper.

### Task 4: Verify the documentation-only change

**Files:**
- Modify: none

- [ ] **Step 1: Build the touched production project**

Run: `dotnet build "src/Arius.Core/Arius.Core.csproj" --no-restore`

Expected: build succeeds with no syntax errors introduced by XML comments.

- [ ] **Step 2: Spot-check coverage**

Confirm every type and every public method/public extension member in `src/Arius.Core/Shared/FileSystem/` now has a summary.

- [ ] **Step 3: Commit if requested later**

Do not create a commit unless explicitly asked.
