User: /opsx-explore #file:REQUIREMENTS.md 

GitHub Copilot: 

 

Ran terminal command:  cat openspec/config.yaml

Ran terminal command:  ls -la openspec/changes/ openspec/specs/

Fresh slate — no changes, no specs yet. Just the requirements doc. Great, let's think.

---

I've read through your requirements carefully. This is a meaty system with some genuinely interesting design tensions. Let me surface what I see, then let's pull on threads.

## The Big Picture

```
┌─────────────────────────────────────────────────────────┐
│                     Arius System                        │
│                                                         │
│  ┌───────────┐   ┌────────────┐   ┌──────────────────┐ │
│  │ Arius.Cli │──▶│ Arius.Core │──▶│ Arius.AzureBlob  │ │
│  │ (UI/CLI)  │   │ (Mediator) │   │ (Storage Backend) │ │
│  └───────────┘   └─────┬──────┘   └──────────────────┘ │
│                         │                                │
│              ┌──────────┴──────────┐                    │
│              │    Domain Model     │                    │
│              ├─────────────────────┤                    │
│              │ Snapshots           │                    │
│              │ Chunks              │                    │
│              │ Pointer Files       │                    │
│              │ State / Index       │                    │
│              └─────────────────────┘                    │
└─────────────────────────────────────────────────────────┘
```

## The Elephant in the Room: State Storage

You flagged this yourself and I think it's the single most consequential design decision. Let me lay out why:

```
Scale scenario: 1 TB of 2 KB files = ~500 million files
Each file needs: relative path + content hash (SHA-256)
                  ~256 bytes avg path + 32 bytes hash ≈ 288 bytes/entry
                  × 500M = ~134 GB of raw index data
```

Wait — that's massive. Even with compression, a SQLite database at this scale would be tens of GB. You're right to reject downloading it on every invocation. But this constraint shapes *everything*:

- How does `archive` check "is this content already in the archive"?
- How does `ls` list files in a snapshot?
- How does `restore` map paths to chunk hashes?

**Three possible architectures I see:**

### Option A: Snapshot-as-File (one blob per snapshot)

Each snapshot is a compressed+encrypted file that maps `{path → content_hash}`. At 500M entries, a single snapshot is ~tens of GB compressed. You'd need to download and parse it for any operation. This is what you had with SQLite — same problem.

### Option B: Sharded Index (partitioned by path prefix or hash prefix)

```
state/
  snapshots/
    2026-03-21T14:00:00Z/
      manifest.enc          ← metadata (timestamp, stats)
      index-00.enc          ← entries for hash prefix 00*
      index-01.enc          ← entries for hash prefix 01*
      ...
      index-ff.enc          ← entries for hash prefix ff*
```

For `archive` (dedup check): hash a file → look up the shard for that hash prefix → download just that shard. At 500M entries / 256 shards ≈ 2M entries per shard ≈ ~550 MB each. Still heavy, but you only download what you need.

For `ls`: harder — you'd need path-based partitioning for that.

### Option C: Bloom Filter + Sharded Details

```
state/
  snapshots/
    2026-03-21T14:00:00Z/
      bloom.enc             ← bloom filter: "is this hash in the archive?"
      manifest.enc          ← snapshot metadata
  index/
    chunks/
      <hash-prefix>/       ← which chunks contain which content hashes
    paths/
      <path-prefix>/       ← path → hash mappings, sharded by path prefix
```

The bloom filter answers "is this content already archived?" with zero false negatives and configurable false positive rate. At 500M entries with 1% false positive rate, a bloom filter is ~570 MB. With 0.1% it's ~850 MB. Still large, but it's a one-time download and works as a fast gate — only on a "maybe yes" do you hit the sharded index.

**But here's the rub**: the bloom filter needs to span *all* snapshots (it's the global "does this content hash exist anywhere?"), while the path→hash mappings are per-snapshot.

### Option D: Hash-Prefix Directory Existence Check

Simplest possible: store each chunk as `chunks/<full-hash>`. To check if content exists, just do a `HEAD` request or `GetBlobProperties` on the expected blob name. Azure Blob Storage handles the indexing. No local index needed for dedup.

```
"Is hash abc123... already archived?"
  → HEAD chunks/abc123...
  → 404? Upload it. 200? Skip.
```

For 500M files during archive, that's potentially 500M HEAD requests though. At ~$0.0004/10K operations on Cool tier... that's $20 per full archive run just for existence checks. And latency: even at 1ms per request, 500M sequential checks = 6 days. You'd need massive parallelism.

---

**This is the fork in the road.** Each option has dramatically different implications for:
- Archive speed
- Restore speed
- `ls` performance
- Storage cost
- Bandwidth cost
- Complexity

A few questions that would help narrow this down:

1. **How often do you run `archive`?** Daily? Weekly? Continuously? This determines how many "new files to check" there typically are vs. the full corpus.

2. **Is incremental archive the norm?** i.e., most runs, 99% of files are unchanged and only a few thousand are new? If so, you could maintain a lightweight "known hashes" bloom filter locally as a cache, only rebuilt from blob storage when lost.

3. **For `ls` — how interactive does it need to be?** Is it acceptable to download a snapshot manifest first (could be large), or does it need to be instant?

4. **The 500M files scenario — is that a real target or an aspirational ceiling?** Because the design for 10M files is very different from 500M.

What's your gut feeling on these? And which operations do you want to be *fast* vs. which can be *cheap*?

User: 1. assume weekly
2. incremental is the norm. my remark with the local knowledge is especially to optimize during the archive operation
3. ls should feel rather responsive. waiting for a 50 MB download to materialize would be too slow. You can cache the 50 MB state file locally and use that, and/or build the cache gradually (the first time it can be slower) but every time downloading 50 MB is not acceptable
4. real target

GitHub Copilot: 

Excellent. Those answers lock in some important constraints. Let me work through the implications.

## The Pointer File Insight

Your answer to #2 reveals something crucial: **pointer files ARE the local state for archive**. This simplifies the dedup problem enormously.

```
Archive flow (incremental, typical):

  Local filesystem                         Blob Storage
  ┌──────────────────────┐                ┌──────────────┐
  │ photo.jpg             │                │ chunks/      │
  │ photo.jpg.pointer.arius ──"abc123"     │   abc123     │
  │                       │                │   def456     │
  │ video.mp4  (NEW!)     │                │   ...        │
  │                       │                └──────────────┘
  └──────────────────────┘

  Step 1: Walk files
  Step 2: photo.jpg → hash it → "abc123"
          pointer file says "abc123" → match → SKIP ✓
  Step 3: video.mp4 → hash it → "xyz789"
          no pointer file → NEW FILE → upload
  Step 4: Only for NEW files: HEAD chunks/xyz789 → 404 → upload
                               (or 200 → already archived by another path, just create pointer)
```

In a typical weekly run with millions of files but only thousands changed, you:
- Hash all files (parallelized, CPU-bound, fast)
- Compare with pointer files (local filesystem, instant)
- Only hit blob storage for genuinely new content

**No bloom filter needed for the common case.** The pointer files are a more efficient local cache than any probabilistic data structure — they're 100% accurate and already on disk.

The bloom filter only helps when:
- First archive on a new machine (no pointer files)
- Mass pointer file corruption
- Verifying archive integrity

That's an optimization you can defer. Nice.

## The Snapshot Problem: A Git-Style Merkle Tree

Here's where it gets interesting. For `ls` and `restore`, you need the snapshot — the full mapping of `{path → content_hash}`. At 500M files, that's enormous as a flat file. But you said `ls` must be responsive and downloading 50+ MB every time is unacceptable.

**Proposal: store snapshots as a content-addressed directory tree.** Exactly like Git's tree objects.

```
snapshots/
  2026-03-21T140000Z/
    manifest.enc                ← tiny: root tree hash + metadata
  
trees/
  <tree-hash-A>.enc            ← root directory listing
  <tree-hash-B>.enc            ← /photos/ listing  
  <tree-hash-C>.enc            ← /photos/2024/ listing
  ...
```

Each tree blob is a directory listing:
```
┌────────────────────────────────────────────────────────────┐
│ Tree: <tree-hash-A>  (root directory)                      │
├────────────────────────────────────────────────────────────┤
│ Name          │ Type │ Hash          │ Chunk Hash          │
│───────────────┼──────┼───────────────┼─────────────────────│
│ photos/       │ tree │ <tree-hash-B> │                     │
│ documents/    │ tree │ <tree-hash-D> │                     │
│ readme.txt    │ file │ abc123...     │ abc123...  (large)  │
│ notes.txt     │ file │ def456...     │ bundle-77... (tar)  │
└────────────────────────────────────────────────────────────┘
```

### Why this works at 500M scale

**`ls /photos/2024/` (directory listing):**
```
manifest.enc  →  root tree hash     (1 small download)
root tree     →  find "photos"      (1 small download)  
photos tree   →  find "2024"        (1 small download)
2024 tree     →  list contents      (1 small download)
                                     ─────────────────
                                     4 tiny downloads, <1 sec
```

**Caching:** Once you've downloaded a tree blob, cache it locally. Content-addressed = immutable = cache forever. After a few `ls` commands you've cached most of the hot paths and it's instant.

**Incremental snapshots:** If `/photos/2024/` didn't change between weeks, it has the same tree hash → the tree blob already exists in storage → no upload needed. Only changed directories produce new tree blobs. A weekly run touching 10K files might only produce a few hundred new tree blobs.

**Shared across snapshots:** Tree blobs live in a shared `trees/` namespace, not per-snapshot. Two snapshots pointing to the same directory get deduplication for free.

### The math

Assume 500M files across 5M directories (average 100 files per directory):
- Average tree blob: 100 entries × ~80 bytes = ~8 KB
- 5M tree blobs × 8 KB = ~40 GB total
- But compressed + encrypted: maybe 10-15 GB total across all snapshots
- Each individual tree blob: compressed ~2-3 KB. Tiny downloads.

### The one hard case: full-text filename search

`ls --grep vacation` (find "vacation" anywhere in any path) requires traversing the entire tree. That's 5M tree blob downloads on a cold cache. Unacceptable.

Options:
1. **Accept it's slow on first run, fast on subsequent** — Full tree traversal populates local cache. Second search is instant from cache.
2. **Maintain a flat name index** — A separate compressed blob listing all `path → hash` entries, sharded. Downloaded once, cached locally.
3. **Build search incrementally** — As user browses with `ls`, the local cache grows. Search only works over cached portions until the full index is built.

I'd lean toward **(1) with a background prefetch**: when a user first interacts with a snapshot, start downloading the full tree in the background. `ls /path` is instant (just fetches needed tree nodes). By the time they want a search, the cache may already be warm.

Or a practical hybrid: build the flat name index only when requested, cache it, and keep using the tree for directory listings.

## The Tar Bundling Question

This is a secondary but important design choice. Small files get bundled into tars. How?

```
Strategy A: Bundle by directory
  /photos/2024/jan/ → all small files in one tar chunk
  
  ✓ Restoring a directory = fewer chunk downloads
  ✗ Dir with 100K tiny files = one huge chunk
  ✗ Adding one file to a dir = need to re-bundle? Or create a new chunk with just the new files?

Strategy B: Bundle by size threshold (e.g., 64 MB)
  Fill a tar until it reaches 64 MB, seal, encrypt, upload
  
  ✓ Predictable chunk sizes
  ✓ Simple streaming implementation
  ✗ One dir's files may span many chunks
  ✗ Files from different dirs in same chunk

Strategy C: Hybrid — bundle by directory, split if too large
  Group by directory, but cap at eg. 64 MB
  
  ✓ Best locality for restore
  ✓ Bounded chunk size
  ✗ More complex bundling logic
```

With archive tier pricing, you pay per rehydration request regardless of size. So fewer, larger chunks = cheaper. But there's a floor: you're downloading 64 MB to get 2 KB if you just want one file.

**A question for you:** When you restore, is it typically:
- Individual files ("give me that one photo")
- Directories ("give me everything from 2024")
- Full snapshots ("give me everything")

This determines whether directory locality matters for bundling.

## The Full Container Layout

Here's what's crystallizing:

```
Container: arius
├── chunks/                              ← Archive/Hot/Cool/Cold tier
│   ├── <content-hash>                   ← single large file (gzip+encrypt)
│   └── <bundle-hash>                    ← tar of small files (tar+gzip+encrypt)
│
├── chunks-rehydrated/                   ← Hot tier, temporary
│   └── <hash>                           ← rehydrated copies for restore
│
├── trees/                               ← Cool tier
│   └── <tree-hash>.enc                  ← directory tree nodes (encrypted)
│
├── snapshots/                           ← Cool tier
│   └── 2026-03-21T140000Z.enc          ← manifest: root tree hash + metadata
│
└── state/                               ← Cool tier (optional/future)
    └── bloom.enc                        ← global bloom filter for archive optimization
```

## Open Questions I'm Tracking

1. **Tar bundling strategy** — depends on your restore patterns
2. **Pointer file format** — needs content hash + chunk hash (different for tar-bundled files). Is just the content hash enough for the common case? Or should it always include the chunk hash for tar'd files?
3. **Tree blob encryption** — encrypting each tree blob individually means you can download and decrypt just what you need. But it's 5M small encrypted blobs. Is the per-blob encryption overhead acceptable, or should tree nodes be batched?
4. **Snapshot GC** — When you delete an old snapshot, how do you know which chunks and tree blobs are still referenced by other snapshots? Reference counting? Mark-and-sweep?

What's pulling your attention? The tar bundling? The tree structure details? Something else entirely?

User: it is possible that i make changes to the local file system (file + its pointer file) eg by renaming it / duplicating it. when i archive it, that should be captured as well in the snapshot
it can also be that all pointer files are gone, they then should be rebuilt

re the git style merkle tree, ok as long as what is stored on azure blob storage is not leaking anyhting about the repository contents (eg. file names of folder structure)

full text filename search: i ll accept that the first time it is slow. i can always add a flat name index later i assume?

re TAR - use strategy B. the tar chunk name should be the hash of the TAR file (to be consistent with the chunks for regular large files)


with the `ls`, i ll also want to see the original size, date created, date modified. In the snapshot this metadata should also be stored.

3. i dont undertand the tree blob encryption question. 

4. assume snapshots are never deleted

GitHub Copilot: 

Good, these are strong constraints that simplify the design. Let me work through the implications.

## Pointer Files: A Cache, Not the Truth

This is now clear:

```
Pointer file is:
  ✓ A local performance optimization for archive
  ✓ Derivable from the snapshot (rebuildable)
  ✗ NOT the source of truth for what's in the archive
```

**Rebuild scenario:** `arius archive` on a machine with no pointer files:
1. Download latest snapshot manifest → root tree hash
2. Traverse the tree, decrypt, build local path→hash map
3. Walk local filesystem, hash each file
4. Compare against the snapshot's path→hash map
5. Recreate pointer files for all matching files
6. Upload new/changed files

This is the "slow first run" — but it only happens once, and then pointer files exist for subsequent runs.

**Rename scenario:**
```
Before:                           After rename:
  photo.jpg                        vacation.jpg  
  photo.jpg.pointer.arius          vacation.jpg.pointer.arius
    → "abc123"                       → "abc123"

Snapshot v1:  photo.jpg → abc123
Snapshot v2:  vacation.jpg → abc123    (chunk already exists, no re-upload)
```

The chunk is content-addressed — renames are free. Duplicates are free. Only the snapshot captures the new path.

## Encrypted Tree: What I Meant by Q3

Let me clarify. The concern wasn't *whether* to encrypt — everything is encrypted. The question was about **granularity**.

```
Option A: One encrypted blob per tree node
  trees/
    a7f3e2.enc    ← encrypted: listing of root dir (3 KB)
    b8c4d1.enc    ← encrypted: listing of /photos/ (3 KB)
    c9d5e2.enc    ← encrypted: listing of /photos/2024/ (3 KB)

Option B: Batch tree nodes into larger encrypted blobs
  trees/
    batch-001.enc  ← encrypted: 1000 tree nodes packed together (300 KB)
    batch-002.enc  ← encrypted: next 1000 tree nodes (300 KB)
```

Option A means 5M tiny encrypted blobs (at 500M file scale). Each stands alone. Random access is perfect. But 5M blobs = 5M blob storage transactions to build the snapshot + per-blob encryption overhead (openssl `Salted__` header + PBKDF2 per blob).

Option B means fewer, larger blobs. Cheaper to store and upload. But to read one tree node, you might download a 300 KB batch when you only need 3 KB.

**Given your constraint that snapshots are never deleted**, I'd go with **Option A** for simplicity. The blobs are immutable and permanent. The per-blob overhead is real but manageable — and the random access property is exactly what makes `ls` fast. For 5M blobs at Cool tier, storage is cheap and the transaction cost of creating them is a one-time expense per weekly run (and only new/changed tree nodes are created).

**On the "not leaking" requirement:** With Option A, an attacker who has access to the storage account (but not the passphrase) would see:

```
What they see:                      What they learn:
─────────────────────────────────── ───────────────────────────────
trees/a7f3e2.enc (2.8 KB)          "Some tree node exists"
trees/b8c4d1.enc (3.1 KB)          "Some tree node exists"  
chunks/deadbeef... (45 MB)          "Some content blob exists"
snapshots/2026-03-21T140000Z.enc   "A snapshot was taken on this date"
```

They see: opaque hashes as blob names, encrypted contents, blob sizes, and blob count. They **cannot** infer: filenames, directory structure, which tree node is a parent of which (since the references inside are encrypted), or any content. The tree hash is computed from the *encrypted* representation? Actually no — let me think about this more carefully.

## The Tree Hash: A Subtle Leaking Risk

Here's the subtlety. If the tree hash is a hash of the *plaintext* tree content (path names, child hashes), then:

```
Two users with the same directory structure but different passphrases:
  User A: trees/a7f3e2.enc  ← hash("photos/ → tree:xxx, docs/ → tree:yyy")
  User B: trees/a7f3e2.enc  ← SAME hash, different encryption

  An attacker comparing two repos could infer they have the same structure.
```

**Fix: seed tree hashes with the passphrase too** — same as you do for content hashes. Then identical directory structures produce different tree hashes under different passphrases. This also prevents someone from building a "rainbow table" of known directory structures.

```
tree_hash = SHA256(passphrase + serialize(entries))
```

With this, the blob name is opaque and passphrase-dependent. No structural leakage.

## Updated Tree Node Format

With the metadata requirement for `ls`:

```
Tree Node (plaintext, before encryption):
┌────────────────────────────────────────────────────────────────┐
│ Entry[] entries                                                │
├─────────┬──────┬──────────┬───────────┬───────────┬───────────┤
│ Name    │ Type │ Hash     │ Size      │ Created   │ Modified  │
├─────────┼──────┼──────────┼───────────┼───────────┼───────────┤
│ photos/ │ tree │ <tree-h> │           │ 2024-01-1 │ 2024-06-1│
│ foo.txt │ file │ <cont-h> │ 1,234     │ 2023-05-2 │ 2024-03-1│
│ bar.jpg │ file │ <cont-h> │ 8,456,789 │ 2024-01-1 │ 2024-01-1│
└─────────┴──────┴──────────┴───────────┴───────────┴───────────┘

For files:  hash = content hash (SHA256 seeded w/ passphrase)
For dirs:   hash = tree hash (SHA256 seeded w/ passphrase of serialized entries)
```

For tar-bundled small files, `ls` only needs the content hash + metadata. The mapping from content hash → chunk hash (which tar bundle contains this file) is separate — it's needed at restore time, not at `ls` time.

## Where Does Content Hash → Chunk Hash Live?

This is a mapping we haven't placed yet. For large files, content hash = chunk hash (the chunk is the file). For small files, content hash ≠ chunk hash (the chunk is a tar bundle).

```
"I want to restore foo.txt (content hash: abc123)"
  → abc123 is in tar bundle chunk xyz789
  → Download xyz789, decrypt, un-tar, extract abc123
```

Options:

**A) Store in the tree node itself:**
```
│ Name    │ ContentHash │ ChunkHash   │ OffsetInTar │
│ foo.txt │ abc123      │ xyz789      │ (optional)  │
```
Simple. But now the tree hash changes whenever a file gets re-bundled into a different tar (even if its content didn't change). That breaks the merkle property — unchanged content, different tree hash.

**B) Separate chunk index:**
```
chunk-index/
  <hash-prefix>/
    index.enc          ← content-hash → chunk-hash mappings
```
Clean separation. A content hash always maps to the same chunk hash regardless of which snapshot references it. But it's another set of blobs to manage.

**C) Convention: try content hash as chunk name first, fall back to chunk index:**
For large files, `GET chunks/<content-hash>` succeeds directly. Only for small (tar-bundled) files do you consult the chunk index. Since large files are presumably the majority of data volume, most restores skip the index entirely.

I'd lean **B or C**. Thoughts?

## Summary of Decisions So Far

| Decision | Choice |
|----------|--------|
| Local state for archive dedup | Pointer files (local cache, rebuildable) |
| Snapshot structure | Git-style encrypted merkle tree |
| Tree hash derivation | Passphrase-seeded (no structural leakage) |
| Tree node metadata | Name, type, hash, size, created, modified |
| Tar bundling | Strategy B (fill to size threshold, hash of tar = chunk name) |
| Full-text search | Accept slow first run, flat index deferred |
| Snapshot deletion | Never (no GC needed) |
| Blob encryption | Per tree node (Option A) for random access |

## Still Open

1. **Content hash → chunk hash mapping** — A, B, or C above?
2. **Pointer file format** — just content hash? Or content hash + chunk hash (for faster restore without index lookup)?
3. **Snapshot naming** — `2026-03-21T140000Z` is human-readable and sortable. UTC? Local time? Include sub-second precision to avoid collisions?
4. **Tar size threshold** — what's the boundary between "small file → tar" and "large file → solo chunk"? You mentioned 1 MB as configurable. What about the tar target size — 64 MB? 128 MB?

User: re encrypted tree - Option A seems not efficeint when it comes to latency / cost (downloading 500 M files from blob will take a very long time) so i m leaning towards option B perhaps with a rebalance option when a batch crosses a certain treshold? or would that really make things very complex but you seem to lean towards option A? how long would it realistically take?

ok to seed the tree hashes with the passphrase too

the metadata stored in the snapshould should be exstensible in the future

go for option C, try the content hash first, and if that filas consult the chunk index

pointer file format: just content hash

snapshot naming: ok, do utc and add sub second precision to avoid collisions

tar size treshold: should we reuse the same treshold also for the TAR target size? so both 1 MB?

GitHub Copilot: 

Great questions. Let me dig into the tree blob one — it deserves a real analysis.

## Option A vs B: The Numbers

Let's be honest about Option A at 500M file scale:

```
Scenario: Full tree traversal (first-time search, pointer rebuild)
  5M directories = 5M tree blobs
  
  Azure Blob Cool tier read: ~$0.01 / 10K operations
  Cost: 5M reads = $5  (fine)
  
  Throughput: Azure SDK with connection pooling, ~5K-10K req/sec
  Time:  5M / 5,000 = 1,000 sec ≈ 17 min
         5M / 10,000 = 500 sec ≈ 8 min
  
  Bandwidth: 5M × 3 KB = ~15 GB download
```

**8-17 minutes for a full traversal.** That's the honest number for a cold cache at 500M scale.

But let's also look at what happens for normal operations:

```
Scenario: ls /photos/2024/vacation/
  Depth = 4 tree nodes
  Time: 4 × ~50ms = 200ms  ✓ Instant

Scenario: Weekly archive, 10K files changed across 500 directories
  Download 500 tree nodes (changed dirs + parents) = ~1 sec ✓

Scenario: Full search (cold cache)
  5M tree blobs = 8-17 min (first time)
  Subsequent searches: instant from local cache ✓

Scenario: Pointer file rebuild (new machine, cold)  
  Same as full traversal: 8-17 min (one-time)
```

So Option A is **great** for the common case (path-based `ls`, incremental archive) and **tolerable** for the rare case (full traversal, first-time only).

### Would Option B help?

```
Option B: 5M tree nodes in batches of 5,000 = 1,000 blobs
  Each batch: ~5,000 × 3 KB = 15 MB compressed to ~5 MB
  Full download: 1,000 × 5 MB = 5 GB
  Time: 1,000 requests + download = ~30 sec
```

That's dramatically faster for full traversal. But the tradeoff:

```
ls /photos/2024/vacation/  (4 tree nodes needed)

Option A: Download exactly 4 blobs (12 KB)          ~200ms
Option B: Download 4 batch blobs (20 MB) to get      ~2 sec
          the 4 tree nodes you need
          (or get lucky and they're in the same batch)
```

And the complexity:

```
Option B complications:
  - How do you batch? By tree depth? By hash prefix? Random?
  - When a directory changes, its tree node gets a new hash
    → it now belongs in a different batch
    → do you update the old batch? Create a new one?
    → batches become mutable → lose content-addressability
  - Rebalancing: when a batch crosses a threshold...
    → split it how? Clients with the old batch cached see stale data
    → versioned batch references → more complexity
```

### My Recommendation: Option A with an Optional Full Export

```
trees/
  <tree-hash>.enc           ← individual tree blobs (Option A)

snapshots/
  2026-03-21T140000.000Z.enc   ← manifest: root tree hash + metadata
  
tree-exports/                ← optional, for bulk operations
  2026-03-21T140000.000Z.enc   ← ALL tree nodes for this snapshot, packed
```

- **Normal operations** use the individual tree blobs. Path-based `ls`, incremental archive — all fast.
- **Full traversal** (first-time search, pointer rebuild) downloads the tree export — a single large blob. One download, not 5M requests.
- **The tree export is built during `archive`** — you already have all the tree nodes in memory, just serialize and upload one extra blob.
- **No rebalancing, no batching complexity.** The individual blobs are the truth. The export is a read-optimization.

At 500M files: the tree export would be ~5-15 GB compressed. Still large, but it's a single streaming download — could finish in a few minutes on a decent connection. And it's only needed once; after that, the local cache has everything.

Would that work for you? You get Option A's simplicity for all normal ops, with a fast-path for the rare full-traversal case.

## Tar Size Threshold: Don't Reuse 1 MB

This is important — the two thresholds serve completely different purposes:

```
Small file boundary (1 MB):  "Is this file small enough to tar-bundle?"
Tar target size (??? MB):    "How full should a tar bundle get before sealing?"
```

If both are 1 MB:
```
500M files × 2 KB each = ~500 files per 1 MB tar = 1,000,000 tar chunks

Rehydration at archive tier: $0.02/10K blobs for SetBlobTier
  1M chunks × $0.02/10K = $2 per restore  (ok actually)

BUT: 1M rehydration requests take HOURS to even submit
AND: each rehydration takes 1-15 hours to complete
AND: you're polling 1M blobs for completion status
```

Compare with 64 MB tar target:
```
500M files × 2 KB / 64 MB = ~15,000 tar chunks

15K rehydration requests: manageable
15K downloads: fast
```

**The small file boundary and tar target size should be independent:**

| Parameter | Default | Purpose |
|-----------|---------|---------|
| `--small-file-threshold` | 1 MB | Files below this get tar-bundled |
| `--tar-target-size` | 64 MB | Tar is sealed when it reaches this size |

The 64 MB target means ~32,000 small files per tar at 2 KB average. A restore of one file requires downloading 64 MB to extract 2 KB — that's the tradeoff. But at archive tier, the rehydration cost per blob is the expensive part, not the bandwidth. Fewer blobs = cheaper restore.

You could go lower (16 MB? 32 MB?) to reduce the "download wasted bandwidth" per file, but you'd get more chunks to rehydrate. 64 MB feels like the sweet spot. Configurable if you want to tune later.

## Updated Decision Table

| Decision | Choice |
|----------|--------|
| Tree blob storage | Option A (individual) + optional bulk tree export |
| Tree hash derivation | Passphrase-seeded |
| Tree node metadata | Extensible format (name, type, hash, size, created, modified + future fields) |
| Content hash → chunk hash | Option C (try content hash as chunk name, fall back to chunk index) |
| Pointer file format | Content hash only |
| Snapshot naming | UTC with sub-second: `2026-03-21T140000.000Z` |
| Small file threshold | 1 MB (configurable) |
| Tar target size | 64 MB (configurable, independent from small file threshold) |
| Snapshot deletion | Never |

## Extensible Metadata Format

For the tree nodes, something like a versioned binary format or just JSON before encryption:

```json
{
  "v": 1,
  "entries": [
    {
      "name": "photo.jpg",
      "type": "file",
      "hash": "abc123...",
      "size": 8456789,
      "created": "2024-01-15T10:30:00Z",
      "modified": "2024-03-21T14:00:00Z"
    }
  ]
}
```

Version field lets you add fields later (permissions, extended attributes, etc.) without breaking old snapshots. Old clients ignore unknown fields, new clients handle missing fields with defaults.

Or, to keep it lean at scale — a binary format with a header declaring which fields are present. But JSON + gzip is honestly fine for tree nodes (they compress extremely well) and keeps the "recoverable with open source tools" promise.

## Container Layout (Revised)

```
Container: arius
├── chunks/                              ← Archive tier (configurable)
│   ├── <content-hash>                   ← single large file (gzip+encrypt)
│   └── <tar-hash>                       ← tar bundle of small files (tar+gzip+encrypt)
│
├── chunks-rehydrated/                   ← Hot tier, temporary
│   └── <hash>                           ← rehydrated copies for restore
│
├── trees/                               ← Cool tier
│   └── <tree-hash>.enc                  ← individual directory tree nodes
│
├── tree-exports/                        ← Cool tier
│   └── <snapshot-name>.enc              ← full tree packed for bulk download
│
├── chunk-index/                         ← Cool tier
│   └── <content-hash-prefix>/           ← content-hash → chunk-hash mappings
│       └── index.enc                      (only for tar-bundled files)
│
├── snapshots/                           ← Cool tier
│   └── 2026-03-21T140000.000Z.enc      ← manifest: root tree hash + metadata
│
└── local cache (not in blob storage)
    ├── trees/                           ← cached tree blobs
    ├── chunk-index/                     ← cached chunk index
    └── tree-export/                     ← cached full tree
```

A few things I want to probe further:

1. **Chunk index structure** — With Option C, the chunk index only contains entries for tar-bundled files. At 500M files all under 1 MB, that's 500M entries mapping content-hash → tar-chunk-hash. How should this be sharded? By content-hash prefix (256 shards)? That's ~2M entries per shard.

2. **Tar bundle: which files go together?** With Strategy B (fill to 64 MB), files are bundled in the order they're encountered during the filesystem walk. Is that deterministic enough? If you archive from two different machines, the walk order might differ, producing different tar bundles with different hashes — but the same content. Not a problem for correctness, but prevents dedup of tar bundles across machines. Acceptable?

3. **Are we at a point where this feels solid enough to start formalizing into a proposal/design?** Or are there other threads you want to pull on first?

User: re Option A vs B: The Numbers
how do you know in advance what files are changed across which directories? I think you ll need a full export anyway?

let s go for the original option A. i assume i can add a full export later right?

re Tar size treshold
ok for the two parameters & defaults

re chunk index structure
what do you think? by prefix seems fine?

re tar bundle
i will only be archiving from one machine at a point in time. i assume that there is an index of what hashes are already in TARs, so i dont understand your point 'prevents dedup of tar bundles across machines'?

almost there

GitHub Copilot: 

You caught something important. Let me correct myself.

## Archive Doesn't Download Trees At All

I was wrong about "download 500 tree nodes for changed dirs." For archive, you **build** the tree from local state — you never read the old one:

```
Archive flow:

  Local FS walk
       │
       ▼
  Hash every file (parallel, CPU-bound)
       │
       ▼
  Compare with pointer files (local, instant)
       │
  ┌────┴──────────────────────┐
  │ Unchanged files           │ Changed / new files
  │ (pointer matches)         │ (no pointer or mismatch)
  │                           │
  │ Record: path → hash       │ HEAD chunks/<hash> → exists?
  │                           │   yes → just create pointer
  │                           │   no  → queue for upload
  │                           │         (large: solo, small: tar buffer)
  └────┬──────────────────────┘
       │
       ▼
  Build tree bottom-up from ALL files (local computation only)
       │
       ▼
  For each tree node: compute tree hash
    HEAD trees/<hash> → exists? skip : upload
       │
       ▼
  Upload snapshot manifest (root tree hash + timestamp)
```

The tree is derived from local state. No remote tree download needed. The only remote calls are HEAD requests to check existence (chunks and tree nodes) — and since most tree nodes haven't changed, most HEAD requests return 200 → skip.

**So when is remote tree download needed?**

| Operation | Needs remote tree? |
|-----------|--------------------|
| Archive (with pointer files) | No — built from local state |
| Archive (no pointer files, rebuild) | Yes — need old snapshot to recreate pointers |
| `ls` | Yes — browse remote tree |
| `restore` | Yes — find files to restore |

**Pointer rebuild = the expensive case.** Without pointer files, you still archive correctly (every file gets hashed and compared against remote chunks). But without the old snapshot, you can't tell which files are *already* archived — you'd have to HEAD-check every single file's content hash. At 500M files, that's 500M HEAD requests.

The smarter path: download the latest snapshot's full tree, build a local hash set, then compare locally. That's where the 5M-blob download (Option A, no full export) is the bottleneck. On a fresh machine, pointer rebuild is the one slow operation.

**Can you add a full export later?** Absolutely. Option A stands on its own. The full export is a pure optimization — an additional blob that contains all tree nodes packed together. Adding it later doesn't change the tree blob format, the snapshot format, or anything else. It's just a new blob type in a new prefix.

## Chunk Index: By Prefix

Yes, by content-hash prefix works well:

```
chunk-index/
  00.enc    ← all content-hash → tar-chunk-hash mappings for hashes starting with 00
  01.enc    ← ...
  ...
  ff.enc

At 500M files all being small:
  500M entries / 256 shards = ~2M entries per shard
  Each entry: 32 bytes (content hash) + 32 bytes (tar chunk hash) = 64 bytes
  Per shard: 2M × 64 bytes = ~128 MB uncompressed
             Compressed: ~40-60 MB
```

Hmm, that's big per shard. Let's go finer:

```
chunk-index/
  00/
    00.enc    ← hashes starting with 0000
    01.enc    ← hashes starting with 0001
    ...
    ff.enc    ← hashes starting with 00ff
  01/
    ...

65,536 shards (2 hex bytes = 16 bits)
  500M / 65,536 = ~7,600 entries per shard
  7,600 × 64 bytes = ~475 KB uncompressed
  Compressed: ~150-200 KB per shard
```

That's a much better size. You download one 200 KB shard to find which tar contains your file. And the shards are immutable — once a content hash is mapped to a tar chunk, it never changes (tars are sealed).

Actually — these shards DO grow over time (new tars get new entries). They're not immutable. So either:

**Append-only approach:** New entries go into a per-snapshot overlay. To look up a content hash, check the latest overlay first, then walk backwards. Overlays are small (only new entries per archive run).

**Rewrite approach:** Each archive run produces new shard blobs (replacing old ones). Simpler lookup, but overwrites previous shards.

Since snapshots are never deleted and the index needs to be consistent, I'd go with **rewrite** — each archive run rebuilds the affected shards. At 10K new small files per weekly run, only a handful of shards change.

## Tar Dedup: Clarified

You're right, my point was muddled. Let me restate:

```
Machine A archives files → produces tar bundle T1 (hash: aaa)
Machine B archives same files → produces tar bundle T2 (hash: bbb)

T1 ≠ T2 (different tar creation order → different tar → different hash)

But: every individual file's content hash is the same.
When Machine B archives:
  - File X (hash: abc123) → check chunk index → already in T1 → SKIP
  - File Y (hash: def456) → check chunk index → already in T1 → SKIP
  
No duplicate upload. Content-level dedup works.
T1 and T2 as tar bundles aren't deduped, but that doesn't matter 
because there IS no T2 — Machine B never created it.
```

Since you're only archiving from one machine, this is a non-issue entirely.

## The Full Picture

```
┌───────────────── ARCHIVE ─────────────────┐
│                                            │
│  Local FS ──hash──▶ Compare pointer files  │
│                     │         │             │
│                   match     new/changed     │
│                     │         │             │
│                   skip    HEAD chunk?       │
│                             │       │      │
│                          exists   missing   │
│                             │       │      │
│                          pointer  upload    │
│                          only    + pointer  │
│                                            │
│  Build tree (local) ─── HEAD each node ──▶ upload new nodes  │
│  Upload snapshot manifest                  │
└────────────────────────────────────────────┘

┌───────────────── LS ──────────────────────┐
│                                            │
│  Download snapshot manifest (tiny)         │
│  Walk tree nodes as needed (cached)        │
│  Display: name, size, created, modified    │
└────────────────────────────────────────────┘

┌───────────────── RESTORE ─────────────────┐
│                                            │
│  Walk tree to find target files            │
│  For each file:                            │
│    GET chunks/<content-hash>               │
│      found? → large file → decrypt → done  │
│      404?   → lookup chunk-index shard     │
│              → get tar-chunk-hash          │
│              → rehydrate tar chunk         │
│              → decrypt → un-tar → extract  │
│              → cache tar (reuse for others)│
└────────────────────────────────────────────┘
```

## Final Decision Table

| Decision | Choice |
|----------|--------|
| Tree storage | Option A (individual blobs), full export deferred |
| Tree hash | Passphrase-seeded SHA-256 |
| Tree metadata | Extensible (versioned JSON), includes size/created/modified |
| Content → chunk mapping | Option C (try content hash first, chunk index fallback) |
| Chunk index sharding | 2-byte prefix (65,536 shards), ~200 KB each |
| Chunk index updates | Rewrite affected shards per archive run |
| Pointer file format | Content hash only |
| Snapshot naming | UTC, sub-second: `2026-03-21T140000.000Z` |
| Small file threshold | 1 MB (configurable) |
| Tar target size | 64 MB (configurable) |
| Tar bundling | Strategy B (fill to target, order of FS walk) |
| Snapshot deletion | Never |
| Encryption | AES-256-CBC, openssl-compatible, `Salted__` prefix, PBKDF2 SHA-256 10K iterations |
| All blob names | Opaque hashes, no structural leakage |

I think this is solid. Want to formalize this into a change proposal, or are there more threads to pull?

User: i want to expand on the Archive (no pointer files, rebuild) scenario why it needs to download the old snapshot. The way i see it, you only need to know if the content hases exist (not the full snapshot). i dont see why the snapshot is needed to recreate the pointers since the pointers are just the hash of the content of the binary file?

GitHub Copilot: 

You're completely right, and I was muddling things. Let me correct this.

## Pointer File = Hash of Local Binary. That's it.

```
Without pointer files:

  photo.jpg  →  SHA256(passphrase + file content)  →  "abc123"
                                                        │
  Create:  photo.jpg.pointer.arius  ←──────────────────┘
           containing "abc123"

  HEAD chunks/abc123  →  200? Already archived. Skip.
                     →  404? Upload.
```

**No snapshot needed.** The pointer file is derived entirely from the local binary. You hash the file, you have the pointer. Done.

What you lose without pointer files is **performance, not correctness**:

| | With pointer files | Without pointer files |
|---|---|---|
| Hashing | Only changed files (pointer mismatch or missing) | Every file (CPU-expensive) |
| HEAD checks | Only new content hashes | Every content hash |
| Correctness | Same | Same |

At 500M files × 2 KB, hashing everything is:
```
500M × 2 KB = 1 TB of data to hash
SHA-256 throughput ~1 GB/sec on modern CPU
Time: ~1,000 sec ≈ 17 min (single threaded)
      ~2-4 min (multi-threaded)
```

Plus 500M HEAD requests (~8-17 min at 5-10K req/sec).

So a no-pointer-file archive is maybe 20 minutes slower, not fundamentally broken. And after that first run, pointer files exist and subsequent runs are fast.

**The only operations that need the remote snapshot are `ls` and `restore`** — where you genuinely don't know what's in the archive without asking blob storage.

Good catch. The design is actually simpler than I made it sound.

User: ok you captured all the parts about parallelization / concurrent hashing / uploading / downloading and the concurrency issues? if so, ok to formalize into a change proposal

GitHub Copilot: 

We haven't actually dug into the concurrency design yet. Let me lay it out quickly before we formalize.

## Archive Pipeline (Channels)

```
┌──────────┐    ┌──────────┐    ┌──────────┐    ┌──────────┐    ┌──────────┐
│ Enumerate │───▶│  Hash    │───▶│  Decide  │───▶│ Upload   │───▶│ Finalize │
│  Files    │    │  Files   │    │          │    │          │    │          │
│           │    │          │    │ skip /   │    │ large →  │    │ write    │
│ walk FS   │    │ parallel │    │ upload / │    │  solo    │    │ pointers │
│ graceful  │    │ N workers│    │ tar-buf  │    │ small →  │    │ build    │
│ skip errs │    │          │    │          │    │  tar buf │    │ tree     │
└──────────┘    └──────────┘    └──────────┘    └──────────┘    └──────────┘
Channel→         Channel→         Channel→         Channel→
FileInfo         (path,hash)      UploadJob        UploadResult
```

**Concurrency guards needed:**

| Risk | Guard |
|------|-------|
| Same content hash being uploaded by two threads | `ConcurrentDictionary<hash, Task>` — second thread awaits the first's upload |
| Tar buffer concurrency | Single writer (or lock): files are added to the current tar buffer sequentially, buffer is sealed+uploaded when it hits 64 MB |
| Pointer file write race | Not a risk — one file = one pointer, no shared state |
| Tree node upload | Same as chunks: dedup by hash before uploading |

```
Tar buffering detail:

  Decide stage                   Upload stage
      │                              │
      │  "small file, hash=abc"      │
      ├─────────────────────────────▶│
      │  "small file, hash=def"      │  ┌─────────────┐
      ├─────────────────────────────▶│  │ Tar Buffer   │
      │  "small file, hash=ghi"      │  │ abc + def +  │
      ├─────────────────────────────▶│  │ ghi + ...    │
      │                              │  │ (filling...) │
      │                              │  └──────┬───────┘
      │                              │         │ hits 64 MB
      │                              │         ▼
      │                              │  seal → gzip → encrypt → upload
      │                              │  record each content-hash → tar-chunk-hash
      │                              │  in chunk-index
```

## Restore Pipeline (Channels)

```
┌──────────┐    ┌──────────┐    ┌──────────┐    ┌──────────┐    ┌───────────┐
│ Resolve  │───▶│ Rehydrate│───▶│ Download │───▶│ Decrypt  │───▶│ Write     │
│ Files    │    │          │    │          │    │ +Extract │    │ to Disk   │
│          │    │ check if │    │ parallel │    │          │    │           │
│ walk tree│    │ already  │    │ N workers│    │ large →  │    │ set dates │
│ find     │    │ hydrated │    │ from     │    │ gunzip+  │    │ + create  │
│ chunks   │    │ else     │    │ rehyd.   │    │ decrypt  │    │ pointers  │
│          │    │ request  │    │ container│    │ small →  │    │           │
│          │    │ + poll   │    │ + cache  │    │ decrypt+ │    │           │
│          │    │          │    │          │    │ gunzip+  │    │           │
└──────────┘    └──────────┘    └──────────┘    │ un-tar   │    └───────────┘
Channel→         Channel→         Channel→       └──────────┘
(path,hash,      RehydrateJob     ChunkData      Channel→
 chunkHash)                                       (path,bytes)
```

**Concurrency guards needed:**

| Risk | Guard |
|------|-------|
| Same chunk being rehydrated by two threads | `ConcurrentDictionary<chunkHash, Task>` — dedup rehydration requests |
| Same tar chunk needed by multiple files | Download once, cache locally, extract multiple files from cache |
| Polling overwhelm | Batch polling: check rehydration status of N blobs at once, not one-by-one |
| Start downloading while still rehydrating | Two-phase: immediately process already-rehydrated chunks; poll+process remaining as they become ready |

```
Restore concurrency — the two-phase approach:

  Phase 1: Quick wins
  ┌─────────────────────────────────────────────┐
  │ For each chunk needed:                       │
  │   HEAD chunks-rehydrated/<hash> → exists?    │
  │     YES → download immediately (parallel)    │
  │     NO  → queue for rehydration              │
  └─────────────────────────────────────────────┘

  Phase 2: Wait + process
  ┌─────────────────────────────────────────────┐
  │ Submit all rehydration requests (parallel)   │
  │ Poll in batches until ready                  │
  │ Download + decrypt as each becomes available │
  │ (don't wait for ALL to be ready)             │
  └─────────────────────────────────────────────┘
```

## Hashing Concurrency Detail

```
500M files × 2 KB = 1 TB to hash

Strategy: bounded Channel<FileInfo> with N consumer tasks
  - Producer: single FS walker (FS enumeration is inherently sequential per directory)
  - Consumers: N hasher tasks (CPU-bound, N = processor count)
  - Backpressure: bounded channel prevents memory blowup
  
  IAsyncEnumerable<FileInfo> → Channel<(path, hash)> → Channel<UploadJob>
```

---

That covers the concurrency model. Anything to adjust, or ready to formalize?

User: ready to formalize