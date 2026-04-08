# Filetree and Chunk-Index Caching Design

This document captures the key design insights for introducing a shared cache
layer across the archive, ls, and restore commands.  The parallelisation of the
flush/upload phases is a separate concern and is not covered here.

---

## Current architecture (before)

### Archive end-of-pipeline

After all chunks are uploaded, the archive handler runs two sequential phases
with **zero progress indicators**:

```
1. ChunkIndexService.FlushAsync()
   foreach shard in dirtyShards:
     LoadShardAsync(prefix)  → L1 (memory LRU) → L2 (disk) → L3 (Azure)
     Merge pending entries
     SerializeAsync + UploadAsync
     SaveToL2 + PromoteToL1

2. TreeBuilder.BuildAsync(manifestPath)
   Read sorted manifest
   Build Merkle tree bottom-up (hash each directory's children)
   foreach directory (bottom-up):
     EnsureUploadedAsync(hash, treeBlob):
       Disk cache hit?  → skip          (trusts local file existence)
       GetMetadataAsync → remote exists? → save to disk, skip
       Else: serialize + upload + save to disk cache

3. SnapshotService.CreateAsync(rootHash, ...)
```

The chunk-index has a three-tier cache:

| Tier | Storage      | Key            | What's stored                       |
|------|-------------|----------------|-------------------------------------|
| L1   | Memory LRU  | 4-char prefix  | Deserialized `Shard` object         |
| L2   | Disk         | 4-char prefix  | Plaintext serialised shard bytes    |
| L3   | Azure Blob   | 4-char prefix  | Gzip + optionally encrypted bytes   |

The filetree has a **disk-only** cache:

| Tier | Storage | Key       | What's stored                           |
|------|--------|-----------|-----------------------------------------|
| Disk | Local  | tree hash | Plaintext JSON (no gzip, no encryption) |

### ls command

`ListQueryHandler.WalkDirectoryAsync` downloads every tree blob directly from
Azure on every invocation — no disk cache, no memory cache:

```csharp
var blobName = BlobPaths.FileTree(treeHash);
await using var stream = await _blobs.DownloadAsync(blobName, cancellationToken);
var treeBlob = await TreeBlobSerializer.DeserializeFromStorageAsync(stream, ...);
```

This is repeated recursively for every directory in the tree.

### restore command

`RestoreCommandHandler.WalkTreeAsync` does the same — direct download per tree
blob with no caching:

```csharp
var blobName = BlobPaths.FileTree(treeHash);
await using var stream = await _blobs.DownloadAsync(blobName, cancellationToken);
var treeBlob = await TreeBlobSerializer.DeserializeFromStorageAsync(stream, ...);
```

---

## Key insight: filetree blobs are content-addressed (immutable)

A filetree blob's name **is** its hash.  If a file exists in the disk cache
under that hash, its content is correct **by definition**.  Cache entries can be
*incomplete* (the cache doesn't have a blob yet) but they can never be *wrong*.

This means:
- `ReadAsync` (used by ls/restore) can **always** trust a disk cache hit,
  regardless of who wrote the blob or when.
- The only question that requires remote validation is "does this hash exist in
  Azure?" — and that question is only asked during **archive** (to avoid
  re-uploading an already-uploaded blob).

---

## Proposed design: TreeCacheService

A single shared service replaces the ad-hoc caching in `TreeBuilder` and the
uncached downloads in `ListQueryHandler` and `RestoreCommandHandler`.

### API surface

```
TreeCacheService
  ├── ReadAsync(hash)            → TreeBlob   (read-through: disk → Azure → disk)
  ├── ExistsInRemote(hash)       → bool       (archive only, uses epoch logic)
  ├── WriteAsync(hash, treeBlob) → void       (serialize + upload + save to disk)
  └── UpdateEpoch(timestamp)     → void       (after successful snapshot)
```

### How each command uses the service

```
┌──────────┬──────────────────────────────────────────────────┐
│ archive  │ ExistsInRemote(hash) → skip upload if true       │
│          │ WriteAsync(hash, tree) → upload new tree blobs   │
│          │ UpdateEpoch(timestamp) → after snapshot created  │
├──────────┼──────────────────────────────────────────────────┤
│ ls       │ ReadAsync(hash) → read-through, feeds disk cache │
├──────────┼──────────────────────────────────────────────────┤
│ restore  │ ReadAsync(hash) → read-through, feeds disk cache │
└──────────┴──────────────────────────────────────────────────┘
```

Note: ls and restore **never** call `ExistsInRemote`.  They only read tree
blobs.  The prefetch HashSet (see below) is never populated by these commands.

### ReadAsync (ls, restore, and archive hash-computation phase)

```
ReadAsync(hash):
  1. Check disk cache (~/.arius/cache/<account>/<container>/filetrees/<hash>)
     HIT  → deserialize and return (always correct — content-addressed)
     MISS ↓
  2. Download from Azure: _blobs.DownloadAsync(BlobPaths.FileTree(hash))
  3. Deserialize (decompress + decrypt if needed)
  4. Save plaintext JSON to disk cache
  5. Return
```

Every read — whether from archive's tree-build phase, ls walking the directory
tree, or restore collecting files — populates the disk cache **organically**.
A subsequent ls after an archive benefits from the cache the archive left
behind. A restore after an ls benefits similarly.

### ExistsInRemote (archive only)

This method answers "has this tree blob already been uploaded?".  It is only
called during archive's tree-build phase and replaces the current
`EnsureUploadedAsync`'s remote HEAD check.

The answer depends on the **cache epoch** — a mechanism to decide whether the
local disk cache is trustworthy for existence checks.

---

## Cache-epoch mechanism

### The problem

On a fresh machine (or after someone else archived to the same container from a
different machine), the local disk cache is empty or stale.  A disk cache miss
could mean either:
- The blob genuinely doesn't exist (needs upload), or
- The blob exists remotely but we just don't have it cached locally.

Currently `EnsureUploadedAsync` solves this with a HEAD request
(`GetMetadataAsync`) per tree blob — one HTTP round-trip per directory.  At 15K
directories that's ~25 minutes of sequential HTTP.

### The solution: epoch tracking

Store a **cache-epoch file** locally:
`~/.arius/cache/<account>/<container>/filetrees/.epoch`

This file contains the timestamp of the last snapshot that **this machine**
participated in creating.

```
┌─────────────────────────────────────────────────────────────────┐
│  On archive start:                                              │
│                                                                 │
│  1. Fetch latest snapshot timestamp from Azure                  │
│  2. Read local .epoch file                                      │
│  3. Compare:                                                    │
│     ├─ MATCH  → fast path: we were the last writer              │
│     │   • Trust disk cache completely                           │
│     │   • Cache miss = genuinely new, needs upload              │
│     │   • Skip ListAsync prefetch entirely                      │
│     │                                                           │
│     └─ MISMATCH → slow path: someone else wrote last            │
│        • Run ListAsync("filetrees/") once → collect all         │
│        │  blob names into HashSet<byte[]> (~20 bytes/entry)     │
│        • Invalidate chunk-index L2 disk cache                   │
│        • Cache miss + in HashSet = exists remotely, save to     │
│        │  disk cache, skip upload                               │
│        • Cache miss + not in HashSet = genuinely new            │
│                                                                 │
│  After successful snapshot:                                     │
│  4. Write new snapshot timestamp to .epoch file                 │
└─────────────────────────────────────────────────────────────────┘
```

### Why HashSet<byte[]> and not HashSet<string>

At 1M-entry scale, there could be ~100K unique tree blobs.  Each tree hash is a
hex string of ~64 chars = 128 bytes as a .NET string (plus object overhead).
Using the raw 32-byte hash in a `byte[]` with a custom `IEqualityComparer`
roughly halves memory usage.

The `knownRemote` HashSet is **lazy** — it's only allocated and populated when
the epoch mismatches and `ExistsInRemote` is first called.  The ls and restore
commands never trigger this allocation.

### ExistsInRemote logic

```
ExistsInRemote(hash):
  1. Disk cache hit → return true
  2. If epoch MATCH (fast path):
     → return false  (cache miss = genuinely new)
  3. If epoch MISMATCH (slow path):
     a. Ensure knownRemote HashSet is populated (lazy ListAsync)
     b. hash in knownRemote?
        YES → save to disk cache (download blob), return true
        NO  → return false (genuinely new)
```

### Chunk-index L2 invalidation on epoch mismatch

When the epoch mismatches, another machine may have uploaded new chunk-index
shards to Azure.  The L2 disk cache may contain stale versions of those shards.
On epoch mismatch, delete the L2 directory to force fresh loads from Azure.

(The L1 in-memory cache is always empty at process start, so it's unaffected.)

---

## Full pipeline picture

```
┌───────────────────────────────────────────────────────────────┐
│  Archive end-of-pipeline                                      │
│                                                               │
│  1. Fetch latest snapshot timestamp                           │
│  2. Compare with local cache-epoch                            │
│     ├─ MATCH:  fast path (trust caches)                       │
│     └─ MISMATCH: slow path                                    │
│        ├─ prefetch filetree names → HashSet<byte[]>           │
│        └─ invalidate chunk-index L2                           │
│                                                               │
│  3. In parallel:                                              │
│     ├─ Chunk-index flush (shards @ N workers)                 │
│     │  → progress: "Updating file metadata: N/M shards"       │
│     │                                                         │
│     └─ Tree build                                             │
│        ├─ compute hashes bottom-up (CPU, ~ms)                 │
│        │  for each dir: serialize → hash → ExistsInRemote?    │
│        │    exists  → skip                                    │
│        │    new     → add to pendingUploads list               │
│        └─ upload pendingUploads @ N workers                   │
│           → progress: "Uploading directory structure: N/M"    │
│                                                               │
│  4. Create snapshot → update cache-epoch                      │
└───────────────────────────────────────────────────────────────┘
┌───────────────────────────────────────────────────────────────┐
│  ls                                                           │
│                                                               │
│  ListQueryHandler.WalkDirectoryAsync:                         │
│    for each directory in the tree (recursive):                │
│      TreeCacheService.ReadAsync(treeHash)                     │
│        disk cache HIT  → deserialize, return                  │
│        disk cache MISS → download → save to cache → return    │
│      merge cloud entries with local directory snapshot         │
│      yield RepositoryEntry results                            │
│                                                               │
│  No prefetch, no epoch check.                                 │
│  Cache gets populated organically for future runs.            │
└───────────────────────────────────────────────────────────────┘
┌───────────────────────────────────────────────────────────────┐
│  restore                                                      │
│                                                               │
│  RestoreCommandHandler.WalkTreeAsync:                         │
│    for each directory in the tree (recursive):                │
│      TreeCacheService.ReadAsync(treeHash)                     │
│        disk cache HIT  → deserialize, return                  │
│        disk cache MISS → download → save to cache → return    │
│      collect FileToRestore entries                            │
│                                                               │
│  No prefetch, no epoch check.                                 │
│  If ls was run first, most/all tree blobs are already cached. │
└───────────────────────────────────────────────────────────────┘
```

### Cross-command cache benefits

The disk cache creates a virtuous cycle across commands:

| Sequence                | Effect                                              |
|-------------------------|-----------------------------------------------------|
| archive → ls            | ls finds all tree blobs in cache (archive wrote them) |
| archive → restore       | restore finds all tree blobs in cache                 |
| ls → restore            | restore benefits from ls's cache population           |
| archive₁ → archive₂    | epoch match: skip all remote checks, near-instant     |
| archive (machine A) → archive (machine B) | epoch mismatch: one ListAsync prefetch, then full cache rebuild |

---

## Scale estimates

At **1M chunks / 550 GiB** (the largest archive):

| Metric                             | Estimate           |
|------------------------------------|--------------------|
| Unique tree blobs                  | ~100K              |
| Disk cache size (plaintext JSON)   | ~50–100 MB         |
| HashSet<byte[]> memory (prefetch)  | ~3 MB              |
| ListAsync("filetrees/") time       | ~5–10 sec          |
| Epoch-match archive finalization   | seconds (I/O only for new blobs) |
| Epoch-mismatch archive finalization| +10 sec for prefetch |
| ls with warm cache                 | milliseconds (pure disk I/O) |
| ls with cold cache                 | ~same as today (downloads, but caches for next time) |
