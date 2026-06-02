# Caching Architecture

## Overview

Shared singleton services own repository data access and local caching. They sit
between the feature handlers and Azure Blob Storage, eliminating redundant
network calls across and within runs.

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
         ├── ChunkIndexService     L1 memory LRU
         │                         L2 disk    ↔  Azure chunk-index/
         │                         L3 Azure

         └── ChunkStorageService              ↔  Azure chunks/
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
  creates empty marker files for any not yet on disk, and returns a snapshot
  mismatch result. During archive, the handler responds by calling
  `ChunkIndexService.InvalidateCaches()` before flushing pending shard entries.

The slow path runs at most once per archive run and makes the disk cache
complete for existence checks, so all subsequent `ExistsInRemote` calls are
pure local file-system lookups.

---

## ChunkIndexService

**What it caches:** `ShardEntry` records — the mapping from a file's content
hash to its storage chunk hash, original size, and compressed size. This is the
deduplication index.

Entries are grouped into *shards* by the first two hex characters of the
content hash (256 shards possible). Each shard is a dictionary of all
entries sharing that prefix.

**Three-tier cache:**

| Tier | Location | Notes |
|---|---|---|
| L1 — memory LRU | Process heap | Byte-budget eviction (default 512 MB). Thread-safe via lock + linked list. |
| L2 — disk | `~/.arius/{account}-{container}/chunk-index/{prefix}` | One file per shard prefix. Plain serialized bytes. Corrupt files are deleted and treated as misses. |
| L3 — Azure | `chunk-index/{prefix}` blob | Authoritative source. A hit populates L2 and L1. |

`ChunkIndexService` is a write-back cache with a write buffer. It avoids
per-upload remote shard writes, but still makes newly uploaded chunks visible to
subsequent lookups before the buffered entries are flushed into shard files.

**Two auxiliary structures (memory, session-scoped):**

- `_sessionEntries` — newly recorded `ShardEntry` values that may not be
  persisted to any shard yet. Checked before L1/L2/L3, so lookups can see chunks
  uploaded earlier in the same service lifetime even before `FlushAsync` runs.
  This dictionary is not bounded by `DefaultL1CacheBudgetBytes`; it is cleared
  after a successful `FlushAsync` or `RepairAsync`.
- `_pendingEntries` — accumulates new `ShardEntry` values to merge into shards.
  Flushed in bulk at end-of-run by `FlushAsync`.

```mermaid
flowchart TD
    A[LookupAsync content hash] --> B{In _sessionEntries?}
    B -->|yes| C[Return session entry]
    B -->|no| D[Shard prefix]
    D --> E{L1 memory shard cache}
    E -->|hit| F[Lookup entry in shard]
    E -->|miss| G{L2 disk shard file}
    G -->|hit| H[Deserialize shard]
    H --> I[Promote shard to L1]
    I --> F
    G -->|miss or corrupt| J{L3 Azure chunk-index shard}
    J -->|hit| K[Download and deserialize shard]
    K --> L[Save to L2]
    L --> I
    J -->|missing| M[Empty shard]
    M --> I
    F --> N{Entry found?}
    N -->|yes| O[Return shard entry]
    N -->|no| P[Miss]

    Q[AddEntry after chunk upload] --> R[_sessionEntries]
    Q --> S[_pendingEntries]
    S --> T[FlushAsync]
    T --> U[Load current shard through L1/L2/L3]
    U --> V[Merge pending entries]
    V --> W[Upload merged shard to Azure]
    V --> X[Save merged shard to L2]
    V --> Y[Promote merged shard to L1]
    W --> Z[Clear _pendingEntries and _sessionEntries]
    X --> Z
    Y --> Z
```

`_sessionEntries` and L1 overlap deliberately but represent different states:

- `_sessionEntries` is a visibility overlay for entries recorded after upload
  but before they have been merged into chunk-index shards. In cache terminology,
  it is the write buffer for this write-back cache.
- L1 is the bounded cache for whole materialized shards loaded from L2/L3 or
  promoted after `FlushAsync` writes merged shard state. A shard is the cache
  page.
- Before `FlushAsync`, L1 may still hold an older shard that does not contain a
  new entry. `LookupAsync` must check `_sessionEntries` first to avoid missing
  newly uploaded chunks.
- During `FlushAsync`, pending entries are merged into the current shard, then
  saved to L2 and promoted to L1. Only after that succeeds are `_pendingEntries`
  and `_sessionEntries` cleared.
- `DefaultL1CacheBudgetBytes` applies only to L1 shard objects. It does not limit
  `_sessionEntries` or `_pendingEntries`.

This is intentionally not implemented as a dirty flag on L1 shard pages. A newly
uploaded entry may belong to a shard that is not currently loaded in L1. Marking
that shard dirty would require loading the full shard during `AddEntry`, making
upload workers perform shard-cache I/O and weakening flush batching. The write
buffer keeps `AddEntry` cheap; `FlushAsync` is the point where buffered entries
are merged into full shard pages and made clean in L1/L2/L3.

**Key operations:**

- `LookupAsync` — checks `_sessionEntries` first, then resolves misses through
  the shared L1 → L2 → L3 shard cache.
- `AddEntry` — adds a newly uploaded chunk to `_sessionEntries` and
  `_pendingEntries` immediately after upload.
- `FlushAsync` — merges pending entries into existing shards, uploads merged
  shards to Azure, saves to L2, promotes to L1, then clears `_pendingEntries`
  and `_sessionEntries`.
- `InvalidateCaches` — clears L2 and L1. Called by `ArchiveCommandHandler` when
  `FileTreeService.ValidateAsync` reports a snapshot mismatch, so stale shard
  files and objects are not used during the following flush.
- `InvalidateL1` — clears only the in-memory LRU. Used when L2 has already been
  cleared by another code path.

`ArchiveCommandHandler` also keeps its own per-run `inFlightHashes` set during
dedup routing. That is the queued-upload guard that prevents duplicate uploads
before upload workers call `AddEntry`; it is separate from `_sessionEntries`.

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
   detected, `ArchiveCommandHandler` calls `ChunkIndexService.InvalidateCaches()`
   before the flush. This prevents stale local shard data from overwriting newer
   remote shards.
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
