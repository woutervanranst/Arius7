# Typed List Query Hashes Design

## Goal

Use first-class hash value objects in internal Arius.Core list and hydration query result models instead of string hashes, and keep string conversion only at real presentation or external serialization boundaries.

## Scope

In scope:
- `RepositoryFileEntry.ContentHash`
- `RepositoryDirectoryEntry.TreeHash`
- `ChunkHydrationStatusResult.ContentHash`
- Internal handler and test updates required by those type changes

Out of scope:
- CLI presentation-state models such as `TrackedFile`
- Logging format changes
- New public wire contracts outside the existing Arius assemblies
- Broader refactors of unrelated query/result models

## Current State

The repository already treats `ContentHash`, `ChunkHash`, and `FileTreeHash` as first-class domain identities in Core internals.

Examples:
- archive feature models use typed hashes directly
- `ListQueryHandler` traverses file trees and pointer files with typed hashes internally
- chunk-index and file-tree services use typed hashes throughout

But some emitted query/result records still flatten those identities into strings:
- `RepositoryFileEntry.ContentHash` is `string?`
- `RepositoryDirectoryEntry.TreeHash` is `string?`
- `ChunkHydrationStatusResult.ContentHash` is `string?`

That creates an internal string boundary inside Core. Downstream consumers such as `ChunkHydrationStatusQueryHandler` must parse `RepositoryFileEntry.ContentHash` back into `ContentHash` before doing useful work.

## Decision

Inside `Arius.Core`, list and hydration query/result models should use typed hashes when the field represents a domain hash identity.

Change:
- `RepositoryFileEntry.ContentHash` from `string?` to `ContentHash?`
- `RepositoryDirectoryEntry.TreeHash` from `string?` to `FileTreeHash?`
- `ChunkHydrationStatusResult.ContentHash` from `string?` to `ContentHash?`

Boundary rule:
- Keep typed hashes inside Core and other Arius assemblies that consume Core models directly.
- Convert to string only at presentation or external boundaries such as CLI output, logs, and explicit serialized contracts.

## Why

Benefits:
- removes parse/stringify churn inside Core
- makes wrong-hash-type usage a compile-time problem
- aligns query/result models with the rest of the domain model
- simplifies handlers that currently accept stringly records and immediately parse them

Costs:
- some tests and consumers must switch from string assertions to typed-hash assertions
- any real presentation/output layer must stringify explicitly instead of relying on pre-stringified Core models

## Detailed Design

### List Query Models

`RepositoryFileEntry` will carry `ContentHash?` directly.

Implications:
- cloud-backed file entries will expose the typed `FileEntry.ContentHash`
- local-only file entries will continue to use `null`
- consumers that compare or group by content hash can do so without reparsing

`RepositoryDirectoryEntry` will carry `FileTreeHash?` directly.

Implications:
- cloud-backed directory entries will expose the typed `DirectoryEntry.FileTreeHash`
- local-only directory entries will continue to use `null`

### Hydration Status Results

`ChunkHydrationStatusResult` will carry `ContentHash?` directly.

Implications:
- unknown or missing hash stays `null`
- valid cloud-backed results preserve the parsed hash identity without flattening to string first
- downstream callers can still stringify at the display boundary when needed

### Handler Changes

`ListQueryHandler` should stop converting typed hashes to strings when constructing result records.

`ChunkHydrationStatusQueryHandler` should stop validating and parsing hash strings from `RepositoryFileEntry` because the input model will already be typed.

The handler should still preserve current behavior for files that do not have a cloud hash:
- skip files with `ExistsInCloud == false`
- skip files whose typed `ContentHash` is `null`

### Testing Changes

Existing tests that assert string hashes should instead assert typed equality.

Examples:
- list query tests should compare `file.ContentHash` to `ContentHashFor(...)`
- list query tests should compare `directory.TreeHash` to `TreeHashFor(...)`
- hydration status tests should construct `RepositoryFileEntry` with typed hashes and assert `ChunkHydrationStatusResult.ContentHash` as typed values where relevant

Focused verification should cover:
- list query streaming for cloud-only, local-only, and merged states
- hydration status resolution for large, thin, and tar-backed files
- any compile-time fallout in direct consumers of these records

## Alternatives Considered

### Keep strings in emitted result models

Pros:
- minimal consumer churn
- simpler ad hoc serialization

Cons:
- keeps an unnecessary internal string boundary in Core
- forces reparsing in downstream logic
- weakens the typed-hash direction already established elsewhere

Rejected because the records are internal Arius query/result models, not a separate public wire contract.

### Introduce parallel internal typed models plus string DTOs

Pros:
- preserves string DTO convenience at presentation boundaries
- keeps domain logic strongly typed

Cons:
- adds mapping layers and model duplication without a current external-contract need
- more moving pieces than necessary for a small internal cleanup

Rejected as premature for the current architecture.

## Risks And Mitigations

Risk:
- some consumers may currently assume hash fields are already formatted strings

Mitigation:
- keep conversion explicit only in CLI/output code
- use focused test updates to reveal every consumer that depended on string values

Risk:
- nullable typed hashes can still be mishandled if call sites assume presence

Mitigation:
- preserve existing `null` semantics for local-only or unknown entries
- keep handlers filtering on `null` before lookup work

## Success Criteria

The change is successful when:
- the three result models use typed hash properties
- `ChunkHydrationStatusQueryHandler` no longer reparses hashes from `RepositoryFileEntry`
- focused Core tests pass with typed-hash assertions
- stringification occurs only at presentation or serialization boundaries
