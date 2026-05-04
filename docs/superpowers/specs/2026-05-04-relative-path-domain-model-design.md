# Relative Path Domain Model Design

## Context

Arius already behaves as if repository-internal paths are a first-class domain concept, but most of the codebase still represents those paths as raw `string` values. The same repository-relative path semantics appear repeatedly in archive, list, restore, filetree staging, filetree serialization, fixtures, and E2E dataset helpers.

That creates three recurring problems:

- canonical-path validation is repeated and can drift
- path operations such as parent/child composition and root handling are encoded as ad hoc string manipulation
- boundary-specific formatting concerns such as trailing slash directory display leak back into the domain model

The goal of this design is to replace the scattered stringly path model with a strong kind-neutral path domain centered on `PathSegment` and `RelativePath`.

## Goals

- Make repository-internal relative paths first-class domain value objects.
- Replace repeated string validation and normalization helpers with typed parsing at boundaries.
- Represent repository root explicitly in the type system rather than as raw `""` at API boundaries.
- Keep repository path identity separate from host operating-system path syntax.
- Keep path identity kind-neutral: file vs directory is metadata on the node at that path, not part of the path value itself.
- Move archive, list, restore, filetree, and relevant test helpers toward typed repository paths.

## Non-Goals

- Do not model host local filesystem paths with the same type as repository-internal paths.
- Do not encode a trailing slash into the identity of a directory path.
- Do not introduce `FilePath` and `DirectoryPath` in the first step.
- Do not require all path-related code in the repository to migrate in one change.
- Do not collapse prefix-filter semantics into the core path type unless the semantics are made explicit.

## Core Model

### `PathSegment`

`PathSegment` represents one canonical repository path component.

Rules:

- not null
- not empty
- not whitespace-only
- contains no `/` or `\`
- not `.`
- not `..`
- no control characters such as `\r`, `\n`, `\0`
- preserves surrounding non-empty whitespace if Arius already treats it as meaningful

Responsibilities:

- parse and validate one segment
- expose canonical string form
- support equality and hashing as a value object

### `RelativePath`

`RelativePath` represents a canonical path inside the Arius repository tree.

Identity rules:

- zero or more `PathSegment` values
- canonical separator is `/`
- no leading slash
- no trailing slash in identity form
- root is represented explicitly as `RelativePath.Root`
- root is not represented as a raw empty string outside narrow compatibility boundaries

Responsibilities:

- parse and validate canonical repository-relative path input
- represent root explicitly
- expose path structure rather than forcing callers to split strings repeatedly
- provide composition and traversal operations

Suggested operations:

- `RelativePath.Root`
- `IsRoot`
- `SegmentCount`
- `Segments`
- `Name`
- `Parent`
- `StartsWith(RelativePath other)`
- `Append(PathSegment segment)`
- optional `/` operator between `RelativePath` and `PathSegment`
- `Parse` / `TryParse`
- canonical `ToString()`

## Domain Boundaries

### Repository-internal paths

These should become `RelativePath` as early as possible:

- archive pipeline file pairs and hashed file models
- restore traversal and restore targets
- list query emitted repository entries
- filetree construction and traversal
- pointer-file logical paths

### Filetree entry names

Filetree node entries should stop storing full paths when they only need one child name.

- `FileTreeEntry.Name` should trend toward `PathSegment`
- directory-entry trailing slash should remain a serializer concern, not the identity of the name itself
- serializer code may format a directory child name as `segment + "/"` at the boundary

This keeps the in-memory model aligned with the real filetree domain:

- the parent directory path is separate
- the child entry name is one segment
- directory-ness is represented by entry type, not by baking `/` into the child-name identity

### Prefix filters

`list` and `restore` currently accept looser string prefix inputs that are normalized and then matched.

This design keeps that as a separate concern. Two plausible future directions are:

- accept canonical `RelativePath` only for prefix filters
- introduce a separate `RelativePathPrefix` type if the selector semantics intentionally differ

This decision is deferred. The first path-domain slice should not blur the distinction.

### Local filesystem paths

Local OS paths remain outside the core `RelativePath` domain.

Boundary code is responsible for:

- converting local OS paths into `RelativePath` during archive enumeration
- converting `RelativePath` back to OS paths during restore and fixture materialization
- performing safe root-containment checks when joining local disk roots with repository-relative paths

The important rule is that once a path enters the Arius repository domain, it should stop being a raw string and become `RelativePath`.

## Why Kind-Neutral Paths

`RelativePath` should not encode file vs directory intent in the base type.

Reasoning:

- the same logical path may refer to a file or a directory depending on the node being described
- many flows know the path before they know the node kind
- trailing slash is a display or serialization convention, not identity
- Arius already has separate file-vs-directory models such as `RepositoryFileEntry`, `RepositoryDirectoryEntry`, `FileEntry`, and `DirectoryEntry`

Kind belongs on the node or entry model, not on the core path value.

## Operators And Developer Experience

Operator support is appropriate if it stays narrow and explicit.

Recommended operator support:

```csharp
var path = RelativePath.Root / PathSegment.Parse("photos") / PathSegment.Parse("2024");
```

Possible convenience support:

```csharp
var path = RelativePath.Root / "photos" / "2024" / "a.jpg";
```

Recommendation:

- support `/` only if it preserves strong validation and does not reintroduce silent stringly behavior
- prefer explicit `PathSegment` composition in core code if the string overload feels too magical
- keep `Parse` and `TryParse` as the primary boundary entry points

The design should favor correctness over cute syntax. The operator is a convenience, not the foundation.

## Migration Shape

This is a broad architectural migration, so the work should proceed in slices.

### Slice 1: Core types and compatibility adapters

- introduce `PathSegment`
- introduce `RelativePath`
- implement canonical parsing and root semantics
- keep compatibility adapters where existing code still needs `string`

### Slice 2: Archive entry boundary

- convert local enumeration output to `RelativePath` immediately
- make `FilePair.RelativePath` typed
- stop passing canonical repository paths as raw strings through archive internals

### Slice 3: Restore and list domain models

- change `FileToRestore.RelativePath` to `RelativePath`
- change `RepositoryEntry.RelativePath` and derived models to `RelativePath`
- move path comparisons and subtree operations onto typed APIs where possible

### Slice 4: Filetree model cleanup

- change `FileTreeEntry.Name` to `PathSegment`
- move trailing slash directory formatting fully into serializer and list-display boundaries
- remove serializer-specific string identity from in-memory filetree models

### Slice 5: Tests, fixtures, and E2E dataset helpers

- replace repeated canonical-path helpers with typed construction/parsing
- keep safe local join and fixture root-containment checks at the local-path boundary

## Expected Codebase Effects

This model should remove or shrink many current helpers, including:

- canonical repository-relative path validators
- repeated `Split('/')` and `string.Join('/', ...)` logic in domain code
- raw `""` root sentinel handling in high-level APIs
- serializer-adjacent directory-name string conventions in in-memory models

What should remain as boundary concerns:

- local filesystem root joining and containment validation
- serializer formatting that emits a trailing slash for directory display or persisted line formats
- user-input parsing for prefix filters if those remain looser than canonical paths

## Risks

- migration cost is high because path strings are central across archive, list, restore, filetree, tests, fixtures, and E2E helpers
- changing path types will surface many implicit assumptions that are currently hidden in string operations
- if compatibility layers linger too long, the codebase may temporarily contain both typed and stringly path flows

## Success Criteria

The design is being followed when:

- repository-internal paths are parsed into `RelativePath` at domain boundaries and carried as typed values internally
- single entry names are represented as `PathSegment` rather than serializer-shaped strings
- root is represented as `RelativePath.Root` rather than raw `""` in public or internal domain APIs
- archive, list, restore, and filetree logic perform structural path operations on typed values rather than on raw strings
- local OS path handling remains clearly separated at infrastructure boundaries

The design is not being followed when:

- new canonical-path string helpers appear in core domain code
- directory identity still depends on trailing slash in in-memory models
- repository path parsing and normalization continue to happen repeatedly after the initial boundary

## Open Follow-Up Decisions

- whether prefix selectors should become canonical `RelativePath` values or a separate `RelativePathPrefix` type
- whether `/` operator support should accept only `PathSegment` or also string overloads
- whether thin `FilePath` or `DirectoryPath` wrappers are useful later on top of kind-neutral `RelativePath`
