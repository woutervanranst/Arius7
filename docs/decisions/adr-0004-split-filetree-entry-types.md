---
status: accepted
date: 2026-04-27
decision-makers: Wouter Van Ranst, OpenCode
---

# Split FileTreeEntry Into File And Directory Entry Types

## Context and Problem Statement

A filetree blob describes the entries of one directory. Each entry is either a file (referencing a `ContentHash` and carrying restore-relevant timestamps) or a child directory (referencing a `FileTreeHash`).

The previous model expressed both kinds with a single `FileTreeEntry` record carrying a tagged-union-style `Hash` property and an entry-type discriminator. That made it ambiguous in memory whether an entry's hash was a file content hash or a child filetree hash, and required every consumer to branch on the discriminator before doing anything useful with the hash.

The decision is whether to keep one entry type with a discriminator, or split it into explicit file-entry and directory-entry types.

## Decision Drivers

* File entries and directory entries reference different hash identities (`ContentHash` vs `FileTreeHash`); see ADR-0003.
* File entries carry restore-relevant timestamps; directory entries do not.
* Consumers (filetree builder, serializer, archive/restore handlers) should be able to handle each kind without runtime discriminator checks.
* The serialized filetree blob format must not change — this is a domain-model decision, not a wire-format decision.

## Considered Options

* Keep one `FileTreeEntry` record with a discriminator and a generically-typed `Hash` property.
* Split into `FileEntry` and `DirectoryEntry` records that share a common `FileTreeEntry` base only for the `Name` property and for collection typing.

## Decision Outcome

Chosen option: "Split into `FileEntry` and `DirectoryEntry` records", because it lets the type system carry the discriminator and lets each entry kind expose only the fields that apply to it.

Concretely, in `src/Arius.Core/Shared/FileTree/FileTreeModels.cs`:

* `FileTreeEntry` is an abstract record with only `Name`.
* `FileEntry : FileTreeEntry` carries `ContentHash`, `Created`, and `Modified`.
* `DirectoryEntry : FileTreeEntry` carries `FileTreeHash`.
* `FileTreeBlob.Entries` is `IReadOnlyList<FileTreeEntry>`; consumers pattern-match to `FileEntry` or `DirectoryEntry`.
* The serialized filetree blob format and the filetree blob hash are unchanged.

### Consequences

* Good, because file content hashes and child filetree hashes are distinct in memory and cannot be confused at the type level.
* Good, because file-only fields (timestamps) live only on `FileEntry` and cannot be set or read accidentally on a directory entry.
* Good, because consumers branch via pattern matching once and then work with strongly-typed fields, instead of branching on a discriminator and then casting or null-checking a shared `Hash` property.
* Neutral, because the serialized filetree blob format is unchanged, so existing repositories continue to work.
* Bad, because every consumer of `FileTreeEntry` had to be updated to pattern-match on the new types during the migration.

### Confirmation

The decision is being followed when:

* `FileTreeModels.cs` defines `FileTreeEntry` as abstract with `Name` only, and `FileEntry`/`DirectoryEntry` as the only concrete subtypes.
* `FileEntry.ContentHash` is typed `ContentHash` and `DirectoryEntry.FileTreeHash` is typed `FileTreeHash`.
* `FileTreeBlobSerializer`, `FileTreeBuilder`, and downstream consumers pattern-match on the two entry types instead of branching on a discriminator.
* The serialized filetree blob format and existing filetree blob hashes remain unchanged.

## More Information

This ADR captures the domain-modeling portion of the 2026-04-27 typed-hashes implementation plan. The hash value objects themselves are recorded in ADR-0003.
