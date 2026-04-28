---
status: accepted
date: 2026-04-27
decision-makers: Wouter Van Ranst, OpenCode
---

# Typed Hash Value Objects For Content, Chunk, And FileTree Identities

## Context and Problem Statement

Arius is a content-addressed backup tool whose domain has three distinct hash identities:

* **Content hash** — hash of an original binary file's content.
* **Chunk hash** — name of the blob that actually stores content (identical to the content hash for large chunks, different for tar/thin chunks).
* **FileTree hash** — hash of an immutable filetree blob describing one directory's entries.

These identities were historically plumbed through the codebase as `string` (lowercase hex) or `byte[]`, both in production code and in tests. That made it possible to assign a chunk hash where a content hash was expected, hide intent in repeated `Convert.ToHexString(...).ToLowerInvariant()` boilerplate, and lose validation at boundaries.

The decision is whether to keep stringly/raw-byte hash plumbing or introduce dedicated typed value objects per identity.

Domain modeling of filetree entries (file content hash vs child filetree hash on each entry) is a related but separate decision, recorded in ADR-0004.

## Decision Drivers

* Domain identities (content vs chunk vs filetree hash) must be impossible to mix up at compile time.
* Persisted and wire formats must remain canonical lowercase hex strings — this ADR changes in-memory typing only, not blob layouts or serialized formats.
* Validation should fail fast at construction; uninitialized `default(...)` instances must not silently behave as valid hashes.
* Conversions between compatible identities (e.g., a `ContentHash` reused as the `ChunkHash` for a large chunk) should be explicit and not require hopping through `string`.
* Test code should not duplicate hash-construction helpers across projects.
* No implicit conversions between hash types or between hash types and `string`; conversions happen only at storage, serialization, log, and UI boundaries.

## Considered Options

* Keep `string`/`byte[]` hash plumbing and rely on naming conventions and review.
* Introduce a single generic `Hash` type used everywhere.
* Introduce one typed value object per domain identity (`ContentHash`, `ChunkHash`, `FileTreeHash`) with shared codec utilities and explicit conversion overloads.

## Decision Outcome

Chosen option: "Introduce one typed value object per domain identity", because it makes the three identities distinct at the type system level, keeps fail-fast validation in one place per type, and matches the existing domain language.

Concretely:

* `ContentHash`, `ChunkHash`, and `FileTreeHash` live under `src/Arius.Core/Shared/Hashes/`. They are immutable, validate format on construction, and reject `default(...)` access via their `Value` accessors.
* A shared `HashCodec` owns parsing, normalization, and digest formatting so the three types do not duplicate hex logic.
* Persisted formats (blob names, serialized filetrees, pointer files, log output) remain canonical lowercase hex; conversions happen at those boundaries only.
* Typed conversion overloads (e.g., `ChunkHash.From(ContentHash)`) replace `Parse(x.ToString())` patterns at call sites that legitimately reuse a hash across identities.
* Production APIs that previously took `string` hashes (`IEncryptionService`, chunk index, chunk storage, archive/restore handlers, pointer-file enumeration, list query, snapshot service) take typed hashes. `SnapshotManifest.RootHash`, `SnapshotCreatedEvent.RootHash`, `SnapshotResolvedEvent.RootHash`, and `ProgressState.SnapshotRootHash` are all typed `FileTreeHash`.
* `IEncryptionService` gains `ComputeHashAsync(string filePath, IProgress<long>?, CancellationToken)` so file-path hashing flows through one place with optional progress.
* Archive upload progress callbacks are keyed by `ChunkHash` rather than by raw string identifiers.
* A single shared test helper in `Arius.Tests.Shared/Hashes/HashTestData.cs` exposes deterministic 64-character hash factories (`Content(char)`, `Chunk(char)`, `FileTree(char)`) so test projects do not redeclare identical local helpers.

### Boundaries kept as `string`

* `ArchiveResult.RootHash` (the public archive command result) remains `string?`. The handler converts the internal `FileTreeHash` to its canonical hex string at the result boundary. This keeps the public command result a plain DTO of primitives and avoids leaking domain value objects to callers that only render or log the hash.

### Consequences

* Good, because mismatched identities (e.g., passing a filetree hash where a chunk hash is expected) become compile errors instead of runtime defects or silent data corruption.
* Good, because validation, normalization, and `default(...)` rejection live in one place per type.
* Good, because test code stops duplicating hash-construction helpers across `Arius.Cli.Tests` and `Arius.Core.Tests`.
* Good, because file-path hashing with optional progress lives behind one encryption-service method instead of being open-coded at call sites.
* Neutral, because persisted formats, blob names, and snapshot/filetree wire layouts are unchanged.
* Bad, because every storage, serialization, log, and UI boundary now needs an explicit conversion to/from canonical hex, which adds a small amount of plumbing.
* Bad, because the migration touches many feature handlers, services, models, and tests in one architectural sweep.

### Confirmation

The decision is being followed when:

* `Shared/Hashes/` defines `ContentHash`, `ChunkHash`, `FileTreeHash`, and `HashCodec`, with focused tests covering parse/normalize/format and `default(...)` rejection.
* `IEncryptionService`, chunk index/storage, filetree, snapshot, and archive/restore APIs expose typed hashes rather than `string` or `byte[]` for hash identities.
* No production call site uses `Parse(x.ToString())` to convert between hash types; typed conversion overloads are used instead.
* Archive upload progress reports `ChunkHash`, not `string`.
* `ArchiveResult.RootHash` remains `string?` and is the only public hash boundary still expressed as a string.
* Test projects consume `Arius.Tests.Shared/Hashes/HashTestData` instead of redeclaring local `Content(char)`/`Chunk(char)` helpers.

## More Information

This ADR captures the architectural intent of three implementation plans dated 2026-04-27 (typed hashes — hash value objects portion only, typed-hash conversion overloads, archive upload progress chunkhash callback, shared hash test helper). The filetree-entry domain split portion of the typed-hashes plan is recorded in ADR-0004. Those plans are tactical task breakdowns for executing this decision and are not part of the long-term decision record.
