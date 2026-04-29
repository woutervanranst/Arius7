# Scalable Filetree Staging Design

## Context

The current archive tail writes a temporary manifest file, sorts it in memory, then builds filetree blobs by loading all directory entries into memory. That does not scale to repositories with millions of files spread across many directories. The current persisted filetree model also has a temporary `ManifestEntry` type whose data overlaps with `FileEntry` once the parent directory is known.

The target scale for this design is many files spread across many directories. A single directory containing millions of direct children remains bounded by the current filetree blob model and is accepted as an existing limitation.

## Goals

- Remove the all-record in-memory manifest sort from the archive tail.
- Remove the all-directory-entry in-memory build from `FileTreeBuilder`.
- Replace manifest staging with filetree-shaped staging under the repository filetree cache.
- Reuse `FileEntry` for staged file records instead of introducing a new per-file build record type.
- Keep the persisted filetree format, filetree hashes, and snapshot format unchanged.
- Keep archive durability: no snapshot is published until all referenced chunks, chunk-index data, and filetrees are durable.
- Design for Windows path-length constraints by avoiding literal source-path mirroring in staging paths.

## Non-Goals

- Do not change the persisted `FileTreeBlob`, `FileEntry`, or `DirectoryEntry` format.
- Do not add sharded filetree blobs for huge single-directory workloads.
- Do not use content-hash-prefix sharding for filetree staging; filetree construction is path-local, not content-hash-local.
- Do not design special handling for SHA-256 directory-id collisions.
- Do not support concurrent archive runs for the same repository cache in one shared staging directory.

## Staging Location

Use one repository-scoped staging directory:

```text
~/.arius/{account}-{container}/filetrees/.staging/
```

There is no run-id directory. At archive start, the staging session acquires a repository-local staging lock and deletes any existing `.staging` directory before creating a fresh one. This makes stale staging from a crashed prior run harmless. If another archive run already holds the staging lock for the same repository, the new run fails fast instead of deleting active staging data.

The lock is not part of the staging path and does not need remote coordination. It protects only the local cache directory from two local archive processes sharing `.staging`.

## Directory Node Identity

Every archive directory maps to a fixed-length staging node id:

```text
dirId = SHA256(canonicalRelativeDirectoryPath)
```

Canonical relative directory path rules:

- Root directory is the empty string `""`.
- Use `/` separators.
- No leading slash.
- No trailing slash.
- Use ordinal, case-sensitive comparisons, matching existing filetree serialization semantics.

Directory nodes are stored below a two-character prefix fanout to avoid very large filesystem directories:

```text
filetrees/.staging/dirs/{dirId[0..2]}/{dirId}/
```

Examples:

```text
""              -> SHA256("")
"photos"        -> SHA256("photos")
"photos/2024"   -> SHA256("photos/2024")
```

No path metadata file is stored for collision detection. SHA-256 collision handling is out of scope.

## Staging Files

Each directory node can contain two append-only files:

```text
entries
children
```

`entries` contains canonical serialized `FileEntry` lines for files directly inside that archive directory:

```text
<content-hash> F <created:O> <modified:O> <leaf-file-name>
```

`children` contains child directory links. The first field is a staging directory id, not a final filetree hash:

```text
<child-dir-id> D <child-directory-name>/
```

The child line intentionally mirrors the existing directory-entry text shape, but it is staging metadata. It must not be parsed as a persisted `DirectoryEntry` because the id is a staging directory id until that child subtree is built.

For this source tree:

```text
readme.txt
photos/a.jpg
photos/2024/b.jpg
docs/manual.pdf
```

The staging graph contains four directory ids:

```text
root        = SHA256("")
photos      = SHA256("photos")
photos2024  = SHA256("photos/2024")
docs        = SHA256("docs")
```

Root `entries`:

```text
<hash-readme> F 2026-04-29T10:00:00.0000000+00:00 2026-04-29T10:00:00.0000000+00:00 readme.txt
```

Root `children`:

```text
<photos-dir-id> D photos/
<docs-dir-id> D docs/
```

`photos` `entries`:

```text
<hash-a> F 2026-04-29T10:00:00.0000000+00:00 2026-04-29T10:00:00.0000000+00:00 a.jpg
```

`photos` `children`:

```text
<photos-2024-dir-id> D 2024/
```

`photos/2024` `entries`:

```text
<hash-b> F 2026-04-29T10:00:00.0000000+00:00 2026-04-29T10:00:00.0000000+00:00 b.jpg
```

`docs` `entries`:

```text
<hash-manual> F 2026-04-29T10:00:00.0000000+00:00 2026-04-29T10:00:00.0000000+00:00 manual.pdf
```

Duplicate child lines are allowed during staging because multiple files in the same child subtree may race to ensure the same edge. The builder deduplicates child links by child directory name and id when reading a node.

## Responsibilities

`ArchiveCommandHandler` remains the archive workflow orchestrator. It decides which files belong in the snapshot and calls a staging writer after a file has been deduplicated or uploaded successfully. It does not know staging file paths or child-link formats.

`FileTreeStagingWriter` owns the `.staging` directory. It acquires the staging lock, clears stale staging, computes staging directory ids, appends `FileEntry` lines to `entries`, and appends child links to `children`. It handles local per-node write coordination for concurrent archive workers.

`FileTreeBuilder` consumes a completed staging directory. It starts from the root directory id, builds child directories before parents, combines staged `FileEntry` records with final child `DirectoryEntry` records, sorts each directory's entries by name, and asks `FileTreeService` to store the resulting `FileTreeBlob`.

`FileTreeService` owns immutable filetree persistence and cache semantics. It validates remote filetree knowledge, computes filetree hashes, skips known filetrees, uploads missing filetrees, and writes local cache files. It exposes a higher-level `EnsureStoredAsync(FileTreeBlob tree, CancellationToken cancellationToken)` method returning `FileTreeHash`.

## Build Algorithm

1. At archive start, create a staging session under `filetrees/.staging` and clear stale staging after acquiring the local staging lock.
2. For each archived file, derive its parent directory path and leaf file name from the root-relative path.
3. Append a serialized `FileEntry` line to the parent directory node's `entries` file.
4. Ensure child-link lines exist for each parent-child directory edge in the file path. Duplicates may be written and are removed during build.
5. After chunk uploads and chunk-index flush are complete, validate `FileTreeService` once.
6. Build the root staging node bottom-up.
7. For each directory node, build child nodes first and receive final `DirectoryEntry` values from them.
8. Read this node's staged `FileEntry` lines, combine them with child `DirectoryEntry` values, sort by `FileTreeEntry.Name`, and store the resulting `FileTreeBlob` through `FileTreeService.EnsureStoredAsync`.
9. Return the root filetree hash to `ArchiveCommandHandler`.
10. Publish the snapshot only after the root hash is available and all filetree storage work has completed.
11. Delete `.staging` when the archive finishes or fails, after releasing file handles.

Empty directories continue to be skipped. A directory node that has no file entries and no built child directory entries returns `null` to its parent. The root returns `null` for an empty archive.

## Parallelization

Staging writes are parallelized by directory node. Archive workers can append to unrelated node files concurrently. A per-node async lock serializes writes to the same `entries` or `children` file.

Tree building is parallelized across independent child subtrees after staging is complete. The builder uses bounded branch parallelism so a directory with many children does not create unbounded tasks. A child subtree can build independently from its siblings. A parent waits for all non-empty children, then computes and stores its own filetree.

`FileTreeService.EnsureStoredAsync` calls may execute concurrently because parallel child subtrees finish independently. The builder must await all child subtree work before returning the root hash, preserving snapshot safety.

## Error Handling And Cleanup

If staging cannot acquire the local staging lock, archive fails before modifying staging. If stale `.staging` exists and no lock is held, archive deletes it and starts fresh.

Any failure while staging, building, uploading filetrees, flushing the chunk index, or creating the snapshot causes archive failure. No snapshot is created unless all referenced data has been stored.

Cleanup of `.staging` is best-effort after failure because stale staging is safe to delete at the next run. Cleanup after success should normally remove the directory before returning.

Cancellation must be observed while writing staged entries, reading staging files, building child subtrees, storing filetrees, and cleaning up.

## Testing And Verification

Unit coverage should verify:

- Staging writer stores direct file entries under the expected hashed node path.
- Staging writer stores parent-child links for nested paths.
- Staging writer clears stale `.staging` at session start when no lock is held.
- Concurrent writes to different directories succeed and produce parseable entries.
- Builder creates the same root hash as the current manifest builder for representative simple and nested inputs.
- Builder deduplicates duplicate child links.
- Builder skips empty directory nodes.
- Builder does not return until `FileTreeService.EnsureStoredAsync` has completed for all referenced filetrees.

Integration coverage should verify a complete archive still restores and lists the same repository structure, and a second unchanged archive reuses existing filetrees.

Relevant commands:

```bash
dotnet test --project src/Arius.Core.Tests/Arius.Core.Tests.csproj --treenode-filter "/*/*/FileTree*/*"
dotnet test --project src/Arius.Integration.Tests/Arius.Integration.Tests.csproj --treenode-filter "/*/*/FileTreeBuilderIntegrationTests/*"
```
