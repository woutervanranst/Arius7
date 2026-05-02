---
status: accepted
date: 2026-04-29
decision-makers: Wouter Van Ranst, OpenCode
---

# Improve scalability of FileTree Build

## Context and Problem Statement

Arius snapshots point at an immutable filetree root. Filetrees need to describe repository structure for archives ranging from small repositories to repositories with millions of files spread across many directories, without requiring all file entries to be loaded in memory at once.

The question for this ADR is how Arius should stage and build immutable filetree nodes during archive creation while preserving deterministic Merkle hashes, Windows-safe local paths, and clear ownership between archive orchestration, filetree construction, and filetree storage.

## Decision Drivers

* filetree construction should scale with current subtree breadth rather than total repository file count
* snapshots must not be published until all referenced filetrees are durably stored
* filetree hashes must stay deterministic and content-addressed
* local staging paths must be safe for Windows path-length constraints
* staging should reuse the persisted `FileEntry` shape for file records instead of creating a second manifest domain model
* directory staging should be path-local, not content-hash-local
* filetree storage/cache behavior should be owned by `FileTreeService`, not by archive orchestration
* filetree hash calculation should be owned by `FileTreeBuilder`, not by `FileTreeService`
* filetree calculation and upload should overlap where possible without weakening snapshot durability

## Considered Options

* Build filetrees from a single path-sorted manifest file
* Build filetrees from literal mirrored directory staging
* Build filetrees from staged directory nodes
* Build filetrees from a second filesystem walk

## Decision Outcome

Chosen option: "Build filetrees from staged directory nodes", because it preserves the current immutable directory-node filetree model while avoiding whole-repository in-memory manifest sorting, keeping staging paths fixed-length, and allowing node calculation to overlap with upload.

During archive creation, completed files are staged under the repository filetree cache at:

```text
~/.arius/{account}-{container}/filetrees/.staging/
```

There is no per-run subdirectory. A local staging session locks the repository filetree cache and deletes any stale `.staging` directory before writing a fresh staging graph. Another local archive for the same repository cache must fail fast rather than delete active staging data.

Each archive directory maps to a staging node id:

```text
dirId = SHA256(canonicalRelativeDirectoryPath)
```

The root directory path is the empty string. Canonical relative paths use `/` separators, no leading slash, no trailing slash, and ordinal case-sensitive semantics. Each staged node is stored as one file directly under `.staging`:

```text
filetrees/.staging/{dirId}
```

Each staged node file contains append-only filetree-shaped records. File lines use the final persisted format directly:

```text
<content-hash> F <created:O> <modified:O> <leaf-file-name>
```

Staged directory lines use the same overall shape, but the first field is a staged directory id rather than a child `FileTreeHash`:

```text
<child-dir-id> D <child-directory-name>/
```

The child line format intentionally resembles a persisted directory entry, but the first field is only a staging directory id until the child subtree has been built. This staged form is modeled explicitly as `StagedDirectoryEntry : FileTreeEntry`, while persisted nodes continue to use `DirectoryEntry : FileTreeEntry`.

After all archive file work and chunk-index flushing are complete, `FileTreeBuilder` builds the staging graph bottom-up from the root directory id. Each directory reads its staged node file, validates duplicate names, resolves child directories to final child `FileTreeHash` values, combines staged `FileEntry` values with final `DirectoryEntry` values, sorts by `FileTreeEntry.Name`, computes its own `FileTreeHash`, and publishes the completed node for storage.

`FileTreeBuilder` is responsible for hash calculation. `FileTreeService` owns filetree validation, remote existence checks, upload, and local cache writes through an `EnsureStoredAsync(FileTreeHash hash, IReadOnlyList<FileTreeEntry> entries)`-style API. `ArchiveCommandHandler` owns archive workflow and decides which files enter staging. `FileTreeBuilder` owns the bottom-up Merkle construction algorithm.

The builder and storage service are decoupled by a bounded channel of completed directory nodes. Upload workers store completed nodes concurrently while the builder continues calculating parent and sibling nodes. The archive tail still waits for all filetree uploads to finish before publishing the snapshot, so durability semantics are unchanged.

### Consequences

* Good, because filetree build memory is bounded by active subtrees and the largest individual directory, not by total repository file count.
* Good, because source path length does not directly determine staging path length.
* Good, because staged file lines and persisted file-entry lines share one `FileTreeEntry` serialization surface.
* Good, because child subtrees can be built while upload workers store completed nodes in parallel.
* Good, because stale local staging from a crash is safe to delete before the next archive run.
* Bad, because staged directory lines still need a staged-only subtype until child hashes are known.
* Bad, because duplicate child links can be written by concurrent archive workers and must be deduplicated during build.
* Bad, because one directory containing millions of direct children still produces one large filetree blob.

### Confirmation

The decision is being followed when archive creation writes completed files into `.staging/{dirId}` node files, staged file records use the final `FileEntry` line format, staged directory records use `StagedDirectoryEntry`, `FileTreeBuilder` computes directory hashes bottom-up from staging, and `FileTreeService` stores already-identified immutable nodes.

Tests should confirm that staging paths are hash-based, stale `.staging` is cleared after acquiring the local lock, nested file paths create parent-child links, duplicate file names in one staged node fail fast, duplicate child links do not change the final root hash, and archived repositories list and restore with the expected structure.

## Pros and Cons of the Options

### Build filetrees from a single path-sorted manifest file

This stages each archived file as one root-relative record, sorts by path, and streams sorted records into a stack-based Merkle builder.

* Good, because path order naturally supports closing completed directories bottom-up.
* Good, because it avoids storing a directory graph separately.
* Bad, because it requires a bounded-memory external sort to scale.
* Bad, because it retains a manifest-like staging concept separate from the filetree directory model.

### Build filetrees from literal mirrored directory staging

This recreates the archive directory structure under `.staging` and stores an `entries` file in each mirrored directory.

* Good, because the filesystem layout directly represents parent-child relationships.
* Good, because no global sort is needed.
* Bad, because staging paths are longer than source paths and can hit Windows path-length limits earlier than the source repository.
* Bad, because reserved staging filenames such as `entries` or `.entries` can collide with real archived names unless extra escaping rules are introduced.

### Build filetrees from staged directory nodes

This is the chosen design.

* Good, because staging paths are fixed-length and Windows-friendly.
* Good, because no whole-repository sort is needed.
* Good, because each directory can be read, sorted, hashed, and built independently after its children are known.
* Good, because filetree-related temporary state stays under the filetree cache.
* Good, because node calculation can overlap with filetree upload.
* Bad, because parent-child relationships must still be stored explicitly in staged directory records.

### Build filetrees from a second filesystem walk

This avoids staging by walking the source tree again after chunk upload work.

* Good, because it appears to reduce temporary staging files.
* Bad, because it can double file I/O or require reusing pointer files that may not exist with `--no-pointers`.
* Bad, because files can change between the upload pass and the filetree pass, producing snapshots that do not describe the uploaded content unless additional validation is added.
* Bad, because durable per-file upload results would still be needed for crash-safe correctness, which recreates staging under another name.

## More Information

This ADR describes the intended filetree architecture as the target steady-state design. It does not document migration steps or intermediate implementations.

Related decisions:

* ADR-0003 uses distinct typed hashes for repository identities.
* ADR-0004 splits persisted filetree entries into `FileEntry` and `DirectoryEntry`.

The implementation design and task plan are recorded in:

* `docs/superpowers/specs/2026-04-30-filetree-staging-and-build-design.md`
* `docs/superpowers/plans/2026-04-30-decouple-filetree-build-and-upload.md`
* `docs/filetrees.md`
