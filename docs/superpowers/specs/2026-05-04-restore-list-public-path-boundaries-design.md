# Restore/List Public Path Boundaries Design

## Context

The typed path migration already moved restore and list internals onto `RelativePath`, but both flows still expose repository-relative selector inputs as raw strings:

- `RestoreOptions.TargetPath`
- `ListQueryOptions.Prefix`

That leaves two feature-local normalization seams behind:

- `RestoreCommandHandler.NormalizeTargetPath`
- `ListQueryHandler.NormalizePrefix`

Those seams keep canonical repository path parsing too deep in the feature handlers instead of at the boundary where the options are created.

At the same time, local filesystem roots such as `RestoreOptions.RootDirectory` and `ListQueryOptions.LocalPath` are still true host-OS paths. This repo does not currently have a shared local-path value object, and inventing one in the same slice would widen scope materially.

## Decision

For this slice:

- change `RestoreOptions.TargetPath` from `string?` to `RelativePath?`
- change `ListQueryOptions.Prefix` from `string?` to `RelativePath?`
- remove `NormalizeTargetPath` and `NormalizePrefix`
- keep `RestoreOptions.RootDirectory` as `string`
- keep `ListQueryOptions.LocalPath` as `string?`

## Why This Slice

This is the smallest change that still widens the public API in the direction already chosen for repository-internal paths.

It improves boundary ownership because:

- repository-relative selectors become typed before they enter the core handlers
- handler logic no longer re-parses canonical repository paths from strings
- `RelativePath` remains the single domain type for repository-relative identity

It deliberately does not introduce a local filesystem path type yet because:

- local paths are host-specific boundaries, not repository-domain identities
- there is no existing reusable local-path type in `src/Arius.Core/Shared/Paths/`
- adding one now would expand this slice into a new design problem spanning archive, list, restore, and shared helpers

## Boundary Ownership

### Repository-relative option boundaries

The following properties become typed public boundaries:

- `RestoreOptions.TargetPath : RelativePath?`
- `ListQueryOptions.Prefix : RelativePath?`

Construction sites that currently hold raw text remain responsible for parsing user input into `RelativePath`:

- CLI verbs
- Explorer view models
- tests and fixtures that construct options directly

If a caller wants to preserve loose user-input affordances such as leading `/`, that normalization belongs at that outer boundary immediately before parsing, not in the core feature handler.

### Local filesystem boundaries

The following properties remain string-based for now:

- `RestoreOptions.RootDirectory : string`
- `ListQueryOptions.LocalPath : string?`

These remain boundary strings because they describe host filesystem roots and are consumed by `Path`, `Directory`, `FileInfo`, and `DirectoryInfo` APIs.

## Handler Changes

### Restore

- `CollectFilesAsync` should accept `RelativePath? targetPath`
- tree traversal should use that typed prefix directly
- `NormalizeTargetPath` should be deleted

Any boundary normalization needed for existing restore callers should happen before `RestoreOptions` is constructed.

### List

- `Handle` should consume `opts.Prefix` directly as `RelativePath?`
- `ResolveStartingPointAsync` should traverse prefix segments from the typed path instead of `prefix.Value.ToString().Split('/')`
- `NormalizePrefix` should be deleted

If the local recursion state benefits from it, `LocalDirectory` may be retyped as a small internal record or value carried through `ResolveStartingPointAsync` and recursion targets, but that remains an internal implementation detail.

## Compatibility And Input Semantics

This slice intentionally keeps current user-facing affordances, but moves them outward.

Expected behavior:

- canonical repository-relative inputs continue to work unchanged
- empty or missing selector input still means no prefix / full restore
- restore callers that currently pass a leading slash may keep doing so only if the outer boundary trims that affordance before parsing

Not in scope for this slice:

- changing local filesystem root semantics
- introducing `LocalDirectoryPath`, `RootDirectoryPath`, or similar public types
- changing `RelativePath` parsing rules
- introducing a separate `RelativePathPrefix` type

## Tests

Use TDD for the implementation slice.

Coverage should include:

- restore tests constructing `RestoreOptions` with typed `TargetPath`
- list tests constructing `ListQueryOptions` with typed `Prefix`
- boundary tests in CLI and Explorer for parsing string input into typed options
- regression coverage for the leading-slash restore input affordance if it is still supported by the chosen boundary

## Success Criteria

This slice is complete when:

- `RestoreOptions.TargetPath` is `RelativePath?`
- `ListQueryOptions.Prefix` is `RelativePath?`
- restore and list handlers no longer contain feature-local normalization helpers for repository-relative selectors
- repository-relative selector parsing happens at CLI / Explorer / test construction boundaries instead of in the handlers
- `RestoreOptions.RootDirectory` and `ListQueryOptions.LocalPath` remain string-based in this slice
