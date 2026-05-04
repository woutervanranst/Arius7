# Caching Architecture

## Overview

Three singleton services own all local caching. They sit between the feature
handlers and Azure Blob Storage, eliminating redundant network calls across
and within runs.

```
  archive / ls / restore
         │
         ├── SnapshotService       disk JSON  ↔  Azure snapshots/
         │       │
         │       └── coordinates epoch for ──────────────────────┐
         │                                                        │
         ├── FileTreeService      disk files ↔  Azure filetrees/ │
         │       │                                                │
         │       └── on mismatch, invalidates ◄───────────────────┘
         │
         └── ChunkIndexService     L1 memory LRU
                                   L2 disk    ↔  Azure chunk-index/
                                   L3 Azure
```

---

## SnapshotService

**What it caches:** `SnapshotManifest` — the root tree hash, file count, total
size, and timestamp recorded at the end of each archive run.

**Where:** `~/.arius/{account}-{container}/snapshots/{timestamp}` as plain JSON.
Azure stores the same data gzip-compressed and optionally encrypted.

**How it works:**

- `CreateAsync` — write-through: writes JSON to disk first, then uploads to
  Azure. The local file is always consistent with the remote after a successful
  archive.
- `ResolveAsync` — disk-first: lists remote blob names, selects the target
  (latest or version-matched), checks local disk. On a miss, downloads from
  Azure and caches to disk.

**Why it matters:** Snapshot timestamps are the coordination point for the
entire cache stack. `FileTreeService` compares the latest local snapshot name
against the latest remote one to decide whether the local tree and chunk-index
caches are trustworthy.

---

## FileTreeService

**What it caches:** serialized filetree nodes represented in code as
`IReadOnlyList<FileTreeEntry>` — the Merkle tree nodes that describe directory
structure. Each blob is content-addressed: its filename *is* its SHA-256 hash,
so a file on disk is correct by definition.

**Where:** `~/.arius/{account}-{container}/filetrees/{hash}` as canonical
plaintext filetree lines.
An empty (zero-byte) file is a *remote-existence marker* — the blob is known to
exist in Azure but has not been downloaded yet.

**How it works:**

- `ReadAsync` — disk-first. Non-empty file → deserialize and return immediately.
  Empty file or miss → download from Azure, write to disk, return. Concurrent
  reads for the same hash are coalesced into a single download.
- `WriteAsync` — upload to Azure (tolerates already-exists for crash recovery),
  then write to disk.
- `ExistsInRemote(hash)` — returns `File.Exists(diskPath)`. Reliable only after
  `ValidateAsync` has run; raises an exception otherwise.

**Validation (epoch check):** `ValidateAsync` runs once per archive pipeline,
before the tree-build phase.

- **Fast path** — the latest local snapshot filename matches the latest remote
  snapshot blob name. This machine was the last writer; the disk cache is
  complete and fully trusted. No network calls beyond the snapshot list.
- **Slow path** — names differ (another machine archived since the last local
  run, or this is a fresh machine). Lists all `filetrees/` blobs from Azure,
  creates empty marker files for any not yet on disk, deletes all L2
  chunk-index shard files, and calls `ChunkIndexService.InvalidateL1()`.

The slow path runs at most once per archive run and makes the disk cache
complete for existence checks, so all subsequent `ExistsInRemote` calls are
pure local file-system lookups.

---

## ChunkIndexService

**What it caches:** `ShardEntry` records — the mapping from a file's content
hash to its storage chunk hash, original size, and compressed size. This is the
deduplication index.

Entries are grouped into *shards* by the first four hex characters of the
content hash (~65 536 shards possible). Each shard is a dictionary of all
entries sharing that prefix.

**Three-tier cache:**

| Tier | Location | Notes |
|---|---|---|
| L1 — memory LRU | Process heap | Byte-budget eviction (default 512 MB). Thread-safe via lock + linked list. |
| L2 — disk | `~/.arius/{account}-{container}/chunk-index/{prefix}` | One file per shard prefix. Plain serialized bytes. Corrupt files are deleted and treated as misses. |
| L3 — Azure | `chunk-index/{prefix}` blob | Authoritative source. A hit populates L2 and L1. |

**Two auxiliary structures (memory, run-scoped):**

- `_inFlight` — a concurrent set of content hashes recorded during the current
  run. Checked before L1, so uploads from earlier in the same pipeline are
  found instantly without touching the shard cache.
- `_pendingEntries` — accumulates new `ShardEntry` values during the run.
  Flushed in bulk at end-of-run by `FlushAsync`.

**Key operations:**

- `LookupAsync` — checks `_inFlight` first, then loads shards for the remaining
  hashes through L1 → L2 → L3. Returns a dictionary of known entries.
- `AddEntry` — adds a newly uploaded chunk to `_inFlight` and
  `_pendingEntries` immediately after upload.
- `FlushAsync` — merges pending entries into existing shards, uploads merged
  shards to Azure, saves to L2, promotes to L1.
- `InvalidateL1` — clears the in-memory LRU. Called by `FileTreeService` on a
  snapshot mismatch, so stale shard objects are not served from memory after L2
  has been wiped.

**Why mutable shards require tiered invalidation:** Unlike tree blobs, shards
are mutable — any machine can extend a shard by uploading new chunks. When the
epoch mismatches, the local L2 shard files may be stale. Wiping L2 and clearing
L1 forces fresh downloads from Azure on the next lookup or flush.

---

## How Commands Use the Services

```
                      SnapshotService   FileTreeService   ChunkIndexService
                      ───────────────   ────────────────   ─────────────────
archive
  stage 3 – dedup                                          LookupAsync (per file)
  stage 4 – upload                                         AddEntry (per chunk)
  end-of-pipeline     CreateAsync       ValidateAsync      FlushAsync
                                        ExistsInRemote     (InvalidateL1 via
                                        WriteAsync          ValidateAsync)
                                        ReadAsync

restore
  step 1 – resolve    ResolveAsync
  step 2 – walk tree                    ReadAsync
  step 4 – chunks                                          LookupAsync (batch)

ls
  entry point         ResolveAsync
  prefix nav                            ReadAsync
  per directory                         ReadAsync          LookupAsync (batch,
                                                            sizes only)
```

### archive — end-of-pipeline ordering

The order is deliberate:

1. `FileTreeService.ValidateAsync` — epoch check first. If a mismatch is
   detected, L2 shard files are wiped and L1 is cleared *before* the flush.
   This prevents stale local shard data from overwriting newer remote shards.
2. `ChunkIndexService.FlushAsync` — merges and uploads pending shard entries
   with fresh data from Azure.
3. `FileTreeBuilder.BuildAsync` — calls `ExistsInRemote` and `WriteAsync` per
   tree node. After `ValidateAsync`, existence checks are pure disk lookups.
4. `SnapshotService.CreateAsync` — records the new snapshot. The timestamp
   written to disk becomes the next epoch baseline.

### restore and ls — read-only consumers

Neither command calls `ValidateAsync`, `FlushAsync`, `CreateAsync`, or
`AddEntry`. They consume the caches but never write to the repository.
Neither creates a blob container.

### Cross-run cache warm-up

Because `FileTreeService.ReadAsync` always writes to the disk cache on a miss,
caches warm organically:

| Sequence | Effect |
|---|---|
| archive → ls | ls finds all tree blobs already cached |
| archive → restore | restore finds all tree blobs already cached |
| ls → restore | restore benefits from ls's cache population |
| archive → archive (same machine) | fast-path epoch match; no remote listing |
| archive (machine A) → archive (machine B) | one `ListAsync` prefetch on mismatch, then full cache rebuild |
