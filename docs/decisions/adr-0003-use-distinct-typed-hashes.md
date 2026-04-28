---
status: accepted
date: 2026-04-28
decision-makers: Wouter Van Ranst, OpenCode
---

# Use Distinct Typed Hashes For Repository Identities

## Context and Problem Statement

Arius is content-addressed, but the same 64-character lowercase hex value can describe different repository identities depending on where it is used. A content hash identifies original file bytes, a chunk hash identifies stored chunk bytes, and a filetree hash identifies an immutable directory tree blob.

The question for this ADR is how Arius should represent those identities in code while keeping persisted repository formats stable and readable.

## Decision Drivers

* content hashes, chunk hashes, and filetree hashes have different domain meanings even when their serialized shape is the same
* storage names, pointer files, snapshots, and filetree blobs should continue to use canonical lowercase hex strings
* incorrect hash identity mixing should be caught by the compiler where possible
* conversions between compatible identities should be explicit and local to the boundary that needs them
* default or uninitialized hash values should fail fast instead of silently behaving like valid hashes
* progress, events, and test helpers should use the same typed identities as production code

## Considered Options

* Keep using raw strings everywhere
* Use one generic hash value object for all repository hashes
* Use distinct `ContentHash`, `ChunkHash`, and `FileTreeHash` value objects

## Decision Outcome

Chosen option: "Use distinct `ContentHash`, `ChunkHash`, and `FileTreeHash` value objects", because Arius has separate repository identities that should not be interchangeable in memory. Persisted and wire formats remain canonical lowercase hex strings, but domain code uses the specific hash type that matches the identity being handled.

Typed conversions are allowed only where a real repository relationship exists. For example, a large chunk uses the same bytes and canonical value as the source content hash, so `ChunkHash.Parse(ContentHash)` is clearer than a string-hop conversion such as `ChunkHash.Parse(contentHash.ToString())`. These overloads preserve explicit conversion while avoiding stringly plumbing at call sites.

Archive upload progress uses `ChunkHash` because upload progress is about the chunk being stored, not the source file identity. Hash-computation progress remains path-based because that progress is tied to reading a local file before a content hash exists.

Repeated test hash helpers live in `Arius.Tests.Shared` so tests can create deterministic fake `ContentHash`, `ChunkHash`, and `FileTreeHash` values without duplicating local helper methods.

### Consequences

* Good, because method signatures communicate whether code is working with original file content, stored chunk data, or filetree structure.
* Good, because accidental mixing of content, chunk, and filetree identities becomes a compile-time error in most call paths.
* Good, because persisted repository data remains compatible: hash values still serialize as canonical lowercase hex strings.
* Good, because typed conversion overloads make intentional identity transitions easier to see during review.
* Good, because progress callbacks and events now describe the repository entity they actually report on.
* Bad, because code that crosses storage, serialization, or UI boundaries must explicitly convert typed hashes to and from strings.
* Bad, because some legitimate transitions, such as content hash to large chunk hash, need explicit overloads and tests.

### Confirmation

The decision is being followed when production code uses `ContentHash`, `ChunkHash`, and `FileTreeHash` inside the domain; converts to strings only at storage, serialization, logging, CLI, and UI boundaries; and avoids `Parse(x.ToString())` string-hop conversions where a typed overload exists.

Focused hash tests should cover parsing, normalization, digest formatting, fail-fast default behavior, and typed conversion overloads. Archive and CLI tests should verify upload progress is keyed by `ChunkHash`.

## Pros and Cons of the Options

### Keep using raw strings everywhere

This is the simplest representation mechanically.

* Good, because it requires no conversion at storage or serialization boundaries.
* Good, because all existing APIs can keep passing strings around.
* Bad, because code cannot tell whether a string is a content hash, chunk hash, filetree hash, blob path, or arbitrary text.
* Bad, because accidental identity mixing is discovered only through tests or runtime behavior.

### Use one generic hash value object for all repository hashes

This adds validation and canonical formatting without separating identity types.

* Good, because canonical lowercase hex parsing and formatting are centralized.
* Good, because it reduces raw string use.
* Bad, because it still allows a filetree hash to be passed where a content hash is expected.
* Bad, because it hides important repository semantics behind one generic abstraction.

### Use distinct `ContentHash`, `ChunkHash`, and `FileTreeHash` value objects

This is the chosen design.

* Good, because it models Arius repository identities directly.
* Good, because it keeps hash identity errors out of most runtime paths.
* Good, because each type can expose only the conversions that are meaningful for that identity.
* Bad, because some boundary code has to be more explicit about parsing and formatting.

## More Information

This ADR preserves the architectural intent from the deleted 2026-04-27 superpowers plans for typed hashes, typed hash conversion overloads, shared hash test helpers, and archive upload progress chunk-hash callbacks. Those plans were implementation artifacts; this ADR records the durable decision.

On 2026-04-28, the representative Azurite workflow benchmark on `woutbook6` stayed effectively flat in runtime (`25.75 s` to `25.68 s`) while managed allocation dropped from `1.08 GB` to `840.47 MB`. The strongest direct cause was the passphrase-hashing pipeline change that stopped allocating a fresh per-hash stream buffer and avoided concatenation buffers when computing `SHA256(passphrase || data)`. Typed hashes were a secondary contributor because archive, dedup, and progress paths now carry `ContentHash` and `ChunkHash` directly instead of repeatedly hopping through lowercase hex strings.

That benchmark result is supporting evidence, not the primary reason for this decision. The main reason remains domain correctness and compile-time separation of repository identities, but the benchmark confirms that removing stringly hash plumbing from hot paths did not regress runtime and can reduce transient allocation pressure.
