# Glossary

The single home for Arius domain vocabulary. Design docs and reviews should link
here rather than redefining terms (e.g. `../glossary.md#chunk`). Each entry gives a
one-line intent definition and where the concept is defined in code.

Prefer these terms consistently in code, tests, docs, and reviews. Avoid generic
words like "blob" or "pointer" when a more precise domain term applies.

---

## Files & paths

### binary file

**binary file** — a file on disk that Arius archives and restores; the original
content that is hashed, chunked, and uploaded.
*Code:* `BinaryFile` in `src/Arius.Core/Features/ArchiveCommand/Models.cs`.

### pointer file

**pointer file** — a file on disk that stands in for a binary file by carrying only
its content hash, modelling thin-archive state (the bytes live in the repository, not
locally). On-disk format is the bare hex hash (v7); legacy v5 pointers (`{"BinaryHash":…}`
JSON) are still read and upgraded on archive.
*Code:* `PointerFile` in `src/Arius.Core/Features/ArchiveCommand/Models.cs`; format in
`PointerFileFormat` (`src/Arius.Core/Shared/FileSystem/`).

### FilePair

**FilePair** — the local archive-time view of one repository path, unifying the
binary-only, pointer-only, and binary-plus-pointer cases behind one model. Carries the
validated relative path and its optional binary and pointer components.
*Code:* `FilePair` in `src/Arius.Core/Features/ArchiveCommand/Models.cs`.

### RelativePath

**RelativePath** — a validated repository-relative path in Arius's canonical
forward-slash format. Preferred for repository-relative paths, subtree roots, and
prefixes that may contain multiple segments; do not pass such values as `string`.
*Code:* `RelativePath` (readonly record struct) in `src/Arius.Core/Shared/FileSystem/RelativePath.cs`.

### PathSegment

**PathSegment** — exactly one validated path segment (rejects separators and traversal
markers). Use only when the value is semantically a single segment, not a multi-segment
path.
*Code:* `PathSegment` (readonly record struct) in `src/Arius.Core/Shared/FileSystem/PathSegment.cs`.

### RelativeFileSystem

**RelativeFileSystem** — Arius.Core's local filesystem boundary scoped to a
`LocalDirectory` root, so features do containment-safe IO using `RelativePath` values
instead of raw host strings. Kept internal alongside the other archive-time/local types.
*Code:* `RelativeFileSystem` in `src/Arius.Core/Shared/FileSystem/RelativeFileSystem.cs`
(root type `LocalDirectory` in `src/Arius.Core/Shared/FileSystem/LocalDirectory.cs`).

### exclusion

**exclusion** — a file or directory the archive walk skips so it never enters a
[snapshot](#snapshot): NAS metadata folders (`@eaDir`), OS junk files (`thumbs.db`,
`.ds_store`), and — configurably — `System`/`Hidden`-attribute entries. Defaults live once in
Arius.Core's embedded `appsettings.json` and are applied as a directory-pruning enumeration; see
[ADR-0019](decisions/adr-0019-central-file-exclusion-configuration.md).
*Code:* `FileExclusionFilter` / `FileExclusionOptions` in
`src/Arius.Core/Shared/FileSystem/`; applied by `LocalFileEnumerator.EnumerateAsync` (archive) and by
`LocalDirectoryReader.Read` for the `ls` local overlay, so excluded entries don't resurface as
spurious local-only rows.

---

## Hashes

Keep distinct hash value objects for distinct identities — do not collapse them into a
generic hash or relate them by inheritance. Persisted/wire forms are canonical lowercase
hex (SHA-256 = 64 chars); convert to `string` only at boundaries.

### hash

**hash** — Arius is content-addressed storage and deduplicates binary files based on a
content hash. "Hash" without qualification means the content identity below.

### content hash

**content hash** — the hash of an (original) binary file's content; the dedup identity.
*Code:* `ContentHash` (readonly record struct) in `src/Arius.Core/Shared/Hashes/ContentHash.cs`.

### chunk hash

**chunk hash** — the name of the chunk in which the content is actually stored. Equal to
the content hash for large chunks; different for tar-bundled files.
*Code:* `ChunkHash` (readonly record struct) in `src/Arius.Core/Shared/Hashes/ChunkHash.cs`.

### ContentHash

**ContentHash** — the typed hash value object for content identity (see
[content hash](#content-hash)). Fail-fast for uninitialized instances.
*Code:* `ContentHash` in `src/Arius.Core/Shared/Hashes/ContentHash.cs`.

### ChunkHash

**ChunkHash** — the typed hash value object for chunk-blob naming (see
[chunk hash](#chunk-hash)).
*Code:* `ChunkHash` in `src/Arius.Core/Shared/Hashes/ChunkHash.cs`.

### FileTreeHash

**FileTreeHash** — the typed hash value object identifying a filetree (Merkle) node;
directory entries carry it, file entries carry a `ContentHash`.
*Code:* `FileTreeHash` in `src/Arius.Core/Shared/Hashes/FileTreeHash.cs`.

---

## Chunks & dedup

### dedup

**dedup** — content-addressed deduplication: identical binary content (same content
hash) is stored once. The [chunk index](#chunk-index) is the existence check.

### chunk

**chunk** — a stored unit of unique binary content. Three realizations exist, identified
by blob-path convention rather than distinct C# types (see [chunk hash](#chunk-hash) and
`BlobPaths`).
*Code:* path conventions in `BlobPaths` (`ChunkPath`, `ThinChunkPath`) in
`src/Arius.Core/Shared/Storage/BlobConstants.cs`; upload/download in
`ChunkStorageService` (`src/Arius.Core/Shared/ChunkStorage/ChunkStorageService.cs`).

### large chunk

**large chunk** — a chunk whose blob body stores one file directly as compressed
(plus optional encryption) bytes. For large chunks, chunk hash == content hash.
*Code:* discriminated by `ShardEntry.IsLargeChunk` in `src/Arius.Core/Shared/ChunkIndex/Shard.cs`;
named via `BlobPaths.ChunkPath(ChunkHash)`.

### tar chunk

**tar chunk** — a chunk whose blob body stores a tar bundle of multiple small files,
then compressed (plus optional encryption). *Why:* small files are prohibitively
expensive to rehydrate in Azure Blob archive tier, so they are tarred together into one
~large chunk.
*Code:* named via `BlobPaths.ChunkPath(ChunkHash)`; small-file shard entries carry a
distinct `ChunkHash` (`src/Arius.Core/Shared/ChunkIndex/Shard.cs`).

### thin chunk

**thin chunk** — a small pointer-like chunk blob whose body is the chunk hash of the tar
chunk that actually contains the file's bytes. *Why:* serves as the deduplication
existence check and metadata for a tar-bundled file.
*Code:* named via `BlobPaths.ThinChunkPath(ContentHash)` in
`src/Arius.Core/Shared/Storage/BlobConstants.cs`.

### chunk metadata sidecar

**chunk metadata sidecar** — a zero-byte Cool-tier blob under `chunks-v5legacy-metadata/`
that carries a chunk's v7 metadata (`arius_type` plus sizes/parent) in its own blob metadata,
for a chunk whose own blob is in the Azure Archive tier and so cannot accept
`Set Blob Metadata`. *Why:* lets the v5→v7 [migration](design/migration.md) describe
already-archived chunks without rehydrating them; [chunk-index repair](design/core/features/repair-chunk-index.md)
reads it as a fallback (the storage tier is still taken from the live chunk listing). See
[ADR-0018](decisions/adr-0018-archive-tier-metadata-sidecar.md).
*Code:* `BlobPaths.V5LegacySideCarPath(ChunkHash)` in
`src/Arius.Core/Shared/Storage/BlobConstants.cs`.

---

## Index & storage

### chunk index

**chunk index** — the repository-wide mapping from content hash to chunk hash. *Why:*
1/ tar lookups, 2/ efficient existence checks for deduplicated content, 3/ metadata
store.
*Code:* `ChunkIndexService` in `src/Arius.Core/Shared/ChunkIndex/ChunkIndexService.cs`.

### shard

**shard** — one mutable chunk-index blob, partitioned by a dynamic-length hash prefix
(2 hex chars to start; splits 16-way by the next hex char when it grows past the entry
threshold). The layout is self-describing from which shard blobs exist; reads use the
shallowest existing shard on a hash's prefix path (parent wins).
*Code:* `Shard` and `ShardEntry` in `src/Arius.Core/Shared/ChunkIndex/Shard.cs`;
named via `BlobPaths.ChunkIndexShardPath(PathSegment)`.

### chunk size

**chunk size** — the byte count of the stored chunk blob recorded per index entry. For
large chunks this is the large chunk blob size; for tar-bundled files it is the full
parent tar chunk blob size, not a per-file share. Restore, download progress, and
rehydration cost estimates operate on distinct chunks and must use this full size.
*Code:* `ShardEntry.ChunkSize` in `src/Arius.Core/Shared/ChunkIndex/Shard.cs`.

---

## Sizes

Three distinct size metrics describe the logical→physical chain. Always qualify which one
is meant — "size" alone is ambiguous. They are related by
`original size ≥ deduplicated size ≥ stored size`.

### original size

**original size** — the logical, uncompressed size of files, counting duplicate content
once *per file* (i.e. the size you would restore). Reported per-snapshot from the manifest
(`SnapshotManifest.OriginalSize`) and per-archive-run from `ArchiveResult.OriginalSize`.
*Code:* `SnapshotManifest.OriginalSize`; `ArchiveResult.OriginalSize`;
`ShardEntry.OriginalSize` (the per-content original size stored in the index).

### deduplicated size

**deduplicated size** — the sum of original (uncompressed) sizes over *distinct* content:
the unique data of the whole repository before compression. Repository-wide across all
snapshots; computed from the chunk index. Differs from [original size](#original-size) by
collapsing duplicate content, and from [stored size](#stored-size) by being pre-compression.
*Code:* `IChunkIndexService.GetDeduplicatedOriginalSize()` in
`src/Arius.Core/Shared/ChunkIndex/ChunkIndexService.cs`.

### stored size

**stored size** — the actual cloud storage footprint: sum of stored [chunk sizes](#chunk-size)
over distinct chunks — deduplicated *and* compressed (plus optional encryption). Repository-wide
across all snapshots, split by [storage tier hint](#storage-tier-hint). The per-run increment is
`ArchiveResult.IncrementalStoredSize`.
*Code:* `ChunkTierStatistic.StoredSize`; `ShardEntry.ChunkSize`;
`ArchiveResult.IncrementalStoredSize`.

### storage tier hint

**storage tier hint** — the chunk blob's storage tier at archive time, recorded per
index entry (wire values: hot=1, cool=2, cold=3, archive=4; for tar-bundled files, the
tar blob's tier). It is a *hint* — lifecycle policies or rehydration can change the
actual tier — and lets `ls` report hydrated-vs-archived state from the index without
per-blob calls. Live truth (including rehydration-pending) comes from
`ChunkHydrationStatusQuery`.
*Code:* `ShardEntry.StorageTierHint` (`src/Arius.Core/Shared/ChunkIndex/Shard.cs`),
typed by `BlobTier` enum in `src/Arius.Core/Shared/Storage/IBlobContainerService.cs`.

### region

**region** — the Azure region a repository's container is priced against for [cost estimates](design/core/shared/cost.md). It is **not** an account property: it lives in the container's own metadata (`region` key), seeded to a `default` sentinel on the first open and otherwise set out-of-band (e.g. in Azure Storage Explorer). Read into Core as `IBlobContainerService.RegionHint`; an unset region prices against a default (`northeurope`). It affects cost figures only — never the data path. Arius does not write it, but the resolved region is shown (read-only) in the web UI's repository list, flagged `(default)` when the container's metadata is unset.
*Code:* `AzureBlobContainerService.RegionMetadataKey` / `RegionHint`
(`src/Arius.AzureBlob/AzureBlobContainerService.cs`); fallback `AzureBlobContainerService.DefaultRegion`.

### epoch

**epoch** — the cache-coherence generation of locally cached chunk-index and filetree
state. An epoch mismatch (another machine published shards) invalidates the run-scoped
listing and forces a re-read.
*Code:* see epoch handling in `ChunkIndexService`
(`src/Arius.Core/Shared/ChunkIndex/ChunkIndexService.cs`) and `FileTreeService`
(`src/Arius.Core/Shared/FileTree/FileTreeService.cs`).

---

## Cache coordination

These verbs describe how Arius keeps a local cache consistent with the remote repository, and they are easy to confuse: **validate** is a *trust check* (is my local copy still good?), **synchronize** is a *push* (make remote match my local writes), and round-trip **verify** is unrelated to caching entirely.

### validated

**validated** — a local cache entry has been confirmed to still match remote state at the current snapshot [epoch](#epoch). FileTree: `FileTreeService.ValidateAsync` runs the epoch fast/slow path once per archive (fast = latest local snapshot name equals latest remote → trust the disk cache; slow = list remote and drop existence markers). ChunkIndex: a per-prefix *coverage claim* in `loaded_prefixes` is validated against a `snapshot_version` and the remote shard ETag. ("verified" is used loosely as a synonym for this trust check.)
*Code:* `FileTreeService.ValidateAsync`; `ChunkIndexLocalStore.FindCoveredPrefixes` / `IsPrefixAtETag`.

### revalidated

**revalidated** — (chunk index) a *cheap* re-confirmation: the prefix's remote shard ETag is **unchanged**, so its coverage claim is advanced to the new snapshot version *without re-downloading* the shard — as opposed to a full reload when the ETag differs. `PromoteToSnapshotVersionAsync` likewise re-stamps already-validated prefixes onto a newly published snapshot without re-probing remote. ("reverified" means the same thing.)
*Code:* `ChunkIndexLocalStore.IngestCoverage` (revalidated vs downloaded shards), `PromoteToSnapshotVersionAsync`.

### synchronized

**synchronized** — local state has been **pushed to remote** (the *opposite* direction from *validated*). Two uses:
- *Chunk index:* `MarkPendingFlushesSynchronized` runs after `FlushAsync` uploads shards — it flips [dirty](#chunk-index) `pending_flush = 1` rows to clean `0` and stamps the uploaded prefixes as validated. "Synchronized" = the local writes now live durably in remote shards.
- *Filetree:* `FileTreeBuilder.SynchronizeAsync` builds the Merkle tree bottom-up and uploads any **missing** tree blobs so the remote filetree matches the staged local tree.
*Code:* `ChunkIndexLocalStore.MarkPendingFlushesSynchronized`; `FileTreeBuilder.SynchronizeAsync`.

> **Not the same as round-trip *verification*.** The inline check that a just-compressed chunk frame decodes back to its [chunk hash](#chunk-hash) before the chunk is committed (`RoundTripVerifier`) is about *codec correctness*, not cache trust. See [compression](design/core/shared/compression.md).

---

## Repository structure

### filetree

**filetree** — an immutable Merkle-tree blob describing one directory's entries.
Filetrees model repository structure, not chunk storage; file entries carry a
`ContentHash`, directory entries carry a `FileTreeHash`.
*Code:* `FileTreeEntry` / `FileEntry` / `DirectoryEntry` in
`src/Arius.Core/Shared/FileTree/FileTreeModels.cs`; traversal/persistence in
`FileTreeService` (`src/Arius.Core/Shared/FileTree/FileTreeService.cs`); named via
`BlobPaths.FileTreePath(FileTreeHash)`.

### FileTreeEntry

**FileTreeEntry** — one entry in a persisted filetree node: a `FileEntry` (content hash
+ timestamps) or a `DirectoryEntry` (tree hash). `Name` is a single `PathSegment`, not a
full path.
*Code:* `FileTreeEntry` (abstract record) in `src/Arius.Core/Shared/FileTree/FileTreeModels.cs`.

### snapshot

**snapshot** — an immutable point-in-time manifest recording the root filetree hash and
snapshot totals (file count; `OriginalSize` — the logical size, i.e. summed original
uncompressed bytes of all files, counting duplicates once per file; creating Arius version).
*Code:* `SnapshotManifest` in `src/Arius.Core/Shared/Snapshot/SnapshotManifest.cs`;
resolve/create/list in `SnapshotService`
(`src/Arius.Core/Shared/Snapshot/SnapshotService.cs`); named via
`BlobPaths.SnapshotPath(...)`.
