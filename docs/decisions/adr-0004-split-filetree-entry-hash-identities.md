---
status: accepted
date: 2026-04-28
decision-makers: Wouter Van Ranst, OpenCode
---

# Split Filetree Entries By Hash Identity

## Context and Problem Statement

Filetree blobs describe repository structure. A file entry points at archived file content, while a directory entry points at another filetree blob. Before the split, filetree entries used one entry shape with a generic hash field and an entry-type discriminator.

The question for this ADR is whether filetree entries should remain one tagged model, or whether code should represent file and directory entries as separate types with distinct hash properties.

## Decision Drivers

* file entries and directory entries reference different repository identities
* file entries carry restore-relevant timestamps, while directory entries do not
* filetree serialization should remain deterministic and compatible with existing repository data
* traversal, restore, and list code should not need to infer hash meaning from an entry-type discriminator
* snapshot root hashes and directory-entry hashes should use `FileTreeHash`
* file-entry hashes should use `ContentHash`

## Considered Options

* Keep one tagged `FileTreeEntry` model with one hash property
* Keep one model but add separate optional hash properties
* Split filetree entries into `FileEntry` and `DirectoryEntry`

## Decision Outcome

Chosen option: "Split filetree entries into `FileEntry` and `DirectoryEntry`", because a file entry and a directory entry carry different hash identities and different metadata. The in-memory model should make those differences explicit while keeping the serialized filetree format stable.

`FileEntry` carries a `ContentHash` plus creation and modification timestamps. `DirectoryEntry` carries a `FileTreeHash` and no file timestamp metadata. Both inherit from the shared `FileTreeEntry` base for consumers that work over all entries in a directory.

### Consequences

* Good, because code that handles a file entry gets a `ContentHash` without casting or interpreting an entry-type flag.
* Good, because code that handles a directory entry gets a `FileTreeHash`, matching snapshot root and tree traversal semantics.
* Good, because timestamp metadata is available only where it is meaningful: on file entries.
* Good, because the serialized filetree format can stay deterministic and stable while the in-memory model becomes safer.
* Bad, because consumers that previously switched on an entry type now need pattern matching over derived entry records.
* Bad, because serializers must explicitly map the stable wire format to the richer in-memory types.

### Confirmation

The decision is being followed when filetree construction, serialization, traversal, list, restore, and snapshot code use `FileEntry.ContentHash` for files and `DirectoryEntry.FileTreeHash` for directories. Tests should verify that filetree serialization remains deterministic and that deserialization reconstructs the correct entry type and typed hash identity.

## Pros and Cons of the Options

### Keep one tagged `FileTreeEntry` model with one hash property

This is the previous shape.

* Good, because consumers only deal with one concrete entry type.
* Good, because the model resembles a line-oriented serialized format.
* Bad, because the hash property's meaning changes based on an entry-type discriminator.
* Bad, because file content identity and directory tree identity can be mixed accidentally.

### Keep one model but add separate optional hash properties

This avoids inheritance but makes validity depend on property combinations.

* Good, because one concrete entry type remains easy to enumerate.
* Neutral, because separate properties make the intended hash identity more visible than one generic hash field.
* Bad, because invalid states become representable, such as a file entry with a filetree hash or a directory entry with timestamps.
* Bad, because consumers still need validation or conditional logic to know which properties are present.

### Split filetree entries into `FileEntry` and `DirectoryEntry`

This is the chosen design.

* Good, because the type system represents the valid filetree entry shapes.
* Good, because file-specific metadata and directory-specific hash identity are no longer optional or ambiguous.
* Good, because pattern matching makes traversal behavior explicit at each consumer.
* Bad, because serializers and broad consumers need to handle two derived types.

## More Information

This ADR records the filetree-entry part of the deleted 2026-04-27 typed-hashes implementation plan. It is separate from ADR-0003 because distinct repository hash value objects and the filetree entry model are related but independent decisions.
