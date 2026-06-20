User: /opsx-explore #file:REQUIREMENTS.md 

GitHub Copilot: 

 

Ran terminal command:  openspec list --json 2>/dev/null || echo "no openspec cli"

  

Read [](file:///Users/wouter/Documents/GitHub/Arius7/openspec)

Read [](file:///Users/wouter/Documents/GitHub/Arius7/openspec/specs)

Read [](file:///Users/wouter/Documents/GitHub/Arius7)

Read [](file:///Users/wouter/Documents/GitHub/Arius7/openspec/config.yaml)

Fresh slate — OpenSpec just initialized, no specs or changes yet, no code. Just the requirements doc and a vision. Good place to think.

---

I've read through the whole requirements document carefully. This is a rich and well-thought-out design. Let me surface the threads that jumped out at me — some are questions, some are tensions I noticed, some are just interesting design spaces worth poking at.

## The Shape of the System

Here's how I see the major data flows:

```
LOCAL FILE SYSTEM                          AZURE BLOB STORAGE
                                           
┌──────────────────┐                       ┌─────────────────────────────┐
│  Binary Files     │──hash──┐             │  chunks/          (Archive) │
│  Pointer Files    │        │             │  filetrees/       (Cool)    │
│  (.pointer.arius) │        ▼             │  snapshots/       (Cool)    │
└──────────────────┘   ┌──────────┐        │  chunk-index/     (Cool)    │
                       │ Archive  │        │  chunks-rehydrated/ (Hot)   │
                       │ Pipeline │───────▶│                             │
                       │          │        └─────────────────────────────┘
                       │ hash     │                    │
                       │ gzip     │                    │
                       │ encrypt? │        ┌───────────┴──────────┐
                       │ upload   │        │   Restore Pipeline   │
                       └──────────┘        │                      │
                                           │  rehydrate (hours)   │
                              ┌────────────│  download             │
                              │            │  decrypt?             │
                              ▼            │  gunzip               │
                       ┌──────────┐        │  untar?               │
                       │ Restored │        └──────────────────────┘
                       │  Files   │
                       └──────────┘
```

## Threads Worth Pulling

### 1. The 500-Million-File Elephant

You call out 1 TB / 2 KB files (500M files) as the target scale. This is the single hardest constraint in the doc, and it ripples through everything:

- **File tree blobs**: With 500M files, even if directory fan-out is reasonable, serializing and uploading tree blobs for deeply nested structures is significant. How deep/wide do you expect typical directory trees to be? A flat directory with 500M entries in one tree blob is very different from 500M files across 50K directories.

- **Chunk index**: 65,536 shards × (500M entries) — each shard averages ~7,600 entries. At ~96 bytes per entry (64 hex hash + 64 hex chunk hash + separators), that's ~730 KB per shard, ~45 GB total. Your doc says 10 GB — are you assuming compression on the index shards, or a tighter entry format?

- **Local enumeration**: Enumerating 500M files on disk, hashing them, creating pointer files — that's a massive I/O operation even before any network calls. Have you thought about incremental approaches (watching for changes, timestamp-based skipping)?

- **Tar bundling**: At 500M × 2KB = 1TB of small files, every file goes into a tar bundle. That's ~1TB / 64MB = ~15,600 tar bundles. Each tar bundle gets a single chunk hash. The chunk index maps each individual content hash → tar chunk hash. That's 500M index entries. The index is the bottleneck, not the chunks.

### 2. The FilePair Abstraction

This is elegant, but I want to poke at it:

```
document.pdf              ← binary
document.pdf.pointer.arius ← pointer (contains hash)
```

**What about naming collisions?** If someone has a file literally named `report.pdf.pointer.arius` — is that a pointer file or a binary? You probably need a rule here (treat `.pointer.arius` suffix as always being a pointer, and warn/skip if the content doesn't look like a hex hash).

**Pointer files alongside binaries in the working directory** — this is like Git's approach to LFS pointers, but living next to the originals rather than replacing them. That means every directory with 1000 files now has 2000 entries. At 500M scale, that's 1 billion file system entries. Is the user OK with that clutter? Or does `--remove-local` become the expected default for large archives?

**Renamed/moved files**: You mention pointers can be renamed alongside binaries. Since content-addressing means the chunk already exists, this is just a snapshot metadata change. Good. But what about detecting renames vs. delete+create? Do you even care about that distinction, or is it purely "what exists right now"?

### 3. The Encryption Boundary

The design says a repo is either **fully encrypted** or **fully plaintext** — no mixing. That's clean. But:

- **Passphrase-seeded hashes** (`SHA256(passphrase + data)`) — this means the same file archived in two different repos with different passphrases produces different hashes. Expected, but worth noting: you can't deduplicate across repos.

- **More importantly**: `SHA256(passphrase + data)` — is that literally `SHA256(concat(passphrase_bytes, data_bytes))`? That's a length-extension-vulnerable construction. A more standard approach would be HMAC-SHA256 with the passphrase as key. Since you want openssl compatibility for worst-case recovery, this matters — what exact construction did the previous Arius version use? Backwards compatibility might force your hand here.

- **Blob names are passphrase-seeded hashes** — so even the chunk index shard paths change with the passphrase? That makes the "2-byte prefix" partitioning deterministic but opaque. Smart for privacy, but means you can't even tell how many files are in a repo without the passphrase.

### 4. The Restore Cost Problem

Archive tier rehydration is the most expensive operation. You've designed around it well (rehydrate to `chunks-rehydrated/`, check what's already rehydrated, download in parallel). But:

- **Cost estimation**: You mention this as a CLI capability for restore. Is this a dry-run that shows "rehydrating N chunks will cost approximately $X" before proceeding? That seems essential given archive tier pricing. Is it blocking (user must confirm) or informational?

- **Partial restores from tar bundles**: If I need one 2KB file that's inside a 64MB tar bundle, I must rehydrate and download the entire 64MB bundle. At archive tier rehydration costs, that's significant for one small file. Is there a way to surface this to the user? ("Restoring `tiny.txt` requires rehydrating a 64MB bundle containing 32,000 files — proceed?")

- **Rehydration priority**: When restoring a full snapshot, do you rehydrate all chunks at once (maximizing parallelism, higher peak cost) or batch them? Azure has limits on concurrent rehydrations per account.

### 5. The Merkle Tree Design

```
snapshot ──▶ root tree hash
                 │
        ┌────────┼────────┐
        ▼        ▼        ▼
     tree:A   tree:B   tree:C
     (dir/)   (dir/)   (dir/)
       │        │        │
       ▼        ▼        ▼
    entries   entries   entries
    (name,    (name,    (name,
     hash,    hash,     hash,
     size,    size,     size,
     dates)   dates)    dates)
```

**Unchanged directories reuse tree blobs** — this is Git-style and great for dedup. But metadata (modified date) changes even when content doesn't. If I `touch` a file without changing it, the tree blob changes (new modified date), which means a new tree hash, which cascades up to the root. Is that intentional? Or should tree hashing ignore metadata and only use name+type+content_hash?

This is a real design fork:
- **Option A**: Tree hash includes metadata → `touch` invalidates tree cache, new snapshot is larger
- **Option B**: Tree hash excludes metadata → Better dedup, but two snapshots with identical tree hashes might have different file dates

### 6. Stateless CLI, Stateful Blob Storage

> For restore or ls, the local file system should not know anything

This means every `ls` command must download at minimum: the snapshot manifest + the tree blob(s) for the requested path. For a deeply nested `ls` on a cold cache, that could be several round trips. Have you thought about a local cache for tree blobs? (The content-addressed nature makes them perfect cache candidates — they're immutable.)

Actually, re-reading — you do mention caching:
> File Tree blobs are cached locally and valid indefinitely (content-addressed = immutable)

Where does this local cache live? `~/.arius/cache/`? Per-repo? What happens in Docker where the filesystem is ephemeral?

---

## What's Grabbing Your Attention?

These are all threads I find interesting. Some are deep design questions, some are edge cases, some are scale concerns. Where do you want to dig in? Or is there something specific about the requirements that prompted you to explore?

User: 1. file trees - assume a typical user pc document structure
gzip + encryption on the chunk index indeed. mayme my math was wrong
local enumeration - yes that s fine
tar bundling - do you see another design?

2. naming collisions - indeed treat .pointer.arius alwasys as a ponter, warn and overwrite with the correct hex hash
the user is ok with that clutter. actually, also include an option --no-pointers
it is purely "what exists right now"

3. yes that s ok
previous arius does SHA356(passphrase+data)

4. cost estimation - yes the user must confirm
partial restores - yes the user must confirm
rehydration priority - ask the user for priority (Starndard/High i think).  rehydrate all at once, if if fails because of limits, we ll have to wait and re-start the restore

5. what would you recommend here? are there practical workarounds?

6. the cache is per repo. for docker it can be on a mounted volume

GitHub Copilot: 

Good, lots to work with. Let me take these in order and then go deep on #5 since you're asking for a recommendation.

---

### 1. Tar Bundling — Do I See Another Design?

The tar approach is solid. The alternatives I considered and why I'd stick with tar:

| Approach | Pros | Cons |
|----------|------|------|
| **Tar bundles** (current) | openssl/gzip/tar recoverable, simple, well-understood | Must download whole bundle for one file |
| **Pack files with index** (Git-style) | Random-access offset into blob | Archive tier = download whole blob anyway, so random access is useless. Breaks openssl/tar/gzip recovery. |
| **Per-directory bundles** | Restore one dir = fewer bundles | Kills cross-directory dedup, tiny dirs make tiny bundles |
| **SQLite blobs** | Queryable | Not openssl-recoverable, complex |

Tar wins on your worst-case-recovery constraint alone. The one refinement worth considering:

**Directory-affinity bundling**: when filling a tar, prefer files from the same directory. Don't enforce it — if a directory only has 3 small files, mix them with others. But when you have a choice, keep neighbors together. This means a directory restore is *likely* to hit fewer bundles. It's a heuristic, not a guarantee, and adds minimal complexity to the bundling logic.

The real question is how the tar builder works in the concurrent pipeline. Imagine this:

```
                    ┌──────────┐
  files ──hash──▶   │ Router   │
                    │ (by size)│
                    └────┬─────┘
                         │
              ┌──────────┴──────────┐
              ▼                     ▼
     ┌────────────────┐    ┌──────────────┐
     │  Large File    │    │  Tar Builder  │
     │  Pipeline      │    │  (accumulate  │
     │  (gzip+encrypt │    │   until 64MB, │
     │   +upload)     │    │   then seal+  │
     └────────────────┘    │   gzip+encrypt│
                           │   +upload)    │
                           └──────────────┘
```

The tar builder is a stateful accumulator across multiple files — that's a natural `Channel<T>` consumer in C#. Feed it small files, it packs them, seals when threshold hit, flushes at end. One thing to decide: **one tar builder or many?** A single builder is simpler and maximizes fill rate. Multiple builders (e.g., per-directory) give better locality but more partial fills.

I'd start with one builder, add directory-affinity sorting on the input.

---

### 2. `--no-pointers` — Careful Here

This is worth thinking through. Pointer files serve two purposes:

1. **Local visibility** — see what's archived in your file explorer
2. **Local cache of identity** — "this file has hash X" avoids re-hashing on the next archive run

Without pointers, every `arius archive` must re-hash every file to determine what's changed. At 500M × 2KB that's reading 1TB from disk every run. With pointers, you can short-circuit: if `file.bin` exists, `file.bin.pointer.arius` exists, and `file.bin`'s modified date hasn't changed → skip re-hashing.

Wait — actually you're not using modified dates for that today. The current design doesn't mention it. Without pointers and without mtime tracking, every archive is a full re-scan+re-hash. Is that acceptable?

Options:
- **`--no-pointers` means skip creating them** — accept re-hash cost every run. Fine for small repos, brutal at scale.
- **`--no-pointers` means ephemeral pointers** — create them during the archive run for internal tracking, delete them after. You still avoid re-hash within a single run but not across runs.
- **`--no-pointers` plus mtime cache** — store a local `.arius/mtime-cache` file mapping `relative-path → (mtime, size, hash)` instead of pointer files. Same optimization, no clutter. But this is a hidden state file, which you say you want to avoid for stateless local.

I'd suggest: implement `--no-pointers` as "don't create them" and accept the re-hash cost. But document the tradeoff clearly. At scale, users will want pointers.

---

### 3. SHA256(passphrase + data) — Lock It In

Backwards compatibility forces your hand. Just noting: `SHA256(passphrase + data)` (naive concatenation) is susceptible to length-extension attacks in theory, but in your threat model (the user owns the passphrase and the data), it doesn't matter. An attacker with access to the blob storage can't derive the passphrase from the hashes, which is what matters.

Document this construction precisely in a spec so future-you doesn't accidentally break it.

---

### 4. Restore UX — Confirmation Flow

Here's how I'd sketch the restore interaction:

```
$ arius restore /photos/2024/ --account myaccount --key ***

Scanning snapshot 2026-03-21T140000.000Z...
  
  Files to restore:     1,247
  Chunks needed:           89  (3 large, 86 tar bundles)
  Already rehydrated:      12
  Need rehydration:        77

  Estimated rehydration cost:  $2.34 (Standard) / $18.72 (High Priority)
  Estimated download cost:     $0.12
  Estimated total:             $2.46 - $18.84

  Rehydration priority? [S]tandard / [H]igh: S

  Proceed? [y/N]: y

  Rehydrating 77 chunks (Standard priority, ~15 hours)...
  ████████░░░░░░░░░░░░ 12/77 rehydrated, 23 files restored
  
  (Ctrl+C to pause — resume with same command)
```

The "resume with same command" is important — rehydration is idempotent. If the process dies or the user Ctrl+C's, re-running checks what's already rehydrated and picks up.

For the Azure limits issue: if rehydration requests start failing (429 / throttling), log a warning, wait with exponential backoff, and retry. Don't require the user to manually re-run.

---

### 5. Merkle Tree Metadata — My Recommendation

This is the meaty one. Let me lay out the options precisely:

```
Tree blob for /photos/:
{
  "entries": [
    { "name": "vacation.jpg", "type": "file", 
      "hash": "a1b2c3...",              ← content hash
      "size": 4200000,                  ← metadata
      "created": "2024-06-15T10:30:00", ← metadata
      "modified": "2024-06-15T10:30:00" ← metadata
    },
    { "name": "2023/", "type": "dir",
      "hash": "d4e5f6..."               ← child tree hash
    }
  ]
}

Tree hash = SHA256(this blob's bytes)
```

The question: when computing the tree hash, do we hash the **full blob** (including metadata) or just the **structural skeleton** (names + types + child hashes)?

**My recommendation: hash the full blob.**

Here's why:

**The cost argument is in your favor.** Tree blobs are Cool tier, typically a few KB each. Even in the extreme case:
- 50,000 directories × 5 KB average = 250 MB of tree blobs per snapshot
- If every single tree blob changes every snapshot (worst case: global `touch`), you add 250 MB of Cool tier per snapshot
- Cool tier storage: ~$0.01/GB/month → 250 MB costs $0.0025/month
- Over 100 snapshots with full churn: 25 GB → $0.25/month
- This is noise compared to 1 TB of Archive tier chunks

**The complexity argument kills the alternative.** To hash only the structural skeleton, you need:
- A separate serialization path for "hash computation" vs. "blob storage"
- Or a two-layer blob (structural hash + metadata overlay)
- Both are error-prone and make worst-case recovery harder (which blob format am I looking at?)

**The correctness argument.** If someone `touch`es a file, the filesystem state *did* change. The modified date is different. A new snapshot *should* reflect that. If the tree hash didn't change, you'd either:
- Miss the metadata change (incorrect snapshot)
- Need a separate metadata-diffing mechanism (complex)

**Practical workaround for the "cascade" concern:** Merkle trees cascade by definition — a leaf change propagates to the root. But the cascade is *cheap*:

```
/photos/2024/june/vacation.jpg  ← touched

Only these tree blobs are regenerated:
  /photos/2024/june/  (new tree blob) 
  /photos/2024/       (new tree blob, only the june/ hash entry changed)
  /photos/            (new tree blob, only the 2024/ hash entry changed)
  /                   (new tree blob, only the photos/ hash entry changed)

= 4 tree blobs regenerated, ~20 KB total, Cool tier
```

The unchanged branches (`/documents/`, `/music/`, etc.) keep their existing tree hashes. The dedup still works for the vast majority of the tree.

**So: hash the full blob, including metadata. Simple, correct, cheap.**

---

### 6. Cache Location

Per-repo, mounted volume for Docker — clean. I'd suggest:

```
~/.arius/cache/<repo-identifier>/
    chunk-index/         ← downloaded index shards
    filetrees/           ← downloaded tree blobs  
    chunks/              ← temporary, downloaded chunks for restore
```

Where `<repo-identifier>` is derived from the account+container (e.g., `SHA256(accountname + container)`[:12]). In Docker, mount `~/.arius/cache/` as a volume.

The `filetrees/` cache is valid forever (content-addressed, immutable). The `chunk-index/` cache needs invalidation when new content is archived (a shard could have new entries). Strategy: when archiving, you *know* which shards you updated — invalidate those. When restoring, download the shard and overwrite the cache (or compare etags).

---

### Open Threads

A few things I'd want to nail down before this becomes a design:

1. **The `--no-pointers` + re-hash tradeoff** — are you OK with the cost at scale, or do you want the mtime-cache compromise?

2. **Tar builder concurrency** — single builder or parallel builders? I argued single above but want your take.

3. **Snapshot pruning** — you say "snapshots are never deleted." At 500M files, each snapshot's tree structure is ~250 MB of tree blobs. After 1000 snapshots with significant churn, tree blob storage could reach 100+ GB in Cool tier. Still cheap (~$1/month), but eventually you might want garbage collection for unreferenced tree blobs. Worth designing the hook now even if not implementing?

4. **The archive pipeline shape** — I can sketch the full Channel-based pipeline (enumerate → hash → route → bundle/compress → encrypt → upload → update index → build tree → snapshot). Want to go deep on that?

User: 1. tar bundling, one tar builder

2. indeed every archive is a full re-scan and re-hash. --no-pointers would just skip creating them

5. ok to hash the full blob

open threads
1. yes, you cannot assume the hash in the pointer is up to date
2. single builder
3. ok to design the hook - but no need to implement
4. yes


GitHub Copilot: 

Alright, let's settle the decisions and then I'll go deep on the pipeline.

## Decisions Locked In

| # | Decision |
|---|----------|
| 1 | One tar builder, directory-affinity sorting as heuristic |
| 2 | `--no-pointers` = skip creation; every archive is full re-scan + re-hash regardless (pointer hash is never trusted as cache) |
| 3 | `SHA256(passphrase + data)` — backwards compatible, document precisely |
| 4 | Restore requires user confirmation (cost estimate + rehydration priority) |
| 5 | Tree hash = hash of full blob including metadata |
| 6 | Cache at `~/.arius/cache/<repo-id>/`, mounted volume for Docker |
| 7 | GC hook designed, not implemented |

---

## The Archive Pipeline

Since pointers are never trusted, every binary gets hashed. That's the starting point. Here's the full pipeline:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         ARCHIVE PIPELINE                                    │
│                                                                             │
│  ┌───────────┐    ┌───────────┐    ┌───────────┐    ┌──────────────────┐   │
│  │ Enumerate  │───▶│  Hash     │───▶│  Dedup    │───▶│  Route by Size   │   │
│  │ FilePairs  │    │  Binary   │    │  Check    │    │                  │   │
│  │            │    │           │    │  (index)  │    │  ≥threshold → L  │   │
│  │ Channel<   │    │ Channel<  │    │ Channel<  │    │  <threshold → S  │   │
│  │ FilePair>  │    │ Hashed    │    │ New       │    └──────┬───────────┘   │
│  └───────────┘    │ FilePair> │    │ FilePair> │           │               │
│                    └───────────┘    └───────────┘      ┌────┴────┐         │
│                                                        ▼         ▼         │
│                                              ┌──────────┐ ┌────────────┐   │
│                                              │  Large    │ │ Tar Builder│   │
│                                              │  File     │ │ (single)   │   │
│                                              │  Pipeline │ │            │   │
│                                              │  (N conc.)│ │ accumulate │   │
│                                              │           │ │ seal@64MB  │   │
│                                              │ gzip      │ │ tar+gzip   │   │
│                                              │ encrypt?  │ │ encrypt?   │   │
│                                              │ upload    │ │ upload     │   │
│                                              └─────┬─────┘ └─────┬──────┘   │
│                                                    │             │         │
│                                                    ▼             ▼         │
│                                              ┌─────────────────────────┐   │
│                                              │   Index Update          │   │
│                                              │   content-hash →        │   │
│                                              │     chunk-hash          │   │
│                                              │   Channel<IndexEntry>   │   │
│                                              └────────────┬────────────┘   │
│                                                           │               │
│  ┌────────────────────────────────────────────────────────┘               │
│  │                                                                        │
│  │  (After all files processed)                                           │
│  │                                                                        │
│  │  ┌──────────────┐    ┌──────────────┐    ┌──────────────┐             │
│  └─▶│ Build File   │───▶│ Upload Trees │───▶│ Create       │             │
│     │ Trees        │    │ & Index      │    │ Snapshot     │             │
│     │ (bottom-up)  │    │ Shards       │    │              │             │
│     └──────────────┘    └──────────────┘    └──────────────┘             │
│                                                                           │
│  Side effect (parallel with upload):                                      │
│  ┌──────────────────┐                                                     │
│  │ Write Pointer    │  (unless --no-pointers)                             │
│  │ Files            │                                                     │
│  └──────────────────┘                                                     │
│                                                                           │
└─────────────────────────────────────────────────────────────────────────────┘
```

Now let me walk through each stage.

---

### Stage 1: Enumerate FilePairs

```csharp
// Produces: Channel<FilePair>
// Concurrency: 1 producer (filesystem enumeration is sequential anyway)
```

Walk the local directory tree recursively. For each directory entry:

- Skip files matching `*.pointer.arius` (they're pointers, not binaries)
- Skip inaccessible files/dirs → log warning, continue
- For each binary file found, check if a matching `.pointer.arius` exists
- For each orphan `.pointer.arius` (no matching binary), include it too

The `FilePair` is:

```
FilePair {
    RelativePath: string        // "photos/2024/june/vacation.jpg"
    BinaryExists: bool
    PointerExists: bool
    PointerHash: string?        // read from pointer file if exists (untrusted)
    FileSize: long?             // from file system, for routing
    Created: DateTime?
    Modified: DateTime?
}
```

**Key rule**: A file is in the snapshot if:
- Binary exists → hash it, archive it
- Only pointer exists → trust the pointer hash, verify chunk exists in index. If chunk missing → log warning, skip from snapshot.
- Both exist → hash binary (pointer is untrusted)

The directory-affinity sorting happens here: enumerate depth-first so files from the same directory arrive at the tar builder roughly together.

**Output**: writes `FilePair` objects into channel. Multiple consumers on the other side.

---

### Stage 2: Hash Binaries

```csharp
// Consumes: Channel<FilePair>
// Produces: Channel<HashedFilePair>  
// Concurrency: N parallel consumers (CPU-bound, say 4-8 based on cores)
```

For each `FilePair`:
- If `BinaryExists` → read file, compute `SHA256(passphrase + data)` or `SHA256(data)`
- If only pointer → pass through with `PointerHash` as the content hash (unverified)

```
HashedFilePair {
    ...FilePair fields
    ContentHash: string         // the computed or read hash
    HashSource: Binary | Pointer  // to know if we verified it
}
```

**Streaming hash**: Read the file once, compute the hash as we stream. Don't load the whole file in memory. For the passphrase-seeded hash, prepend the passphrase bytes to the hash computation, then stream the file data.

**Pointer file update**: If `BinaryExists && PointerExists && computedHash != PointerHash`, mark the pointer as stale. It'll be overwritten in the pointer-writing side effect.

---

### Stage 3: Dedup Check (Chunk Index)

```csharp
// Consumes: Channel<HashedFilePair>
// Produces: Channel<HashedFilePair> (only NEW content)
// Side output: all HashedFilePairs go to tree builder regardless
// Concurrency: batched lookups
```

Check the chunk index: does this `ContentHash` already have a mapping to a `ChunkHash`?

- **Cache the chunk index shards locally** — on first access, download the shard for the 2-byte prefix. On subsequent lookups for the same prefix, use the cache.
- **Batch lookups by prefix** — if 100 files hash to the same 2-byte prefix, one shard download answers all of them.

If the content hash already exists → skip upload (dedup!). The file still goes into the snapshot tree, just no upload needed.

If the content hash is new → pass to the router.

**Pointer-only files**: Check the index. If `ContentHash` found → file is valid for the snapshot. If not found → log warning: "pointer file X references hash Y but no chunk exists" → exclude from snapshot.

```
┌─────────────────────────────────────────────────────┐
│              Dedup Decision Matrix                    │
├──────────────┬───────────┬──────────────────────────┤
│ Binary       │ In Index? │ Action                   │
├──────────────┼───────────┼──────────────────────────┤
│ Yes          │ Yes       │ Skip upload, add to tree │
│ Yes          │ No        │ Route to upload pipeline  │
│ No (pointer) │ Yes       │ Add to tree              │
│ No (pointer) │ No        │ Warn, exclude from tree  │
└──────────────┴───────────┴──────────────────────────┘
```

---

### Stage 4: Route by Size

```csharp
// Consumes: Channel<HashedFilePair> (new content only)
// Produces: Channel<LargeFile> OR Channel<SmallFile>
// Concurrency: 1 (trivial routing, just reads FileSize)
```

Simple threshold check: `FileSize >= --small-file-threshold` → large file pipeline, else → tar builder.

---

### Stage 5a: Large File Pipeline

```csharp
// Consumes: Channel<LargeFile>
// Produces: Channel<IndexEntry>
// Concurrency: N parallel (say 4, network-bound)
```

For each large file:
1. Open file stream
2. Pipe through GZipStream
3. Pipe through AES256CBC encrypt stream (if passphrase)
4. Upload to `chunks/<chunk-hash>`
   - The chunk hash = content hash for large files (it's a 1:1 mapping)
   - Set content type: `application/aes256cbc+gzip` or `application/gzip`
   - Set tier: `--tier` (default Archive)
5. Emit `IndexEntry { ContentHash, ChunkHash }`

**Streaming**: The file is never fully buffered. Read → gzip → encrypt → upload as a single streaming pipeline.

**Chunk hash for large files**: Since one chunk = one file, `ChunkHash == ContentHash`. Simple.

---

### Stage 5b: Tar Builder

```csharp
// Consumes: Channel<SmallFile>
// Produces: Channel<IndexEntry> (batch — one per file in the sealed tar)
// Concurrency: 1 (singleton accumulator)
```

This is the most interesting stage. It's stateful:

```
TarBuilder {
    currentTar: TarArchive
    currentSize: long
    entries: List<(ContentHash, RelativePath)>
    
    Add(file):
        add file to currentTar
        currentSize += file.Size
        entries.add(file.ContentHash, file.RelativePath)
        
        if currentSize >= --tar-target-size:
            Seal()
    
    Seal():
        finalize tar
        tarBytes = tar.ToBytes()
        chunkHash = SHA256(passphrase + tarBytes) or SHA256(tarBytes)
        compressed = gzip(tarBytes)
        encrypted = encrypt?(compressed)
        upload to chunks/<chunkHash>
        
        for each entry in entries:
            emit IndexEntry { ContentHash: entry.Hash, ChunkHash: chunkHash }
        
        reset currentTar, currentSize, entries
    
    Flush():  // called when channel completes
        if entries.Count > 0:
            Seal()  // seal the partial last tar
}
```

**Content type**: `application/aes256cbc+tar+gzip` or `application/tar+gzip`

**Critical detail**: The tar contains the files by their `ContentHash` as filename (not the original path). This way the tar is content-addressed — the same file bundled in any tar is extractable by its hash. The mapping from original path → content hash is in the file tree, the mapping from content hash → chunk hash is in the chunk index.

```
tar contents:
  a1b2c3d4e5f6...   (raw bytes of file 1)
  f7e8d9c0b1a2...   (raw bytes of file 2)
  ...
```

**Directory-affinity**: Since enumeration is depth-first, files from the same directory arrive roughly in sequence. The tar builder just packs them as they come. No explicit sorting needed — the enumeration order provides the locality.

---

### Stage 6: Index Update

```csharp
// Consumes: Channel<IndexEntry>
// Concurrency: batched writes per shard
```

Collect all new `IndexEntry` objects. Group by 2-byte prefix of `ContentHash`. For each shard:
1. Download existing shard (or use cached version)
2. Append new entries
3. Upload updated shard
4. Invalidate local cache for that shard

**Batching**: Don't upload after every entry. Collect all entries, then do one upload per shard at the end (or periodically for very large archives to checkpoint progress).

---

### Stage 7: Build File Trees

This happens **after all uploads complete** (needs all content hashes resolved):

```
Walk the in-memory directory structure:

  For each leaf directory (deepest first, bottom-up):
    1. Collect entries (files with their metadata + content hashes)
    2. Serialize to JSON
    3. Compute tree hash = SHA256(json bytes)  [passphrase-seeded if encrypted]
    4. Check if tree blob already exists in filetrees/ (content-addressed dedup)
    5. If new, upload to filetrees/<tree-hash>
    6. Pass tree hash up to parent directory

  For each parent directory:
    1. Entries = child dirs (with their tree hashes) + local files
    2. Same: serialize, hash, dedup, upload
    3. Pass up

  Root directory → root tree hash
```

```
Example:

/                          tree: hash_root
├── photos/                tree: hash_photos
│   ├── 2024/              tree: hash_2024
│   │   └── june/          tree: hash_june
│   │       ├── vacation.jpg  (content: a1b2c3)
│   │       └── beach.jpg     (content: d4e5f6)
│   └── 2023/              tree: hash_2023  ← UNCHANGED from last snapshot
│       └── ...                              ← same tree hash, no re-upload
└── documents/             tree: hash_docs   ← UNCHANGED
    └── ...                                  ← same tree hash, no re-upload
```

**GC hook**: When building trees, we know exactly which tree hashes are referenced by this snapshot. A future GC would diff referenced hashes across all snapshots vs. all blobs in `filetrees/` and delete orphans. The hook is just: each snapshot stores its root hash, and tree blobs are traversable. No extra bookkeeping needed — the merkle tree *is* the reference graph.

---

### Stage 8: Create Snapshot

```json
// snapshots/2026-03-22T150000.000Z
{
    "rootTreeHash": "abc123...",
    "timestamp": "2026-03-22T15:00:00.000Z",
    "fileCount": 1247,
    "totalSize": 4832716800,
    "ariusVersion": "7.0.0"
}
```

Encrypt if passphrase. Upload to `snapshots/`. Done.

---

### Side Effect: Pointer Files

Running in parallel with uploads (doesn't block the pipeline):

```csharp
// Consumes: Channel<HashedFilePair> (all files, not just new ones)
// Concurrency: N parallel writers
```

For each `HashedFilePair` where `BinaryExists`:
- If `--no-pointers` → skip
- If pointer exists and hash matches → skip
- If pointer doesn't exist → create `<path>.pointer.arius` with content hash
- If pointer exists but hash differs (stale) → overwrite

---

### The Full Channel Graph

```
                    Channel<FilePair>
Enumerate ─────────────────────────────────▶ Hash (×N)
                                                │
                                    Channel<HashedFilePair>
                                                │
                                    ┌───────────┤
                                    │           │
                                    ▼           ▼
                              Pointer      Dedup Check
                              Writer       (batch by prefix)
                              (side)            │
                                         Channel<NewFilePair>
                                                │
                                           ┌────┴────┐
                                           ▼         ▼
                                     Large File   Tar Builder
                                     (×N)         (×1)
                                           │         │
                                           ▼         ▼
                                    Channel<IndexEntry>
                                           │
                                    Index Update
                                    (batch per shard)
                                           │
                                    ── barrier ──
                                           │
                                    Build Trees
                                    (bottom-up)
                                           │
                                    Create Snapshot
```

**Backpressure**: Bounded channels throughout. If uploads are slow, the hash stage naturally pauses waiting for channel space. If hashing is slow, enumeration pauses. The system self-regulates.

**Progress reporting**: Each stage transition is a natural place to emit a progress event via the Mediator. The CLI subscribes and renders:

```
Scanning...  12,847 files found
Hashing...   ████████████░░░░  8,231 / 12,847
Uploading... ████░░░░░░░░░░░░  312 / 2,100 chunks (1.2 GB / 8.4 GB)
Building trees... 847 directories
Snapshot created: 2026-03-22T150000.000Z
```

---

### Edge Cases in the Pipeline

| Scenario | Handling |
|----------|----------|
| File deleted between enumerate and hash | Hash stage catches `FileNotFoundException` → warn, skip |
| File modified between hash and upload | Content will differ from hash — but we hash *then* read the same stream for upload. If using streaming hash+upload, the hash is computed on what's actually uploaded. Consistent. |
| Tar builder has 1 file at flush | Seal it anyway. A tar with one entry is fine. |
| Same content hash appears twice (two identical files) | First one wins through dedup check. Second skips upload. Both appear in the tree with their respective paths. |
| Binary + stale pointer, `--no-pointers` set | Hash the binary, archive it, don't update the pointer. The stale pointer sits there. Harmless — it's never trusted. |
| Pointer only, chunk missing from index | Warn: "orphan pointer, no backing chunk". Exclude from snapshot. |
| Very large directory (100K+ files in one dir) | Tree blob will be large (~10MB+). Fine — it's Cool tier, single upload. |
| Upload fails mid-way | Retry with exponential backoff. After N retries, fail the file, log error, continue with remaining files. Snapshot excludes the failed file. |

---

### Concurrency Model Summary

| Stage | Concurrency | Why |
|-------|-------------|-----|
| Enumerate | 1 | FS enumeration is inherently sequential |
| Hash | N (CPU cores) | CPU-bound, embarrassingly parallel |
| Dedup Check | Batched | Network I/O for shard download, amortized |
| Large Upload | N (4-8) | Network-bound, parallel uploads |
| Tar Builder | 1 | Stateful accumulator, must be single-threaded |
| Tar Upload | 1 per sealed tar | Could overlap: builder seals, uploads, starts new tar |
| Index Update | Batched at end | One upload per modified shard |
| Tree Build | 1 | Sequential bottom-up traversal (fast, in-memory) |
| Pointer Write | N | Trivially parallel file writes |

---

### One Design Question That Emerged

**Tar builder upload overlap**: When the tar builder seals a bundle at 64MB, should it:

**(A)** Block while it gzips + encrypts + uploads, then resume accumulating?
**(B)** Hand off the sealed tar to an upload channel, immediately start a new tar?

Option B is clearly better — the builder can keep accumulating while the previous bundle uploads. This means the tar builder actually produces into a `Channel<SealedTar>`, and a separate upload stage (could be the same as the large file upload pool) consumes it.

```
Small files ──▶ Tar Builder (×1) ──▶ Channel<SealedTar> ──▶ Upload (×N)
                 accumulate           sealed bundles          shared pool
                 seal@64MB                                    with large files
```

This way the upload pool is unified: large files and sealed tars compete for the same N upload slots. Simple and efficient.

---

Where do you want to go from here? If this pipeline shape feels right, it might be ready to crystallize into a change proposal. Or we can keep pulling threads — I haven't touched the restore pipeline yet, and that one has its own interesting concurrency story (rehydration polling, partial availability, progressive download).

User: 1. skip files matching .pointer,arius --> you cannot do that: if the archive is 'thin' they have real meaning and files should be marked as present

2. indeed, you cannot load the file in memory; it CAN be 10 GB as well - so streaming is the way

3. how will you know if a content hash is already present in the repository? Will you keep 10 GB of hashes in memory?

5a. i also would like to know the size of the binary after compression/encryption -> also put that in the index shards as metadata?

For the CLI archive progress reporting - yes what you show is nice but i also would like a notion of the files that are in flight currently in the hashing & uploading stage

Tar builder -> option B is ok

Have you thought through potential concurrency/race issues? Memory consumption? It is ok to max out CPU and network bandwidth

How will an incremental archive work? anything special there?

How will you handle a crash mid-archive pipeline - some large files/TARs will already be uploaded but the remote index will not be in sync?

after that, ok to start pulling the restore pipeline

GitHub Copilot: 

Great questions — several of these expose real design problems I glossed over. Let me go deep.

---

## 1. Enumerate Stage — Pointer Files Have Meaning

You're right, I was wrong. In a thin archive, pointer files *are* the files. Revised enumeration:

```
For each filesystem entry:
  If it's a .pointer.arius file:
    Check if a matching binary exists
    YES → part of a FilePair (binary drives, pointer is advisory)
    NO  → "thin" entry, pointer drives. Read hash from pointer.
  
  If it's a regular file:
    Check if it has a .pointer.arius sibling
    YES → part of a FilePair
    NO  → standalone binary, will get a pointer created
  
  Skip: .arius internal files, inaccessible files (warn + continue)
```

---

## 3. The 10GB Chunk Index Problem

This is the biggest design challenge you've surfaced. Let me think through it carefully.

At 500M files with 64-char hex hashes: a naive `HashSet<string>` is ~32 GB. Won't fit in memory.

The chunk index lives as 65,536 shards on disk (2-byte prefix). Each shard averages ~7,600 entries at 500M scale. Compressed+encrypted, each shard might be ~200-400 KB. Total on-disk: ~15-25 GB.

**The dedup check strategy:**

```
┌──────────────────────────────────────────────────────────────────┐
│                    DEDUP CHECK DESIGN                             │
│                                                                   │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │               LOCAL DISK CACHE                               │ │
│  │  ~/.arius/cache/<repo>/chunk-index/                         │ │
│  │                                                              │ │
│  │  00/index  01/index  02/index  ...  ff/index                │ │
│  │  (each shard: ~200-400 KB compressed on disk)               │ │
│  └─────────────────────────────────────────────────────────────┘ │
│                          ▲                                       │
│                          │ download on first access              │
│                          │                                       │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │              IN-MEMORY                                       │ │
│  │                                                              │ │
│  │  1. Pending buffer: List<HashedFilePair>  (bounded, ~10K)   │ │
│  │  2. In-flight set:  ConcurrentHashSet<string> (small)       │ │
│  │                                                              │ │
│  └─────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────┘
```

**The algorithm:**

The dedup stage doesn't check hashes one at a time. It **buffers and batches by prefix**:

```
1. Accumulate N hashed files into a buffer (N = 10,000)

2. Group buffer by 2-byte hash prefix
   e.g., hash "a1b2c3..." → prefix "a1"
         hash "a1d4e5..." → prefix "a1"
         hash "f7e8..."   → prefix "f7"

3. For each unique prefix in the batch:
   a. Is shard cached on disk? 
      NO  → download from Azure, save to disk cache
      YES → use cached version
   b. Load shard into memory (parse ~400 KB → HashSet)
   c. Check all buffered hashes with this prefix against the shard
   d. Release the parsed shard from memory

4. Results:
   - Found in index → skip upload, emit to tree manifest
   - Not found AND not in-flight → add to in-flight set, route to upload
   - Not found BUT in in-flight → skip (duplicate in same run)
```

**Why this works at scale:**

- In-memory at any point: 1 parsed shard (~7,600 entries × 96 bytes = ~730 KB) + the pending buffer (~10K × 200 bytes = 2 MB) + the in-flight set (bounded by pipeline backpressure, say 1000 entries = 64 KB)
- Total memory: **~3 MB** regardless of archive size
- After ~100K files processed, most of the 65K shards are cached on disk. Subsequent batches hit disk only (fast on SSD).
- First archive of 500M files: downloads all 65K shards once = ~25 GB network. This is a one-time cost, cached afterwards.

**The in-flight set solves the race condition:**

```
File A (hash: abc123) → dedup stage → not in index, not in-flight → add to in-flight, route to upload
File B (hash: abc123) → dedup stage → not in index, BUT in in-flight → skip upload

... later ...

File A upload completes → remove abc123 from in-flight, update disk shard cache
```

The in-flight set stays small because bounded channels create backpressure. If you have 8 upload slots and 10K channel buffer, at most ~10K hashes can be in-flight. That's ~640 KB of memory.

---

## 5a. Compressed Size in the Index

Yes, store it. Each index entry becomes:

```
content-hash → (chunk-hash, compressed-chunk-size)
```

For a tar bundle, every file in that tar maps to the same chunk-hash and same compressed-chunk-size. Redundant but cheap (8 bytes per entry) and avoids a separate chunk metadata lookup during restore cost estimation.

For the restore cost estimation, you can then sum `compressed-chunk-size` for all unique `chunk-hash` values needed — that gives you exact download size and a good basis for rehydration cost ($0.02/GB for Standard, varies by region).

Shard entry format:

```
<content-hash-hex> <chunk-hash-hex> <compressed-size-bytes>\n
```

Plain text, one line per entry. Easy to parse, grep-friendly for worst-case recovery. With encryption, the whole shard is encrypted — but once decrypted, it's human-readable.

---

## CLI Progress — In-Flight Visibility

```
Archiving snapshot...

  Scanned:    142,847 files  (12.4 GB)
  Hashed:     138,201 / 142,847  ████████████████░░░░  96.7%
  New:          2,341 (1.8 GB)   Deduped: 135,860
  Uploading:    1,892 / 2,341    ████████████████░░░░  80.8%  (1.2 GB / 1.8 GB)

  In-flight:
    Hashing (4):  photos/2024/DSC_8842.RAW (68%)
                  photos/2024/DSC_8843.RAW (12%)
                  documents/report-2025.pdf (91%)
                  videos/clip-003.mp4 (3%)
    
    Uploading (3): photos/2024/DSC_8840.RAW (2.1 GB, 84%)
                   tar-bundle-0147 (64 MB, 23%, 312 files)
                   tar-bundle-0148 (41 MB, sealing...)
```

Each pipeline stage emits progress events through Mediator:

```csharp
record FileScanned(string RelativePath, long Size);
record FileHashing(string RelativePath, long BytesProcessed, long TotalBytes);
record FileHashed(string RelativePath, string ContentHash, bool IsNew);
record ChunkUploading(string ChunkHash, long BytesUploaded, long TotalBytes, string? SourceFile);
record ChunkUploaded(string ChunkHash, long CompressedSize);
record TarBundleSealing(int BundleNumber, int FileCount, long UncompressedSize);
record TarBundleUploaded(int BundleNumber, string ChunkHash, long CompressedSize);
record SnapshotCreated(string Timestamp, int FileCount, long TotalSize);
```

The CLI subscribes to these and maintains a live-updating display. Terminal.Gui would give you a nice TUI for this; Spectre.Console if you prefer a simpler progress bar approach.

---

## Concurrency & Race Conditions

Let me map every shared resource and its synchronization:

```
┌────────────────────────────────────────────────────────────────────┐
│              SHARED RESOURCE / RACE CONDITION MAP                   │
├───────────────────┬──────────────────┬─────────────────────────────┤
│ Resource          │ Contention       │ Resolution                  │
├───────────────────┼──────────────────┼─────────────────────────────┤
│ In-flight set     │ Dedup stage      │ ConcurrentDictionary        │
│                   │ + upload done    │ TryAdd returns false = skip │
│                   │                  │                             │
│ Disk shard cache  │ Dedup batches    │ Single dedup stage (×1)     │
│                   │ + index update   │ so no contention during     │
│                   │                  │ reads. Index update happens  │
│                   │                  │ after uploads complete =     │
│                   │                  │ sequential, no race.         │
│                   │                  │                             │
│ Tar builder       │ Small files from │ Single consumer (×1) on     │
│                   │ the router       │ the channel. No contention. │
│                   │                  │                             │
│ Upload pool       │ Large files +    │ Shared bounded channel.     │
│                   │ sealed tars      │ Each consumer takes one     │
│                   │                  │ item. No shared state.      │
│                   │                  │                             │
│ Azure blob upload │ Parallel uploads │ Each uploads to a unique    │
│                   │ to same blob?    │ blob name (content hash).   │
│                   │                  │ If two upload same hash:    │
│                   │                  │ in-flight set prevents this.│
│                   │                  │                             │
│ Tree manifest     │ All stages write │ ConcurrentBag or Channel.   │
│ (on-disk)         │ completed files  │ See below.                  │
│                   │                  │                             │
│ Pointer files     │ Parallel writers │ Each writes to a unique     │
│                   │ on local FS      │ file path. No contention.   │
│                   │                  │                             │
│ Progress events   │ All stages emit  │ Mediator handles dispatch.  │
│                   │                  │ Events are fire-and-forget. │
└───────────────────┴──────────────────┴─────────────────────────────┘
```

**The only tricky race**: two identical files processed concurrently. File A and File B have the same content hash. They arrive at the dedup stage in the same batch (or different batches close together).

```
Timeline:
  t0: File A arrives at dedup. Not in index, not in-flight. 
      → Add hash to in-flight. Route to upload.
  t1: File B arrives at dedup. Not in index, BUT in in-flight.
      → Skip upload. Both files recorded in tree manifest.
  t2: File A upload completes. Remove from in-flight. Update shard cache.
  
Result: One upload, two tree entries. Correct.
```

**What if dedup runs concurrently?** It doesn't. The dedup stage is a single consumer (×1) because it manages the in-flight set and batches by prefix. Making it concurrent would require locking on the in-flight set and shard cache, adding complexity for minimal gain (the bottleneck is network, not dedup checking).

---

## Memory Consumption

Here's the memory budget at 500M file scale:

```
┌──────────────────────────────────────────────────────────────────┐
│                    MEMORY BUDGET                                  │
├─────────────────────────────┬─────────┬──────────────────────────┤
│ Component                   │ Size    │ Notes                    │
├─────────────────────────────┼─────────┼──────────────────────────┤
│ Channel buffers (5 channels │  ~1 MB  │ 1000 items × ~200 bytes  │
│   × 1000 bounded)           │         │ each × 5 channels        │
│                             │         │                          │
│ Hash worker buffers         │ ~512 KB │ 8 workers × 64 KB read   │
│   (8 concurrent)            │         │ buffer                   │
│                             │         │                          │
│ Dedup pending buffer        │  ~2 MB  │ 10K items × 200 bytes    │
│                             │         │                          │
│ Dedup shard (active)        │ ~730 KB │ 1 shard loaded at a time │
│                             │         │                          │
│ In-flight set               │ ~640 KB │ ~10K hashes × 64 bytes   │
│                             │         │                          │
│ Tar builder                 │ ~256 KB │ Writes to temp file on   │
│   (streams to disk)         │         │ disk, not memory.        │
│                             │         │ Buffer only.             │
│                             │         │                          │
│ Upload buffers              │  ~4 MB  │ 8 upload workers ×       │
│   (8 concurrent × 512 KB)  │         │ 512 KB stream buffer     │
│                             │         │                          │
│ GZip buffers                │ ~512 KB │ Per-stream internal      │
│                             │         │                          │
│ Encrypt buffers             │ ~128 KB │ AES block buffers        │
│                             │         │                          │
│ Tree manifest (on-disk)     │  ~0 MB  │ Written to temp file,    │
│                             │         │ read back per-directory   │
│                             │         │                          │
│ Tree builder (1 dir at time)│  ~20 MB │ Worst case: 100K files   │
│                             │         │ in one directory         │
├─────────────────────────────┼─────────┼──────────────────────────┤
│ TOTAL (peak)                │ ~30 MB  │ Bounded, independent of  │
│                             │         │ total file count         │
└─────────────────────────────┴─────────┴──────────────────────────┘

Disk usage (temporary):
  - Shard cache:    ~25 GB  (downloaded from Azure, persistent)
  - Tar temp files: ~64 MB  (one at a time, deleted after upload)
  - Tree manifest:  ~50 GB  (500M entries × ~100 bytes, deleted after snapshot)
```

**The tree manifest is the biggest temporary disk consumer.** 500M files × (relative path avg 80 bytes + hash 64 bytes + metadata 40 bytes) ≈ 90 GB on disk. That's significant.

**Optimization**: compress the manifest with gzip as it's written. Text paths compress ~5:1, so ~18 GB. Or use a binary format (varint lengths, raw hash bytes) to get it down to ~30 GB uncompressed, ~8 GB compressed.

Or, rethink the tree building. Instead of a flat manifest:

**Tree builder integrated with enumeration**: Since enumeration is depth-first, we can seal directory trees as we go:

```
Enumerating depth-first:
  /photos/2024/june/vacation.jpg    → process
  /photos/2024/june/beach.jpg       → process
  /photos/2024/june/                ← all files in june/ seen
                                       → SEAL tree blob for june/
  /photos/2024/july/sunset.jpg      → process
  /photos/2024/july/                ← all files in july/ seen
                                       → SEAL tree blob for july/
  /photos/2024/                     ← all children of 2024/ sealed
                                       → SEAL tree blob for 2024/
                                          (entries: june/ → hash, july/ → hash)
```

This works if enumeration is strictly depth-first and we can detect "leaving a directory." The challenge: files arrive at the tree builder *after* hashing and dedup (out of order due to parallel hashing). So we need to restore order.

**Solution: two-phase tree build with bounded memory:**

```
Phase 1 (during pipeline): 
  Write completed files to a manifest file, unsorted.
  Each entry: (relative-path, content-hash, size, created, modified)

Phase 2 (after pipeline completes):
  External sort the manifest by path (standard merge-sort on disk).
  Stream through sorted manifest, group by directory.
  For each directory (bottom-up): build tree blob, hash, upload.
  Memory = one directory at a time.
```

The external sort handles 500M entries on disk efficiently. Standard implementations exist. A 90 GB file sorts in ~15 minutes with ~1 GB working memory.

This is the safe, scalable approach. Even at 500M files, memory stays bounded.

---

## Incremental Archive

Nothing special — by design:

```
Run 1 (initial archive of 100K files):
  Enumerate: 100K files
  Hash: 100K files
  Dedup: 0 in index → 100K new
  Upload: 100K (routed by size to large/tar)
  Index: updated
  Snapshot: created (v1)

Run 2 (10 files changed, 5 added, 3 deleted):
  Enumerate: 100,005 files (100K - 3 deleted + 5 added + 2 pointers for the 3 deleted? No - deleted means gone)
  
  Actually: 99,997 files still exist + 5 new = 100,002
  Hash: 100,002 files (full re-hash, pointers untrusted)
  Dedup: 99,987 found in index → skip. 15 new (10 changed + 5 added).
  Upload: 15 files
  Index: updated (15 new entries; old entries for the 10 changed files remain — they're still valid, old content still in chunks)
  Snapshot: created (v2)
  
  The 3 deleted files: simply not in the new snapshot's tree.
  The 10 changed files: old chunk still exists, old snapshot (v1) references it.
  Nothing is deleted from blob storage. Ever.
```

**The cost of full re-hash on incremental**: at 100K files × 100 KB average = 10 GB of disk reads. On modern SSD: ~30 seconds. At 500M × 2 KB = 1 TB: ~15 minutes on SSD, longer on HDD. This is the accepted tradeoff for not trusting pointers.

**One optimization for later** (not in scope): if the shard cache is current, you could check file `(path, size, mtime)` against the manifest from the previous snapshot. If unchanged → skip re-hash. But this requires downloading tree blobs to compare. Might be worth it at extreme scale, but adds complexity.

---

## Crash Recovery

This is important. Let me walk through what state looks like mid-crash:

```
CRASH SCENARIOS
═══════════════

Scenario A: Crash during hashing
  Uploaded: nothing
  Index: unchanged
  Snapshot: not created
  Recovery: re-run = full re-scan. No wasted work (nothing uploaded).
  Cost: zero

Scenario B: Crash during uploads (most likely)
  Uploaded: some chunks (M of N)
  Index: ???
  Snapshot: not created
  
  SUB-QUESTION: when are index shards updated?

Scenario C: Crash during tree building
  Uploaded: all chunks
  Index: updated
  Snapshot: not created
  Recovery: re-run = full re-scan, dedup finds all chunks → 0 new uploads,
            rebuild trees, create snapshot. 
  Cost: re-hash time only (no network waste)

Scenario D: Crash during snapshot creation
  Same as C. Snapshot upload is atomic (single small blob).
  Cost: re-hash time only
```

**Scenario B is the critical one.** The question is: when do we update the index shards?

**Option 1: Update index shards at the end (after all uploads)**
```
Risk: crash after uploads but before index update = orphan chunks.
Re-run: dedup stage downloads stale index shards, doesn't find the 
        already-uploaded chunks, re-uploads them.
Cost: duplicate archive-tier uploads ($$) for the chunks uploaded 
      before the crash.
```

**Option 2: Update index shards incrementally (after each upload or batch)**
```
Risk: crash leaves index partially updated.
Re-run: dedup stage finds most chunks already in index, only re-uploads
        the ones whose shard wasn't updated yet.
Cost: minimal duplicate uploads. Much better.
```

**Option 2 is clearly better.** Here's the flow:

```
Upload completes for chunk X (content-hash: abc123, chunk-hash: def456)
  → Read shard "ab" from disk cache (or download)
  → Append entry: abc123 → (def456, compressed-size)
  → Upload updated shard "ab" to Azure
  → Update disk cache

Next upload completes for chunk Y (content-hash: ab7890, chunk-hash: 111222)
  → Same shard "ab" — already cached and current
  → Append entry
  → Upload updated shard
```

**Batching the shard updates**: Uploading a shard after every single chunk upload is expensive (65K potential shard uploads for 65K chunks). Instead, batch:

```
Buffer completed uploads: List<IndexEntry>
Every N uploads (say 100) OR every T seconds:
  Group buffered entries by shard prefix
  For each modified shard:
    Download/load current shard
    Append new entries
    Upload updated shard
  Clear buffer

On pipeline completion: flush remaining buffer
```

**Crash impact with batched updates**: At most the last N uploads (~100) might not be in the index. On re-run, those ~100 chunks get re-uploaded. Acceptable.

**One more edge case**: archive-tier chunks can't be overwritten (well, they can but the old version lingers and you pay for it). So "re-uploading" a chunk that's already in archive tier actually creates a new version. To avoid this:

```
Before uploading chunk X:
  1. Check: does blob chunks/<chunk-hash> already exist? 
     (HEAD request — very cheap)
  2. YES → skip upload, just ensure index entry exists
  3. NO  → upload
```

This HEAD check makes re-runs after a crash **idempotent and cost-free** for chunks. The only wasted work is re-hashing locally.

**Summary of crash recovery:**

| What crashed | What's wasted | Re-run cost |
|-------------|---------------|-------------|
| During hashing | Nothing uploaded | CPU only |
| During uploads (with incremental index) | ~100 chunks not indexed | CPU + ~100 re-uploads |
| After uploads, during trees | Everything indexed | CPU only |
| During snapshot | Everything built | CPU only (snapshot is tiny) |

---

## Revised Pipeline (incorporating all feedback)

```
┌─────────────────────────────────────────────────────────────────────────┐
│                    REVISED ARCHIVE PIPELINE                              │
│                                                                         │
│  Enumerate (×1)                                                         │
│      │ Channel<FilePair>                                                │
│      ▼                                                                  │
│  Hash (×N, CPU cores)                                                   │
│      │ Channel<HashedFilePair>                                          │
│      ├──────────────────────────────────▶ Manifest Writer (disk)        │
│      │ (ALL files, for tree building)     sorted temp file              │
│      ▼                                                                  │
│  Dedup Check (×1, batched by prefix)                                    │
│      │ Channel<NewFilePair>                                             │
│      │ in-flight set (ConcurrentDict)                                   │
│      ▼                                                                  │
│  Route by Size (×1)                                                     │
│      ├─── large ──▶ Channel<LargeFile>                                  │
│      └─── small ──▶ Channel<SmallFile>                                  │
│                          │                                              │
│                          ▼                                              │
│                     Tar Builder (×1)                                     │
│                     → streams to temp file on disk                       │
│                     → seals at 64MB                                      │
│                          │                                              │
│                     Channel<SealedTar>                                   │
│                          │                                              │
│      ┌───────────────────┘                                              │
│      ▼                                                                  │
│  Upload Pool (×N, network-bound)  ← unified for large + sealed tars    │
│      │                                                                  │
│      │  (HEAD check before upload to avoid duplicate archive writes)    │
│      │                                                                  │
│      ▼                                                                  │
│  Index Updater (×1, batched every 100 uploads or 30 sec)                │
│      │  update shard → upload shard → update disk cache                 │
│      │  remove from in-flight set                                       │
│      │                                                                  │
│      ══════════ barrier (all uploads + index updates done) ═════════    │
│      │                                                                  │
│  Pointer Writer (×N)   ← unless --no-pointers                          │
│      │                                                                  │
│  External Sort manifest on disk                                         │
│      │                                                                  │
│  Tree Builder (×1, stream sorted manifest, one dir at a time)           │
│      │  → build tree blob, hash, upload to filetrees/                   │
│      │  → cascade hashes up to root                                     │
│      │                                                                  │
│  Create Snapshot (root hash + metadata)                                 │
│      │                                                                  │
│  Cleanup (delete temp manifest, temp tar files)                         │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘

Memory: ~30 MB constant (independent of file count)
Temp disk: ~18 GB compressed manifest + 64 MB tar buffer (at 500M scale)
```

---

Ready for the restore pipeline? That one has its own interesting problems — the rehydration polling loop, progressive download of already-rehydrated chunks while waiting for others, tar bundle extraction for one file, and the local cache management.---

Ready for the restore pipeline? That one has its own interesting problems — the rehydration polling loop, progressive download of already-rehydrated chunks while waiting for others, tar bundle extraction for one file, and the local cache management.

User: Dedup check strategy - so you will be swapping out full shards all the time in/out of memory? is a hybrid cache mechanism memory -> disk -> remote an option?

5a. actually i think i want to properly expand the chunk index with a metadata section: original size and comressed size

re crash / scenario B : how about you put the chunk metadata in the blob metadata as well. if it crashes and you start a re-upload of the hash - dont overwrite and recover the metadata from the blob metadata. if you write the blob metadata at the end you also know that the file was uploaded in full (not stopped mid way - in that case you should overwrite it). that way you can upload the index at the end in 1 go?

ok for the restore pipeline

GitHub Copilot: 

Great refinements. Let me address each, then the restore pipeline.

---

## Dedup: Hybrid L1/L2/L3 Cache

You're right — swapping full shards in and out of memory is wasteful, especially when 500M files means many shards get hit repeatedly. Here's a proper tiered approach:

```
┌──────────────────────────────────────────────────────────────────────┐
│                    DEDUP: THREE-TIER CACHE                            │
│                                                                       │
│  L1: IN-MEMORY LRU CACHE                                            │
│  ┌─────────────────────────────────────────────────────────────────┐ │
│  │  Dictionary<ushort, HashSet<string>>                            │ │
│  │  Recent/frequent shards stay resident                           │ │
│  │  Capacity: ~1000 shards (~730 MB at full scale)                │ │
│  │  Tunable: --dedup-cache-mb (default: 512 MB)                   │ │
│  │  Eviction: LRU when budget exceeded                            │ │
│  └──────────────────────────┬──────────────────────────────────────┘ │
│                             │ miss                                    │
│                             ▼                                        │
│  L2: LOCAL DISK CACHE                                                │
│  ┌─────────────────────────────────────────────────────────────────┐ │
│  │  ~/.arius/cache/<repo>/chunk-index/<prefix>/index               │ │
│  │  All previously downloaded shards                               │ │
│  │  Read from disk → parse → promote to L1                        │ │
│  │  No size limit (disk is cheap)                                  │ │
│  └──────────────────────────┬──────────────────────────────────────┘ │
│                             │ miss                                    │
│                             ▼                                        │
│  L3: AZURE BLOB STORAGE                                             │
│  ┌─────────────────────────────────────────────────────────────────┐ │
│  │  chunk-index/<prefix>/index                                     │ │
│  │  Download → save to L2 → promote to L1                         │ │
│  │  If 404 (new shard) → create empty shard in L1                 │ │
│  └─────────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────┘
```

**Why this helps**: Depth-first enumeration means files from the same directories arrive together. Their hashes share prefixes statistically less — but at 500M files, many prefixes repeat within the same directory batch. With 65K shards and batches of 10K files, each batch touches ~10K distinct prefixes on average *but only if hashes are uniformly distributed*. In practice, a batch of 10K files might touch all 65K evenly — so the LRU helps when the *next* batch hits some of the same shards.

**The real win**: For incremental archives. Run 2 of 100K files: most of those 100K hashes existed in the previous run. The L2 disk cache has all the shards. L1 keeps the hot ones. Zero remote calls for dedup.

**Memory math with the LRU**:

```
At --dedup-cache-mb 512:
  Each parsed shard: ~730 KB (7,600 entries × 96 bytes)
  512 MB / 730 KB ≈ 700 shards in L1
  That's 700/65536 ≈ 1% of all shards

  But with LRU, the working set for a batch is what matters.
  If a batch of 10K files touches 5K distinct prefixes, 
  L1 hit rate will be low for the first pass.
  
  Better use: --dedup-cache-mb 2048 (2 GB) → ~2,800 shards → 4.3%
  Or at smaller scale (100K files, ~1,500 entries per shard, ~144 KB each):
  512 MB → ~3,500 shards → 5.3%, L2 disk covers the rest.
```

Honestly, at 500M files the L1 hit rate across random prefixes is modest. The L2 disk cache is what saves you. L1 is a nice bonus for the common case (incremental archives with small changes where the same few shards keep getting hit).

**Make `--dedup-cache-mb` configurable**, default to something reasonable like 512 MB. Users on beefy machines can crank it up.

---

## Expanded Chunk Index Metadata

Each chunk index entry becomes:

```
<content-hash> <chunk-hash> <original-size> <compressed-size>\n
```

Example:
```
a1b2c3d4e5...64hex  f7e8d9c0...64hex  4200000  1870432
```

For tar-bundled small files, every file in that tar points to the same chunk:

```
a1b2c3...(file1)  TARCHUNK01...  2048      ← original file size
d4e5f6...(file2)  TARCHUNK01...  1024      ← original file size
                                  67108864  ← compressed tar size (same for both)
```

Wait — there's an ambiguity. For a tar bundle, `compressed-size` is the size of the whole chunk (the tar). But `original-size` should be the individual file's size, not the tar's uncompressed size. Let me refine:

```
Entry format:
<content-hash> <chunk-hash> <original-size> <chunk-size>\n

Where:
  content-hash:  SHA256 of the original file content
  chunk-hash:    SHA256 of the chunk blob (= content-hash for large files, tar hash for bundled)
  original-size: size of this individual file (uncompressed, pre-archive)
  chunk-size:    size of the chunk blob on Azure (after gzip + encrypt)
```

This gives you:
- **Restore cost estimation**: sum of unique `chunk-size` values = total download volume
- **`ls` display**: `original-size` per file, directly from the index (no tree blob needed for size... actually, size is also in the tree blob metadata)
- **Dedup reporting**: "Skipped 135K files (120 GB original, deduplicated to 45 GB on Azure)"

The `original-size` is redundant with the tree blob metadata (which also stores size). But having it in the index avoids tree traversal for cost estimation. Cheap redundancy.

---

## Crash Recovery: Blob Metadata as Upload Receipt

This is a much cleaner design. Here's the flow:

```
┌──────────────────────────────────────────────────────────────────────┐
│               UPLOAD WITH BLOB METADATA RECEIPT                       │
│                                                                       │
│  Upload chunk X:                                                      │
│    1. Stream file → gzip → encrypt → upload blob body to              │
│       chunks/<chunk-hash>                                             │
│    2. AFTER body upload completes:                                    │
│       Set blob metadata:                                              │
│         x-ms-meta-content-hash: <content-hash>                       │
│         x-ms-meta-original-size: <original-size>                     │
│         x-ms-meta-chunk-size: <compressed-size>                      │
│         x-ms-meta-arius-complete: true                               │
│                                                                       │
│  The metadata write is the "commit" signal.                          │
│  No metadata = incomplete upload.                                     │
│                                                                       │
│  On re-run after crash:                                               │
│    For each hash to upload:                                           │
│      1. HEAD chunks/<chunk-hash>                                     │
│         404 → upload fresh                                           │
│         200 + has metadata → skip upload, recover index entry         │
│                              from blob metadata                       │
│         200 + NO metadata → incomplete upload, overwrite              │
│                                                                       │
│  Index shard upload: at the END, in one pass.                        │
│    Collect all entries (newly uploaded + recovered from blob meta)    │
│    Merge with existing shards                                         │
│    Upload all modified shards                                         │
│                                                                       │
└──────────────────────────────────────────────────────────────────────┘
```

**Why this is better:**

1. **Index upload is one atomic pass at the end** — no incremental shard updates during the pipeline. Simpler, fewer Azure transactions, no shard contention.
2. **Crash recovery is complete** — re-run discovers all successfully uploaded chunks via HEAD + metadata. Zero duplicate archive-tier writes.
3. **The metadata is the receipt** — no metadata means the upload was incomplete. Overwrite it. Archive tier blobs with no metadata are garbage (partial writes) that get overwritten.

**Edge case: what about tar bundles?** A tar bundle contains many files, but is one blob. The blob metadata can't hold all N content-hashes (Azure blob metadata has a size limit: 8 KB total for all metadata key-value pairs). For a tar with 32,000 files... that's way over.

**Solution for tar bundles**: Store the tar's manifest in the blob metadata as a reference instead:

```
For a large file chunk:
  x-ms-meta-content-hash: <content-hash>
  x-ms-meta-original-size: 4200000
  x-ms-meta-chunk-size: 1870432
  x-ms-meta-arius-complete: true

For a tar bundle chunk:
  x-ms-meta-manifest-hash: <SHA256 of the manifest entries>
  x-ms-meta-file-count: 312
  x-ms-meta-chunk-size: 67108864
  x-ms-meta-arius-complete: true
```

But then on crash recovery, you can't reconstruct the individual content-hash → chunk-hash mappings from the blob metadata alone for tar bundles.

**Alternative**: Write a small sidecar blob for each tar bundle:

```
chunks/<tar-chunk-hash>.manifest
  content-hash-1 original-size-1
  content-hash-2 original-size-2
  ...
```

This sidecar is uploaded *before* the tar chunk (cheap, small, Cool tier). Then:

```
Crash recovery for tar bundles:
  1. HEAD chunks/<chunk-hash> → 200 + metadata with arius-complete
  2. Download chunks/<chunk-hash>.manifest
  3. Recover all content-hash → chunk-hash mappings
```

Actually, simpler: **upload the manifest as part of the blob metadata using a compressed format.** At 32K entries × 64 hex chars = 2 MB — way over the 8 KB limit. So the sidecar approach is needed.

Or... even simpler:

```
On crash recovery:
  1. Scan chunks/ for blobs with arius-complete metadata ← this gives you all chunk-hashes
  2. For large files: content-hash IS in the metadata → recover index entry
  3. For tar bundles: download the tar, read the tar entries 
     (filenames inside are the content-hashes) → recover index entries
```

This avoids the sidecar entirely. It's more expensive (downloading tar bundles) but only on crash recovery, which is rare. And the tars are already in Hot/Cool tier during the archive run (before tier change takes effect — archive tier changes happen asynchronously).

Wait — actually tars are uploaded *directly to Archive tier* if that's the configured `--tier`. You can't read them back without rehydration. Bad.

**OK, the sidecar manifest is the clean answer:**

```
For each sealed tar:
  1. Upload manifest: chunks/<tar-chunk-hash>.manifest  (Cool tier, tiny)
  2. Upload tar blob: chunks/<tar-chunk-hash>  (Archive tier, large)
  3. Set tar blob metadata: arius-complete=true, chunk-size=..., file-count=...

Crash recovery:
  For tars with arius-complete metadata:
    Download .manifest sidecar → recover all index entries
  
  For tars WITHOUT arius-complete:
    .manifest may or may not exist.
    If manifest exists: delete it (the tar upload failed, redo both)
    If not: nothing to clean up
```

**Upload order matters**: manifest before tar. If the tar upload fails mid-way, the manifest exists but the tar has no `arius-complete` metadata. On re-run: detect incomplete tar, re-upload (overwrite the partial blob).

The `.manifest` sidecar lives in Cool tier — cheap and always readable.

```
Container layout (updated):
  chunks/                    ← Archive tier (configurable)
  chunks/*.manifest          ← Cool tier (tar sidecar manifests)
  chunks-rehydrated/         ← Hot tier, temporary
  filetrees/                 ← Cool tier
  snapshots/                 ← Cool tier
  chunk-index/               ← Cool tier
```

---

## Revised Crash Recovery Summary

```
┌─────────────────────────────────────────────────────────────────────┐
│               CRASH RECOVERY (re-run arius archive)                  │
│                                                                      │
│  1. Enumerate + Hash (same as normal)                                │
│                                                                      │
│  2. Pre-flight recovery scan:                                        │
│     List all blobs in chunks/ with metadata arius-complete=true      │
│                                                                      │
│     For each:                                                        │
│       Large file? → read content-hash from blob metadata             │
│                     → add to recovered index entries                  │
│       Tar bundle? → download .manifest sidecar                       │
│                     → add all entries to recovered index              │
│                                                                      │
│     Merge recovered entries into L1/L2 cache                         │
│     (Now dedup check knows about previously uploaded chunks)         │
│                                                                      │
│  3. Normal pipeline continues                                        │
│     Dedup finds recovered chunks → skips them                        │
│     Only truly new content gets uploaded                             │
│                                                                      │
│  4. At the end: merge ALL entries (recovered + new) into             │
│     index shards. Upload shards in one pass.                         │
│                                                                      │
│  Cost of crash: re-hash time + recovery scan (LIST + HEAD calls)     │
│  Zero duplicate archive-tier uploads.                                │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

Actually — we can even skip the pre-flight scan in the normal (no-crash) case. How do we know if a previous run crashed?

Simple: **if a snapshot was created, the run completed successfully.** On startup:

```
1. Read latest snapshot timestamp from snapshots/
2. Read local state file: ~/.arius/cache/<repo>/last-archive-run
3. If last-archive-run > latest snapshot → previous run crashed, do recovery scan
4. If equal → clean, skip recovery
5. If no local state → first run on this machine, or state lost. Do recovery scan (safe, just slower).
```

Or even simpler: always write a `.archive-in-progress` marker at the start, delete it when the snapshot is created. If the marker exists on startup → recovery path.

---

## Restore Pipeline

Here's the full picture.

### Restore Inputs

Four modes:
```
arius restore <file>                    ← one file
arius restore <file1> <file2> ...       ← multiple files
arius restore <directory>               ← everything under it
arius restore --full                    ← entire snapshot
arius restore ... -v <snapshot>         ← specific version (default: latest)
```

All modes resolve to the same underlying operation: **given a set of (relative-path, content-hash) pairs, restore them to the local filesystem.**

### Resolve Phase

```
┌──────────────────────────────────────────────────────────────────────┐
│  RESOLVE: What files need restoring?                                  │
│                                                                       │
│  1. Identify snapshot                                                 │
│     -v specified → find snapshots/<timestamp>                        │
│     default → list snapshots/, pick latest                           │
│     → Get root tree hash                                             │
│                                                                       │
│  2. Walk tree to target                                               │
│     For file restore: traverse tree blobs down to the file's dir     │
│     For dir restore: traverse to that dir, then enumerate subtree    │
│     For full restore: enumerate entire tree                          │
│                                                                       │
│  3. Produce: List<RestoreEntry>                                       │
│     { RelativePath, ContentHash, OriginalSize, Created, Modified }   │
│                                                                       │
│  Tree traversal downloads tree blobs on demand (L1→L2→L3 cache)     │
│  For a single file: ~5 tree blobs (average directory depth)          │
│  For full snapshot: all tree blobs (~50K at 500M scale, ~250 MB)     │
│                                                                       │
└──────────────────────────────────────────────────────────────────────┘
```

### Conflict Check

Before doing anything expensive:

```
For each RestoreEntry:
  Does the local file already exist?
    NO  → needs restore
    YES → hash local file, compare with ContentHash
           Match    → skip (already correct)
           Mismatch → prompt user: "local file differs, overwrite? [y/N/a(ll)]"
```

At scale (full restore of 500M files), hashing every local file to check conflicts is expensive. Optimization: if `--overwrite` flag is set, skip all conflict checks. Or if the target directory is empty, skip.

### Chunk Resolution

```
┌──────────────────────────────────────────────────────────────────────┐
│  CHUNK RESOLUTION: What chunks are needed?                            │
│                                                                       │
│  For each RestoreEntry.ContentHash:                                   │
│    Look up in chunk index → (ChunkHash, OriginalSize, ChunkSize)     │
│    (Uses same L1→L2→L3 tiered cache as archive dedup)               │
│                                                                       │
│  Group by ChunkHash (dedup: many files may share a tar chunk)         │
│                                                                       │
│  Result: List<ChunkToRestore>                                        │
│    { ChunkHash, ChunkSize, Type (large|tar), ContentHashes[] }       │
│                                                                       │
│  At this point we know:                                               │
│    - Total chunks needed                                              │
│    - Total download volume (sum of ChunkSize)                        │
│    - Which chunks are tar bundles (need extraction after download)    │
│                                                                       │
└──────────────────────────────────────────────────────────────────────┘
```

### Rehydration Check

```
┌──────────────────────────────────────────────────────────────────────┐
│  REHYDRATION CHECK                                                    │
│                                                                       │
│  For each ChunkToRestore:                                             │
│                                                                       │
│    1. Check chunks-rehydrated/<chunk-hash> exists?                   │
│       YES → already rehydrated, add to "ready" set                   │
│       NO  → continue                                                 │
│                                                                       │
│    2. Check local cache ~/.arius/cache/<repo>/chunks/<chunk-hash>    │
│       YES → already downloaded, add to "cached" set                  │
│       NO  → continue                                                 │
│                                                                       │
│    3. Check chunks/<chunk-hash> blob tier                            │
│       Hot/Cool → can download directly, add to "ready" set           │
│       Archive  → needs rehydration, add to "needs-rehydration" set   │
│                                                                       │
│  Result:                                                              │
│    cached:              12 chunks  (can restore immediately)          │
│    ready:               15 chunks  (download directly, no rehydrate)  │
│    needs-rehydration:   62 chunks  (need to start rehydration)        │
│                                                                       │
└──────────────────────────────────────────────────────────────────────┘
```

### Cost Estimation + User Confirmation

```
$ arius restore /photos/2024/ --account myaccount --key ***

  Snapshot: 2026-03-21T140000.000Z

  Files to restore:          1,247
  Already local (matching):    102  (skipped)
  Needs restore:             1,145

  Chunks:
    In local cache:             12  (34 MB)
    Hot/Cool (direct DL):       15  (890 MB)
    Needs rehydration:          62  (4.2 GB)

  Estimated costs:
    Rehydration:       $0.84 (Standard) / $6.72 (High Priority)
    Data retrieval:    $0.21           
    Download egress:   $0.45            
    ─────────────────────────────
    Total:             $1.50 (Standard) / $7.38 (High Priority)

  Rehydration priority? [S]tandard (15h) / [H]igh (1h): S
  Proceed? [y/N]: y
```

### The Restore Pipeline

After user confirms:

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         RESTORE PIPELINE                                 │
│                                                                          │
│                    ┌─────────────────────┐                               │
│                    │  Start Rehydration   │                               │
│                    │  (all 62 chunks)     │                               │
│                    │  → copy blob to      │                               │
│                    │  chunks-rehydrated/  │                               │
│                    └──────────┬──────────┘                               │
│                               │                                          │
│          ┌────────────────────┼────────────────────┐                     │
│          ▼                    ▼                     ▼                     │
│  ┌───────────────┐  ┌─────────────────┐   ┌────────────────────┐        │
│  │ Restore from  │  │ Download ready  │   │ Rehydration Poller │        │
│  │ local cache   │  │ chunks (Hot/    │   │                    │        │
│  │               │  │ Cool)           │   │ Poll every 5 min   │        │
│  │ immediate     │  │                 │   │ for newly ready    │        │
│  │               │  │ → save to local │   │ chunks             │        │
│  │               │  │   cache         │   │                    │        │
│  └──────┬────────┘  └──────┬──────────┘   │ ready? → download  │        │
│         │                  │              │ → save to cache     │        │
│         │                  │              │                    │        │
│         ▼                  ▼              └──────┬─────────────┘        │
│  ┌──────────────────────────────────────────────┴──────────────┐        │
│  │                    PROCESS CHUNK                              │        │
│  │                    (shared, ×N concurrent)                    │        │
│  │                                                               │        │
│  │  Input: downloaded chunk on local disk                        │        │
│  │                                                               │        │
│  │  1. Decrypt (if passphrase)                                   │        │
│  │  2. Gunzip                                                    │        │
│  │  3. Is it a tar?                                              │        │
│  │     YES → extract needed files from tar by content-hash name  │        │
│  │     NO  → single file, write directly                         │        │
│  │  4. For each extracted file:                                  │        │
│  │     → write to correct local path                             │        │
│  │     → set created/modified dates from tree metadata           │        │
│  │     → create pointer file (unless --no-pointers)              │        │
│  │                                                               │        │
│  └───────────────────────────────────────────────────────────────┘        │
│                                                                          │
│  After ALL files restored:                                               │
│    → Delete local chunk cache (--keep-cache to retain)                   │
│    → Delete chunks-rehydrated/ blobs on Azure                            │
│                                                                          │
└─────────────────────────────────────────────────────────────────────────┘
```

### The Three Concurrent Streams

The key insight: **you don't wait for all rehydration to complete before starting restores.** Three independent streams feed into the same processing pool:

```
TIME ──────────────────────────────────────────────────────────────────▶

Stream 1: Local cache (instant)
  ████ done

Stream 2: Hot/Cool direct download (~minutes)
  ░░░░████████████ done

Stream 3: Rehydration batches (~hours, progressive)
  ░░░░░░░░░░░░░████░░░░░████░░░░░████████████████ done
                  ↑        ↑
           first batch   second batch
           rehydrated    rehydrated

Processing:
  ████████████████░░░░████░░░░░████████████████████
  ↑               ↑        ↑
  cache+hot/cool  gap      rehydration batches
  restored        (waiting) trickle in
```

The user sees files appearing progressively:

```
  Restoring...
    From cache:      102 / 102   ████████████████████  (34 MB)     ✓
    Direct download: 268 / 268   ████████████████████  (890 MB)    ✓
    Rehydrating:      38 / 62    ████████████████░░░░  (2.7 / 4.2 GB)
    
    Files restored:  847 / 1,145  ████████████████░░░  73.9%
    
    Next poll in: 3m 42s
    Estimated completion: ~4h 12m remaining
```

### Rehydration Poller Detail

```csharp
// Pseudocode
async Task PollRehydration(
    List<string> pendingChunkHashes, 
    Channel<DownloadedChunk> readyChannel,
    CancellationToken ct)
{
    while (pendingChunkHashes.Count > 0 && !ct.IsCancellationRequested)
    {
        var stillPending = new List<string>();
        
        foreach (var hash in pendingChunkHashes)
        {
            var props = await blobClient.GetPropertiesAsync(hash);
            
            if (props.ArchiveStatus == "rehydrate-pending-to-hot")
                stillPending.Add(hash);  // not ready yet
            else
            {
                // Ready! Download from chunks-rehydrated/
                var localPath = await DownloadToCache(hash);
                await readyChannel.Writer.WriteAsync(
                    new DownloadedChunk(hash, localPath));
            }
        }
        
        pendingChunkHashes = stillPending;
        
        if (stillPending.Count > 0)
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
    }
    
    readyChannel.Writer.Complete();
}
```

**Throttling concern**: `GetPropertiesAsync` on 62 blobs every 5 minutes = 12 calls/minute. Negligible. At 10K chunks, that's 2K calls/minute — still fine, Azure can handle it.

### Tar Extraction — Only What You Need

When restoring a single file from a tar bundle:

```
1. ChunkHash resolves to a tar (content type: application/...+tar+gzip)
2. Download entire tar (no way around it — archive tier, all or nothing)
3. Decrypt → gunzip → open tar stream
4. Iterate tar entries until you find the content-hash filename you need
5. Extract just that entry, discard the rest

BUT: keep the downloaded tar in cache. If another file in the same 
restore set needs a different entry from the same tar → cache hit, 
extract from cached tar. No re-download.
```

```
For a tar chunk needed by 3 files in the restore set:
  Download once → extract file A → cache hit → extract file B → extract file C
  Cost: 1 download of 64 MB, not 3
```

### Concurrency & Race Conditions (Restore)

```
┌───────────────────┬──────────────────┬─────────────────────────────┐
│ Resource          │ Contention       │ Resolution                  │
├───────────────────┼──────────────────┼─────────────────────────────┤
│ Rehydration start │ Same chunk       │ Start rehydrate-to-hot      │
│                   │ requested twice? │ request is idempotent.      │
│                   │                  │ Dedup by ChunkHash before   │
│                   │                  │ issuing requests.           │
│                   │                  │                             │
│ Local chunk cache │ Tar downloaded,  │ Write to temp file, rename  │
│                   │ two files need   │ atomically. Concurrent      │
│                   │ extraction       │ readers of same tar: open   │
│                   │                  │ separate file handles.      │
│                   │                  │                             │
│ Local file write  │ Same file from   │ Can't happen — each output  │
│                   │ two chunks?      │ file maps to exactly one    │
│                   │                  │ content hash, one chunk.    │
│                   │                  │                             │
│ chunks-rehydrated │ cleanup while    │ Cleanup only after ALL      │
│ cleanup           │ still polling?   │ restores complete. Single   │
│                   │                  │ cleanup pass at end.        │
│                   │                  │                             │
│ Pointer files     │ Same as archive  │ No contention, unique paths │
└───────────────────┴──────────────────┴─────────────────────────────┘
```

### Crash Recovery (Restore)

Restore is inherently resumable:

```
Crash during rehydration:
  Re-run same command.
  → Rehydration requests already in-flight continue (Azure-side, async)
  → Poll finds them eventually
  → Already-rehydrated chunks are downloaded
  → Already-downloaded chunks are in local cache
  Cost: zero

Crash during download:
  Re-run same command.
  → Local cache has what was already downloaded
  → Conflict check finds already-restored files, skips them
  → Remaining files get downloaded
  Cost: zero (assuming partial downloads are detected by size/hash)

Crash during extraction:
  Re-run same command.
  → Conflict check: restored files match → skip
  → Partially written file: hash mismatch → overwrite
  Cost: re-extract from cached chunk
```

**Key**: the only non-idempotent cost is rehydration — but Azure remembers pending rehydration requests. A re-run just picks up where it left off.

### Cleanup

After all files restored:

```
1. Delete blobs in chunks-rehydrated/ on Azure
   → These are Hot tier, costing money per GB per day
   → Prompt: "Delete 4.2 GB of rehydrated chunks? [Y/n]"

2. Local cache:
   Default: delete chunks from cache (save disk space)
   --keep-cache: retain for future restores
   
3. Tree blob cache: always keep (immutable, useful for future ls/restore)
```

---

### Full Restore Pipeline Summary

```
INPUT: user command + snapshot version

    Resolve Snapshot
         │
    Walk Tree → List<RestoreEntry>
         │
    Conflict Check (local files)
         │
    Chunk Resolution (index lookup)
         │
    Rehydration Check (what's where?)
         │
    Cost Estimation → USER CONFIRMATION
         │
    ┌────┴──────────────────────────────────────┐
    │                                            │
    │  Three parallel streams feed into          │
    │  a shared processing channel:              │
    │                                            │
    │  ┌──────────────┐                          │
    │  │ From Cache   │──┐                       │
    │  └──────────────┘  │                       │
    │  ┌──────────────┐  ├──▶ Channel<Chunk>     │
    │  │ Direct DL    │──┤       │               │
    │  └──────────────┘  │       ▼               │
    │  ┌──────────────┐  │   Process (×N)        │
    │  │ Rehydration  │──┘   decrypt→gunzip      │
    │  │ Poller       │      →untar?→write       │
    │  └──────────────┘      →set dates          │
    │                        →pointer file       │
    │                                            │
    └────────────────────────────────────────────┘
         │
    Cleanup (rehydrated blobs + local cache)
         │
    DONE
```

---

That's the full picture for both pipelines. The requirements doc captures the *what*, and this exploration has defined the *how* at a design level. The main things we've resolved:

- Tiered dedup cache (L1 memory LRU → L2 disk → L3 Azure)
- Chunk index with full metadata (content-hash, chunk-hash, original-size, chunk-size)
- Blob metadata as upload receipt + sidecar manifests for tars
- Index upload at end in one pass
- Crash-recovery for both archive and restore
- Three-stream progressive restore
- Memory bounded at ~30 MB for archive, similar for restore
- All shared resources and race conditions mapped

Feels solid enough to formalize. Want to capture this in a proposal/design?

User: let s revise the sidecar approach. one challenge we havent solved yet (i think?) is when it crashes during a run. the next run may see the tar being built slightly different leading to orphaned tars, right?

how about a 'thin' chunk that has the name of the content has, and inside it points to the actual tar hash where it is stored? you can then store metadata on the thin chunk

would that solve it?

for the tar metadata, i want the original size and the compressed size of the file - it will be an approximation (eg. the amount of bytes the stream has advanced after writing the file to it)?


for the restore pipeline, the restore run should just start the rehydration. dont wait/poll for the actual rehydration -  the idempotent command will be run after ~18 hours and then fetch the remaining items

if we re doing a full restore, would the cache eventually take the same size as the full restored content? that may pose a disk size issue. can you restore them by chunk so the local cache stays small? do you even need a chache?

GitHub Copilot: 

These are all sharp observations. Let me work through each.

---

## Orphaned Tars on Crash

You're exactly right. Here's the scenario I missed:

```
Run 1 (crashes mid-upload):
  Tar A = {file1, file2, file3}  → hash: TAR_A1  → uploaded, incomplete
  
Run 2 (file3 was modified between runs):
  Tar B = {file1, file2, file3'} → hash: TAR_B2  → uploaded, complete
  
  TAR_A1 sits orphaned in archive tier forever.
  At $0.00012/month for 64MB, it's cheap but messy.
```

Even without file modifications, the tar builder is non-deterministic — files arrive from the hash stage in parallel, so ordering can differ between runs. Different order → different tar bytes → different hash → orphan.

## Thin Chunks — Yes, This Is Cleaner

The idea: every content-hash gets its own blob in `chunks/`, regardless of whether it's a large file or a small file bundled in a tar.

```
LARGE FILE:
  chunks/<content-hash>          ← the actual gzipped+encrypted file data
  blob metadata:
    arius-type: large
    original-size: 4200000
    chunk-size: 1870432          ← compressed+encrypted size
    arius-complete: true

SMALL FILE (bundled in tar):
  chunks/<content-hash>          ← THIN: ~80 bytes, just the tar hash
  blob metadata:
    arius-type: thin
    tar-hash: <tar-chunk-hash>
    original-size: 2048
    compressed-size: 847         ← approx bytes written to tar gzip stream
    arius-complete: true

  chunks/<tar-chunk-hash>        ← the actual tar+gzip+encrypted bundle
  blob metadata:
    arius-type: tar
    file-count: 312
    chunk-size: 67108864
    arius-complete: true
```

### Why This Solves the Crash Problem

```
Archive pipeline for small files:
  1. Build tar in memory/temp file
  2. Seal tar → compute tar-hash
  3. Upload tar blob to chunks/<tar-hash>, set arius-complete
  4. For each file in the sealed tar:
     Upload thin chunk to chunks/<content-hash>, 
       body = tar-hash, 
       metadata = {type:thin, tar-hash, original-size, compressed-size}
       set arius-complete

Crash scenarios:

  Crash during step 3 (tar upload):
    Tar blob: exists but no arius-complete → incomplete
    Thin chunks: none created yet
    Re-run: files get re-bundled (possibly different tar), new tar uploaded.
    Old incomplete tar: no thin chunks reference it, but it's just a blob sitting there.
    Cost: one orphan blob (64 MB). Rare, cheap.

  Crash during step 4 (thin chunk upload):
    Tar blob: complete (arius-complete=true) ✓
    Thin chunks: some created, some not
    Re-run: 
      - Files whose thin chunk exists → HEAD check → found, skip
      - Read thin chunk → tar-hash → HEAD tar blob → arius-complete → all good
      - Files whose thin chunk is MISSING → re-bundled in new tar
    Old tar: some thin chunks point to it, it remains valid for those files.
    New tar: contains the remaining files.
    Result: correct, no data loss. Some tars are partially referenced. Fine.
```

### What This Changes

```
BEFORE (sidecar approach):
  chunk-index: content-hash → chunk-hash (tar-hash for small files)
  Recovery: scan chunks/ + download sidecar manifests

AFTER (thin chunk approach):
  chunk-index: content-hash → content-hash (uniform for ALL files)
  Recovery: HEAD chunks/<content-hash> → found? → done
  
  For restore: read thin chunk body → get tar-hash → download tar
```

**The chunk index becomes simpler**: every entry maps `content-hash → content-hash`. The index is now purely an existence check + metadata store. The indirection (content-hash → tar-hash) lives in the thin chunk blob itself.

Actually... wait. If the chunk index just maps content-hash → content-hash, what's the point of the index? It becomes a set, not a map. Let me reconsider the index design:

```
OPTION A: Index still maps content-hash → chunk-hash
  Large file: content-hash → content-hash (same, it IS the chunk)
  Small file: content-hash → tar-hash (the lookup goes: 
    index → tar-hash → download tar → find file by content-hash name)
  Metadata in index: original-size, compressed-size
  
  Pro: index is the single lookup, no need to read the thin chunk for restore
  Con: index is still the bottleneck, crash still needs index recovery

OPTION B: Index is just existence + metadata. Thin chunk is the indirection.
  Index entry: content-hash, original-size, compressed-size, chunk-size
  For restore: 
    Large file → download chunks/<content-hash> directly
    Small file → download chunks/<content-hash> (thin, 80 bytes) 
                 → read tar-hash → download chunks/<tar-hash>
  
  Pro: index upload is less critical (thin chunks are the source of truth)
  Con: extra round-trip for small file restore (download thin chunk first)
```

**I'd go with Option A: index still maps content-hash → chunk-hash.** The index is the fast lookup for both dedup and restore. The thin chunk is the crash-recovery fallback and the worst-case-recovery path. During normal operation, you never need to read a thin chunk — the index tells you everything.

```
Chunk Index entry (revised):
  <content-hash> <chunk-hash> <original-size> <compressed-size>\n

  Large file: a1b2c3... a1b2c3... 4200000 1870432
              (same)    (same)
  
  Small file: d4e5f6... TARHASH1... 2048 847
              (content) (tar blob)
```

**Crash recovery with thin chunks:**

```
Re-run after crash:
  1. For each file to archive:
     HEAD chunks/<content-hash>
     → 200 + arius-complete → already uploaded
       If arius-type=large: recover index entry from blob metadata
       If arius-type=thin: read body (tar-hash) + metadata → recover index entry
     → 200 + NO arius-complete → incomplete, overwrite
     → 404 → needs upload

  2. Recovered entries go into in-memory index
  3. Normal pipeline continues (dedup against recovered + cached index)
  4. At end: upload all index shards in one pass
```

### Compressed Size in Tar — The Approximation

You want to know how many bytes each individual file contributed to the compressed tar stream. Since gzip is a continuous stream, you can't get exact per-file compression:

```
Tar stream:
  [tar header 1][file 1 bytes][padding][tar header 2][file 2 bytes][padding]...
  
  ↓ gzip (streaming)
  
  [compressed bytes...................................................]

  The gzip dictionary spans across file boundaries.
  File 2's bytes might compress better because of patterns in file 1.
```

**The approximation**: Track the gzip output position before and after writing each file:

```csharp
long positionBefore = gzipStream.TotalBytesWritten;  // or track on a counting wrapper
tarWriter.WriteEntry(fileEntry);
long positionAfter = gzipStream.TotalBytesWritten;
long compressedSize = positionAfter - positionBefore;
```

This includes the tar header overhead (~512 bytes per entry) and padding in the compressed output. It's not exact per-file compression, but it's a good enough proxy for:
- Cost estimation ("restoring this 2 KB file will download ~847 bytes of a 64 MB tar")
- Storage reporting

If gzip hasn't flushed yet, the delta might be zero or lumpy. You could flush after each file, but that hurts compression ratio. 

**Better approach**: after the tar is sealed and fully compressed, compute proportional sizes:

```
Total uncompressed: sum of all file sizes + tar overhead = 62 MB
Total compressed: 41 MB (actual gzip output)
Compression ratio: 41/62 = 0.661

File 1 (2048 bytes uncompressed):
  compressed-size ≈ 2048 × 0.661 = 1354 bytes (proportional estimate)

File 2 (4096 bytes uncompressed):
  compressed-size ≈ 4096 × 0.661 = 2707 bytes
```

This is less accurate per-file but gives a reasonable estimate for cost calculations. And it's simple — no stream position tracking needed.

**I'd go with proportional**: compute after the tar is sealed. `compressed_size_estimate = original_size * (total_compressed / total_uncompressed)`. Store that in the thin chunk metadata and index entry.

---

## Restore: Kick Off and Come Back

Much simpler UX:

```
$ arius restore /photos/2024/ --account myaccount --key ***

  Snapshot: 2026-03-21T140000.000Z

  Files to restore:          1,145

  Chunks:
    In local cache:             12  (34 MB)     → will restore now
    Hot/Cool (direct DL):       15  (890 MB)    → will restore now
    Needs rehydration:          62  (4.2 GB)    → will start rehydration

  Estimated rehydration cost:  $0.84 (Standard) / $6.72 (High Priority)
  Rehydration priority? [S]tandard / [H]igh: S
  Proceed? [y/N]: y

  Restoring from cache...         102 files  ✓
  Downloading from Hot/Cool...    268 files  ✓
  
  Started rehydration for 62 chunks (Standard priority).
  
  Re-run this command in ~15 hours to complete the restore.
  875 files remaining.

$ # ... 18 hours later ...

$ arius restore /photos/2024/ --account myaccount --key ***

  Snapshot: 2026-03-21T140000.000Z

  Files to restore:          1,145
  Already restored locally:    270  (hash verified, skipped)
  
  Chunks:
    Rehydrated & ready:         58  (3.9 GB)   → will restore now
    Still rehydrating:           4  (0.3 GB)    → will re-request
  
  Proceed? [y/N]: y

  Downloading rehydrated chunks...  821 files  ✓
  Re-requested rehydration for 4 chunks.
  
  Re-run this command later for 54 remaining files.

$ # ... later ...

  All 1,145 files restored.  ✓
  
  Delete 4.2 GB of rehydrated chunks from Azure? [Y/n]: y
  Cleanup complete.
```

**This is idempotent by design.** Every run:
1. Resolves what needs restoring
2. Checks what's already restored locally (hash match → skip)
3. Downloads whatever is available now
4. Kicks off rehydration for whatever isn't
5. Reports what's remaining

No polling loop, no background process, no long-running CLI. Run it, come back, run it again.

---

## Cache Size Problem — Stream and Discard

You're right. For a full 1 TB restore, a persistent cache means:
- 1 TB of cached chunks on disk
- 1 TB of restored files on disk
- = 2 TB disk needed

**The answer: no cache. Process chunks as a stream, then discard.**

```
┌──────────────────────────────────────────────────────────────────────┐
│  STREAM-AND-DISCARD RESTORE                                          │
│                                                                       │
│  1. Group all RestoreEntries by chunk-hash                           │
│     (already done in chunk resolution)                                │
│                                                                       │
│  2. For each available chunk (in any order):                         │
│                                                                       │
│     LARGE FILE CHUNK:                                                │
│       Download → decrypt → gunzip → stream DIRECTLY to final path    │
│       Peak temp disk: 0 (streamed end-to-end)                        │
│                                                                       │
│     TAR BUNDLE CHUNK:                                                │
│       Download to temp file → decrypt → gunzip → open tar reader     │
│       For each tar entry matching a needed content-hash:             │
│         Extract → write to final path                                │
│       Delete temp file                                               │
│       Peak temp disk: ~64 MB (one tar)                               │
│                                                                       │
│  3. No cache. Each chunk is processed and discarded.                 │
│                                                                       │
│  Peak temp disk: max(single chunk) ≈ 64 MB                          │
│                                                                       │
└──────────────────────────────────────────────────────────────────────┘
```

**For large files**, the download-decrypt-gunzip pipeline can stream directly to the final file path. The data flows through memory buffers (~512 KB) but never touches temp disk. This is the ideal path.

**For tar bundles**, you need random-access to extract specific entries. Two options:

**(A)** Download to temp file, then extract:
```
Azure → temp.tar.gz.enc → decrypt → gunzip → tar → extract files → delete temp
Peak: 64 MB temp + extracted files (but extracted files ARE the final output)
```

**(B)** Stream the tar and extract on the fly:
```
Azure → decrypt stream → gunzip stream → tar reader → 
  for each entry:
    if needed → write to final path
    if not needed → skip/discard
```

Option B is better — **no temp file at all**. Tar is a sequential format; you can iterate entries in a single pass. The tar reader gives you entry headers, you check if the filename (content-hash) is in your needed set, and either extract or skip.

```
Peak temp disk with streaming tar extraction: 0
Peak memory: ~512 KB stream buffers
```

The only downside: if the same tar is needed for two separate restore runs (run 1 restores files A and B from tar X, run 2 needs file C from tar X), you re-download the tar. But since each run processes ALL needed files from each chunk upfront (grouped by chunk-hash), this only happens across runs, not within a run.

**When would you still want a cache?**

One scenario: restoring the same tar in the same run but from different code paths. Can't happen with the grouped approach. Another: partial failure mid-extraction — you'd have to re-download. Given that tars are max 64 MB and we're already re-downloading from `chunks-rehydrated/` (Hot tier, fast), this is fine.

**Decision: no persistent cache for chunks. Stream and discard.**

The cache directory still exists for:
- Tree blobs (always cached, immutable, small)
- Chunk index shards (cached for dedup, useful across runs)
- Not for chunk data during restore

```
~/.arius/cache/<repo>/
    chunk-index/     ← cached index shards (useful across archive runs)
    filetrees/       ← cached tree blobs (immutable, useful for ls/restore)
    (no chunks/)     ← chunks are streamed, not cached
```

---

## Revised Restore Pipeline

```
┌─────────────────────────────────────────────────────────────────────────┐
│                    RESTORE PIPELINE (REVISED)                            │
│                                                                          │
│  INPUT: user command + snapshot version                                  │
│                                                                          │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ RESOLVE                                                          │    │
│  │  Load snapshot → walk tree → List<RestoreEntry>                 │    │
│  │  Conflict check (local files: skip if hash matches)             │    │
│  │  Chunk resolution (index lookup → group by chunk-hash)          │    │
│  │  Rehydration check (what's available now?)                      │    │
│  │  Cost estimation → USER CONFIRMATION                            │    │
│  └──────────────────────────────┬──────────────────────────────────┘    │
│                                 │                                        │
│  ┌──────────────────────────────▼──────────────────────────────────┐    │
│  │ PHASE 1: Process available chunks (immediate)                    │    │
│  │                                                                  │    │
│  │  For each available chunk (Hot/Cool/already rehydrated):        │    │
│  │                                                                  │    │
│  │    ┌──────────────┐    ┌──────────────┐    ┌──────────────────┐│    │
│  │    │ Download     │───▶│ Decrypt?     │───▶│ Gunzip           ││    │
│  │    │ (streaming)  │    │ (streaming)  │    │ (streaming)      ││    │
│  │    └──────────────┘    └──────────────┘    └───────┬──────────┘│    │
│  │                                                    │           │    │
│  │                                            ┌───────▼─────────┐ │    │
│  │                                            │ Large? Stream   │ │    │
│  │                                            │ to final path   │ │    │
│  │                                            │                 │ │    │
│  │                                            │ Tar? Iterate    │ │    │
│  │                                            │ entries, extract│ │    │
│  │                                            │ needed files    │ │    │
│  │                                            └─────────────────┘ │    │
│  │                                                                  │    │
│  │  Concurrency: N parallel chunk processors (network-bound)       │    │
│  │  Peak memory: N × 512 KB stream buffers                         │    │
│  │  Peak temp disk: 0                                               │    │
│  │                                                                  │    │
│  └──────────────────────────────┬──────────────────────────────────┘    │
│                                 │                                        │
│  ┌──────────────────────────────▼──────────────────────────────────┐    │
│  │ PHASE 2: Start rehydration for archive-tier chunks               │    │
│  │                                                                  │    │
│  │  For each chunk needing rehydration:                             │    │
│  │    Start-Copy-Blob to chunks-rehydrated/ with rehydrate priority│    │
│  │    (Standard or High, user-selected)                            │    │
│  │                                                                  │    │
│  │  Log: "Rehydration started for N chunks. Re-run in ~15h."      │    │
│  │                                                                  │    │
│  └──────────────────────────────────────────────────────────────────┘    │
│                                                                          │
│  ┌──────────────────────────────────────────────────────────────────┐   │
│  │ ON SUBSEQUENT RUN (idempotent):                                   │   │
│  │                                                                   │   │
│  │  Same command → resolve → conflict check finds restored files →  │   │
│  │  skip them → check rehydration status → download newly ready →   │   │
│  │  re-request still-pending → report remaining                     │   │
│  │                                                                   │   │
│  │  When all files restored: prompt to clean up chunks-rehydrated/  │   │
│  │                                                                   │   │
│  └──────────────────────────────────────────────────────────────────┘   │
│                                                                          │
└─────────────────────────────────────────────────────────────────────────┘
```

### Concurrency for Restore

```
┌─────────────────┬──────────────┬─────────────────────────────────┐
│ Stage           │ Concurrency  │ Notes                           │
├─────────────────┼──────────────┼─────────────────────────────────┤
│ Tree walk       │ 1            │ Sequential traversal            │
│ Conflict check  │ N            │ Parallel hash checks            │
│ Index lookup    │ batched      │ Same L1/L2/L3 as archive        │
│ Chunk download  │ N (4-8)     │ Network-bound                   │
│   + process     │              │ Each: download→decrypt→         │
│                 │              │ gunzip→extract, streaming       │
│ Rehydrate start │ N            │ Fire-and-forget API calls       │
│ File write      │ 1 per chunk  │ Each chunk processor writes     │
│                 │              │ its own output files            │
│ Pointer write   │ alongside    │ After each file restored        │
└─────────────────┴──────────────┴─────────────────────────────────┘
```

### Edge Cases

| Scenario | Handling |
|----------|----------|
| Tar contains 312 files but we only need 1 | Stream through tar, extract the one, skip 311. Download cost = full tar. But streaming = no temp disk. |
| Same tar needed for files in different directories | All grouped under same chunk-hash. One download, extract all needed in single pass. |
| Rehydration failed (Azure timeout, 7-day limit) | Next run detects blob not in chunks-rehydrated/. Re-requests rehydration. |
| Rehydration succeeded but blob expired before download | Re-request. chunks-rehydrated/ blobs are Hot tier, no auto-expiry unless user deleted. |
| Large file (10 GB) download interrupted mid-stream | Partially-written local file has wrong hash. Next run: conflict check → mismatch → overwrite. Re-download from scratch (resume not worth the complexity). |
| Disk full during extraction | Fail gracefully on write error. Next run picks up remaining files. |

---

## Updated Container Layout

```
chunks/                      ← Archive tier (configurable)
  <content-hash>             ← large file: gzip+encrypt data
  <content-hash>             ← small file: thin pointer (body = tar-hash)
  <tar-hash>                 ← tar bundle: tar+gzip+encrypt data

chunks-rehydrated/           ← Hot tier, temporary (restore only)
filetrees/                   ← Cool tier
snapshots/                   ← Cool tier  
chunk-index/                 ← Cool tier
```

One thing to note: `chunks/` now has three kinds of blobs, distinguished by `arius-type` metadata: `large`, `thin`, `tar`. All encrypted if passphrase is set.

For worst-case recovery of a small file:
```
1. Read pointer file → content-hash
2. Download chunks/<content-hash> (thin) → tar-hash  
3. Download chunks/<tar-hash> (tar bundle)
4. openssl enc -d ... | gunzip | tar x
5. Find file by content-hash name in extracted tar
```

One extra step vs. large files, but still entirely openssl/gzip/tar recoverable.

---

## Summary of What Changed This Round

| Topic | Before | After |
|-------|--------|-------|
| Tar manifest | Sidecar .manifest blob | Thin chunk per small file in chunks/ |
| Crash recovery | Scan sidecars | HEAD each content-hash, read thin chunk body |
| Index entry | content-hash → chunk-hash (tar-hash for small) | Same — index still provides the mapping |
| Compressed size in tar | Not tracked | Proportional estimate stored in metadata |
| Restore polling | Long-running poll loop | Kick off rehydration + exit. Re-run later. |
| Chunk cache | Persistent, could grow to 1 TB | No cache. Stream and discard. |
| Peak temp disk (restore) | Unbounded | ~0 (streaming) |

---

That covers the main design. The `ls` command is comparatively simple (walk tree blobs, display). Want to cover that, or does this feel ready to capture in a formal proposal/design?

User: ok for proportional

go for option B

before formalizing, should we explore testing strategy?

GitHub Copilot: 

Yes, testing strategy is worth exploring carefully — this is your childhood backups after all. Let me map out what needs testing, at what level, and the hard parts.

---

## The Testing Pyramid for Arius

```
                         ╱╲
                        ╱  ╲
                       ╱ E2E╲        Real Azure Blob Storage
                      ╱ (few)╲       Full archive → restore cycle
                     ╱────────╲
                    ╱Integration╲    Azurite container (TestContainers)
                   ╱  (moderate) ╲   Full pipeline with real blob storage
                  ╱───────────────╲
                 ╱    Unit Tests    ╲  Pure logic, no I/O
                ╱   (many, fast)    ╲  Crypto, hashing, tar building,
               ╱─────────────────────╲ tree construction, index parsing
              ╱    Architecture Tests  ╲ Dependency rules, layer boundaries
             ╱─────────────────────────╲
```

Let me work through each layer.

---

## Layer 1: Unit Tests (Pure Logic)

These test individual components with no I/O, no Azure, no filesystem. Fast, deterministic.

### Encryption/Hashing

This is the most critical unit to test — if encryption is wrong, data is unrecoverable.

```
┌────────────────────────────────────────────────────────────────────┐
│ CRYPTO TESTS — most critical tests in the entire system            │
│                                                                     │
│ ✓ SHA256(data) produces known hash for known input                 │
│ ✓ SHA256(passphrase + data) produces known hash                    │
│ ✓ SHA256(passphrase + data) matches previous Arius version output  │
│ ✓ AES-256-CBC encrypt → decrypt roundtrip                          │
│ ✓ AES-256-CBC output is openssl-compatible:                        │
│     encrypt in C# → decrypt with openssl CLI → same bytes          │
│ ✓ AES-256-CBC from openssl CLI → decrypt in C# → same bytes       │
│ ✓ Encrypted blob starts with "Salted__" (8 bytes)                  │
│ ✓ PBKDF2 with SHA-256, 10K iterations matches openssl              │
│ ✓ Different passphrases produce different hashes                   │
│ ✓ Empty passphrase = no encryption (plaintext mode)                │
│ ✓ Streaming encrypt: encrypt 10 GB stream without loading in mem   │
│ ✓ Streaming decrypt: same                                          │
│                                                                     │
│ BACKWARDS COMPAT (golden file tests):                               │
│ ✓ Known file + known passphrase → known encrypted bytes            │
│   (capture actual output from previous Arius, store as test data)  │
│ ✓ Decrypt chunk from previous Arius version → correct content      │
│                                                                     │
└────────────────────────────────────────────────────────────────────┘
```

The golden file tests are vital. Capture actual encrypted chunks from the previous Arius version and include them as test resources. If these ever break, you know you've broken backwards compatibility.

### GZip Streaming

```
✓ Gzip → gunzip roundtrip (various sizes: 0, 1 byte, 1 KB, 1 MB, 100 MB)
✓ Gzip stream output is standard gzip (gunzip CLI can decompress it)
✓ Streaming: gzip 10 GB without loading in memory
✓ Encrypt(Gzip(data)) → Gunzip(Decrypt(data)) roundtrip
```

### Tar Builder

```
✓ Add files → seal → extract → same content
✓ Files named by content-hash (not original path)
✓ Seal at target size threshold
✓ Seal with partial fill (last tar < target size)
✓ Tar with 1 file works
✓ Tar with 10,000 files works
✓ Tar output is standard tar: tar CLI can list/extract
✓ Full pipeline: tar → gzip → encrypt → decrypt → gunzip → untar → same files
✓ Proportional size estimation is within 50% of actual (sanity check)
```

### Chunk Index Shard

```
✓ Parse shard: "<content-hash> <chunk-hash> <original-size> <compressed-size>\n"
✓ Serialize shard: same format
✓ Roundtrip: serialize → parse → same entries
✓ Merge: existing shard + new entries → combined shard
✓ Duplicate content-hash: last write wins (or error?)
✓ Empty shard (new prefix, no entries yet)
✓ Large shard (10K entries) parses correctly
✓ Shard prefix routing: content-hash "a1b2c3..." → shard "a1"
```

### File Tree Blob

```
✓ Serialize directory entries to JSON
✓ Parse JSON back to entries
✓ Tree hash: SHA256(json bytes) is deterministic
✓ Tree hash with passphrase: SHA256(passphrase + json bytes)
✓ Unchanged directory → same tree hash
✓ Metadata change (touch file) → different tree hash
✓ Entry ordering is deterministic (sorted by name)
✓ Extensible: unknown JSON fields are preserved (future-proof)
```

### Snapshot Manifest

```
✓ Serialize: root hash, timestamp, file count, total size, version
✓ Parse: roundtrip
✓ Timestamp format: UTC ISO 8601
```

### FilePair Logic

```
✓ Binary + pointer (matching hash) → FilePair, hash from binary
✓ Binary + pointer (mismatched hash) → FilePair, hash from binary, pointer marked stale
✓ Binary only → FilePair, no pointer yet
✓ Pointer only → FilePair, hash from pointer, thin archive mode
✓ .pointer.arius naming: "file.txt" → "file.txt.pointer.arius"
✓ Naming collision: file IS named "x.pointer.arius" → treated as pointer, warn if invalid content
✓ Path normalization: backslash → forward slash, reserved chars
```

### Size Router

```
✓ File ≥ threshold → large pipeline
✓ File < threshold → tar builder
✓ Threshold is configurable
✓ Boundary: file exactly at threshold → large (or small? Define it.)
```

---

## Layer 2: Integration Tests (Azurite)

These use TestContainers to spin up Azurite (Azure Blob Storage emulator). Real blob operations, no real Azure costs.

### The Black Box Approach

Your requirements doc says to treat the system as a black box from the Mediator command level. This is the right instinct. Each test:

1. Set up local filesystem (temp directory with test files)
2. Set up Azurite container (empty)
3. Execute Mediator command (Archive/Restore/Ls)
4. Assert results

```
┌────────────────────────────────────────────────────────────────────┐
│ TEST FIXTURE                                                        │
│                                                                     │
│  ┌──────────────┐    ┌──────────────┐    ┌──────────────────┐     │
│  │ Temp Local   │    │ Arius.Core   │    │ Azurite          │     │
│  │ Directory    │◄──▶│ Mediator     │◄──▶│ (TestContainers) │     │
│  │              │    │ Commands     │    │                  │     │
│  └──────────────┘    └──────────────┘    └──────────────────┘     │
│                                                                     │
│  Test creates         Test invokes         Test verifies            │
│  files here           commands here        blobs here               │
│                                                                     │
└────────────────────────────────────────────────────────────────────┘
```

### Scenario Matrix

Here's where the edge case thinking really matters. Let me map the scenarios systematically.

**Archive → Verify Blob State:**

```
BASIC ARCHIVE SCENARIOS
───────────────────────
✓ Single large file → 1 chunk in chunks/, 1 index entry, 
    1 tree blob, 1 snapshot, 1 pointer file
✓ Single small file → 1 thin chunk + 1 tar chunk, 1 index entry,
    1 tree blob, 1 snapshot, 1 pointer file
✓ Mix of large and small files
✓ Nested directory structure → tree blobs per directory
✓ Empty directory → tree blob with no file entries (or skip?)
✓ Archive with --no-pointers → no .pointer.arius files created
✓ Archive with passphrase → all blobs encrypted, hashes differ
✓ Archive without passphrase → plaintext gzip
```

**Archive → Restore → Verify Content:**

```
ROUNDTRIP SCENARIOS
───────────────────
✓ Archive folder → restore to different location → byte-identical files
✓ Archive with encryption → restore → byte-identical
✓ Archive with --tier Hot → restore (no rehydration needed)
✓ Large file (>1 MB) roundtrip
✓ Small file (<1 MB) roundtrip through tar bundling
✓ Mix of sizes roundtrip
✓ Files with special characters in names: spaces, unicode, 
    brackets, dots, very long names
✓ Deeply nested path (20+ levels)
✓ Binary files (images, zip files — no corruption through gzip/encrypt)
✓ Empty file (0 bytes) roundtrip
✓ File exactly at small-file-threshold boundary
```

**Deduplication:**

```
DEDUP SCENARIOS
───────────────
✓ Two identical files in different dirs → one chunk uploaded, 
    both in snapshot tree
✓ Archive twice with no changes → zero new uploads on second run
✓ Archive, modify one file, archive again → one new chunk, 
    old chunk still exists, two snapshots
✓ Archive, delete a file, archive again → new snapshot without file, 
    old snapshot still has it
```

**Incremental Archive:**

```
INCREMENTAL SCENARIOS
─────────────────────
✓ Add new files between runs → only new files uploaded
✓ Delete files between runs → new snapshot excludes them
✓ Modify file content → new chunk uploaded, pointer updated
✓ Rename file (same content) → no new chunk, new tree structure
✓ Move file to different directory → no new chunk, new tree structure
✓ Touch file (same content, new mtime) → new tree blob (metadata change),
    no new chunk
```

**Thin Archive (--remove-local):**

```
THIN ARCHIVE SCENARIOS
──────────────────────
✓ Archive with --remove-local → binaries deleted, only pointers remain
✓ Archive thin repo → snapshot created from pointer files alone
✓ Restore from thin archive → files restored correctly
✓ Pointer without backing chunk → warning logged, excluded from snapshot
```

**FilePair Edge Cases (the ones you called out):**

```
FILEPAIR EDGE CASES
────────────────────
✓ Binary exists, no pointer → pointer created, content archived
✓ Pointer exists, no binary (thin) → hash from pointer, verify chunk exists
✓ Both exist, hashes match → normal archive
✓ Both exist, hashes DON'T match (stale pointer) → 
    hash from binary, pointer overwritten, new content archived
✓ Binary renamed, pointer not renamed → 
    orphan pointer (hash from pointer, verify chunk), 
    binary treated as new filepath
✓ Both renamed together → new paths in snapshot, 
    same content hash, dedup works
✓ Pointer file with invalid content (not a hex hash) → 
    warn, skip, or treat as binary?
✓ Pointer file for file that was previously archived then binary updated →
    binary hash wins, pointer overwritten
```

**Restore Scenarios:**

```
RESTORE SCENARIOS
─────────────────
✓ Restore single file from snapshot
✓ Restore multiple specific files
✓ Restore directory and everything under it
✓ Restore full snapshot
✓ Restore specific version (-v)
✓ Restore latest (default)
✓ Restore file that's in a tar bundle → full tar downloaded, 
    correct file extracted
✓ Restore two files from same tar → tar downloaded once
✓ Restore when local file exists and matches → skip
✓ Restore when local file exists and differs → prompt user
✓ Restore creates pointer files alongside restored binaries
✓ Restore with --no-pointers → no pointer files
```

**Ls Scenarios:**

```
LS SCENARIOS
────────────
✓ List all files in latest snapshot
✓ List files in specific snapshot version
✓ Filter by path prefix (e.g., "photos/2024/")
✓ Filter by filename part (e.g., "vacation")
✓ List returns correct metadata (size, dates)
✓ List with encryption → works (decrypts tree blobs)
```

**Crash Recovery (the hard ones):**

```
CRASH RECOVERY SCENARIOS
────────────────────────
✓ Archive crash after some large file uploads, before index →
    re-run recovers via HEAD + blob metadata, no duplicate uploads
✓ Archive crash after some thin chunk uploads →
    re-run recovers thin chunks, re-bundles remaining
✓ Archive crash after all uploads, before snapshot →
    re-run: dedup finds everything, builds trees, creates snapshot
✓ Restore crash mid-download →
    re-run: conflict check skips completed files, re-downloads rest
```

### How to Simulate Crashes

This is the tricky part. You can't easily crash a Mediator command mid-execution in a test. Options:

```
OPTION A: Fault injection via interface
──────────────────────────────────────
  IBlobStorageService with a test decorator that throws after N uploads.
  
  var faultingStorage = new FaultAfterNUploads(
      realAzuriteStorage, failAfterN: 5);
  
  // First run: crashes after 5 uploads
  var result1 = await mediator.Send(
      new ArchiveCommand(...), faultingStorage);
  Assert.That(result1.IsFailed);
  
  // Second run: same command, real storage
  var result2 = await mediator.Send(
      new ArchiveCommand(...), realAzuriteStorage);
  Assert.That(result2.IsSuccess);
  // Verify: no duplicate chunks, snapshot correct

OPTION B: Two-phase test
─────────────────────────
  // Phase 1: manually upload some chunks + thin chunks to Azurite
  //          (simulating a partial archive run)
  await UploadChunk(azurite, hash1, data1, metadata);
  await UploadChunk(azurite, hash2, data2, metadata);
  // DON'T upload index shards or snapshot
  
  // Phase 2: run full archive
  var result = await mediator.Send(new ArchiveCommand(...));
  
  // Verify: recovered chunks not re-uploaded, index correct, snapshot created
```

I'd go with **Option A** — it's closer to reality and tests the actual recovery code path. The fault-injecting decorator is a clean pattern:

```csharp
class FaultInjectionBlobService : IBlobStorageService
{
    private readonly IBlobStorageService _inner;
    private int _uploadsRemaining;
    
    public async Task UploadAsync(...)
    {
        if (--_uploadsRemaining < 0)
            throw new IOException("Simulated crash");
        await _inner.UploadAsync(...);
    }
    
    // All other methods delegate to _inner
}
```

---

## Layer 3: E2E Tests (Real Azure)

A small set of tests against a real Azure Blob Storage account. These catch what Azurite doesn't emulate:

```
REAL AZURE TESTS (gated, requires credentials)
───────────────────────────────────────────────
✓ Archive to real Hot tier → restore → correct content
✓ Archive to real Cool tier → restore → correct content
✓ Archive to real Archive tier → verify blob tier is set
  (can't test rehydration in automated tests — too slow/expensive)
✓ Thin chunk blob metadata is correctly set
✓ Concurrent uploads don't hit throttling at small scale
✓ Large file (100 MB+) upload/download streaming works
```

These tests need:
- An Azure storage account (test account with budget)
- Credentials via `Microsoft.Extensions.Configuration.UserSecrets` or env vars
- A `[Category("RealAzure")]` attribute to run separately from the fast suite
- Automatic cleanup (`[AfterEach]` deletes the test container)

```csharp
[Category("RealAzure")]
[Skip("Requires ARIUS_TEST_ACCOUNT_NAME and ARIUS_TEST_ACCOUNT_KEY")]
public class RealAzureTests
{
    // Only run when both env vars are set
    // Use a unique container name per test run: $"test-{Guid.NewGuid():N}"
    // AfterEach: delete container
}
```

### What Azurite Can't Test

```
┌──────────────────────────────┬─────────────────────────────────────┐
│ Azurite limitation           │ Impact                              │
├──────────────────────────────┼─────────────────────────────────────┤
│ No Archive tier              │ Can't test rehydration flow         │
│ No blob rehydration          │ Mock the rehydration poller         │
│ No tier enforcement          │ Verify tier is SET, not ENFORCED    │
│ No throttling                │ Can't test rate limit handling      │
│ Limited blob metadata        │ Should be close enough              │
│ Copy-to-rehydrate behavior   │ Mock this in integration tests      │
└──────────────────────────────┴─────────────────────────────────────┘
```

For the rehydration flow specifically, mock the blob tier checking:

```csharp
// In integration tests, the "rehydration" is instant:
// archive-tier blob → copy to chunks-rehydrated/ → immediately available
// This tests the code path without the 15-hour wait
```

---

## Layer 4: Architecture Tests (ArchUnitNET)

```
ARCHITECTURE RULES
──────────────────
✓ Arius.Core has no reference to Azure.Storage.Blobs
✓ Arius.Core has no reference to System.CommandLine
✓ Arius.Cli depends on Arius.Core, not on Arius.AzureBlob directly
  (or: Cli → Core → IBlobStorage ← AzureBlob)
✓ Arius.AzureBlob implements interfaces defined in Arius.Core
✓ No circular dependencies between projects
✓ Mediator commands live in Arius.Core
✓ All public types in Arius.Core are in the correct namespaces
✓ Encryption module is pluggable: Core depends on IEncryption, 
    not on concrete AES implementation
```

---

## Test Organization

```
tests/
├── Arius.Core.Tests/
│   ├── Crypto/
│   │   ├── HashingTests.cs
│   │   ├── EncryptionTests.cs
│   │   ├── EncryptionBackwardsCompatTests.cs    ← golden files
│   │   └── OpenSslCompatTests.cs                ← shell out to openssl
│   ├── Chunking/
│   │   ├── TarBuilderTests.cs
│   │   ├── GzipStreamTests.cs
│   │   └── SizeRouterTests.cs
│   ├── Index/
│   │   ├── ShardParsingTests.cs
│   │   └── ShardMergingTests.cs
│   ├── Trees/
│   │   ├── TreeBlobSerializationTests.cs
│   │   └── TreeHashTests.cs
│   ├── FilePairs/
│   │   └── FilePairResolutionTests.cs
│   └── Snapshots/
│       └── SnapshotManifestTests.cs
│
├── Arius.Integration.Tests/
│   ├── Fixtures/
│   │   ├── AzuriteFixture.cs                 ← TestContainers lifecycle
│   │   ├── TestFileSystem.cs                 ← temp dir helpers
│   │   └── FaultInjectionBlobService.cs
│   ├── Archive/
│   │   ├── BasicArchiveTests.cs
│   │   ├── IncrementalArchiveTests.cs
│   │   ├── DeduplicationTests.cs
│   │   ├── EncryptedArchiveTests.cs
│   │   ├── ThinArchiveTests.cs
│   │   └── CrashRecoveryArchiveTests.cs
│   ├── Restore/
│   │   ├── BasicRestoreTests.cs
│   │   ├── PartialRestoreTests.cs
│   │   ├── ConflictTests.cs
│   │   └── CrashRecoveryRestoreTests.cs
│   ├── Roundtrip/
│   │   ├── ArchiveRestoreRoundtripTests.cs   ← the golden tests
│   │   ├── FilePairEdgeCaseTests.cs
│   │   └── SpecialCharacterTests.cs
│   └── Ls/
│       └── LsTests.cs
│
├── Arius.E2E.Tests/
│   └── RealAzure/
│       ├── RealAzureArchiveTests.cs
│       └── RealAzureRestoreTests.cs
│
├── Arius.Architecture.Tests/
│   └── DependencyRuleTests.cs
│
└── TestData/
    ├── golden-chunks/                 ← encrypted chunks from previous Arius
    │   ├── large-file-v6.enc         ← known passphrase, known content
    │   └── tar-bundle-v6.enc
    └── test-files/
        ├── empty.bin                  ← 0 bytes
        ├── small.bin                  ← 500 bytes
        ├── threshold.bin              ← exactly at threshold
        ├── large.bin                  ← 2 MB
        ├── unicode-名前.txt           ← unicode filename
        └── binary.dat                 ← random bytes (not text)
```

---

## The OpenSSL Compatibility Tests

These are special — they shell out to the `openssl` CLI to verify the worst-case recovery path:

```csharp
[Test]
public async Task EncryptedChunk_CanBeDecryptedByOpenSsl()
{
    // Archive a known file with a known passphrase
    var passphrase = "test-passphrase-123";
    var content = "Hello, Arius!"u8.ToArray();
    
    // ... archive the file through the full pipeline ...
    
    // Download the raw chunk from Azurite
    var chunkBytes = await DownloadChunkBlob(contentHash);
    var tempChunk = Path.GetTempFileName();
    await File.WriteAllBytesAsync(tempChunk, chunkBytes);
    
    // Decrypt with openssl CLI
    var result = await RunProcess("openssl", 
        $"enc -d -aes-256-cbc -pbkdf2 -iter 10000 " +
        $"-pass pass:{passphrase} -in {tempChunk}");
    
    // Gunzip
    var gunzipped = Gunzip(result.Stdout);
    
    // Verify
    Assert.That(gunzipped, Is.EqualTo(content));
}

[Test]
public async Task EncryptedTarBundle_CanBeExtractedByStandardTools()
{
    // Archive small files → tar bundle
    // Download raw tar chunk from Azurite
    // openssl dec → gunzip → tar x → verify files by content-hash name
    
    var decrypted = await RunPipeline(
        $"openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:{passphrase}",
        "gunzip",
        $"tar xf - -C {tempDir}");
    
    // Verify extracted files exist with correct content-hash names
    var extractedFiles = Directory.GetFiles(tempDir);
    foreach (var (hash, expectedContent) in testFiles)
    {
        var extracted = await File.ReadAllBytesAsync(
            Path.Combine(tempDir, hash));
        Assert.That(extracted, Is.EqualTo(expectedContent));
    }
}
```

These tests are the ultimate safety net. If they pass, your data is always recoverable with standard tools, no matter what happens to the Arius codebase.

---

## What I'm NOT Sure About

### Test Data Generation at Scale

Unit tests are fast with small data. But the 500M-file design has behavior that only manifests at scale:
- LRU cache eviction patterns
- External sort of the manifest
- Index shard batching behavior
- Channel backpressure

**Do we need a stress test?** Something like:

```csharp
[Category("Stress")]
[Test]
public async Task Archive_100K_SmallFiles()
{
    // Generate 100K small files (100 KB each = 10 GB)
    // Archive to Azurite
    // Verify: correct number of tar bundles, index entries, tree blobs
    // Measure: peak memory, duration
    
    // This is NOT a unit test — it runs for minutes.
    // Gated behind [Category("Stress")]
}
```

100K files is a practical upper bound for CI. The real 500M test would be manual on dedicated hardware.

### Docker Testing

The system runs on Docker (Synology). Things that could break:
- Path handling: Linux paths vs. macOS/Windows test machines
- Filesystem: ext4 on Synology vs. APFS on dev machine
- Mounted volumes: the cache directory behavior

A Docker-based integration test (test runs inside Docker matching the production image) would catch these but adds CI complexity.

### Concurrency Tests

Race conditions are notoriously hard to test deterministically. Options:

```
A) Stress testing: run the pipeline at high concurrency 100 times, 
   check for data corruption each time. Flaky by nature.

B) Deterministic scheduling: inject a test scheduler that controls 
   when each channel consumer runs. Precise but artificial.

C) Thread Sanitizer: run under a race detector. 
   .NET doesn't have one natively. 

D) Property-based testing: generate random file sets, 
   random crash points, verify invariants always hold.
```

Property-based testing (D) with a library like FsCheck or similar for C# could be powerful here. Define invariants:

```
INVARIANT: For any set of files, 
  archive(files) → restore(files) produces byte-identical output

INVARIANT: For any crash point during archive,
  archive(files) → crash → archive(files) produces a valid snapshot
  with the same content as a single clean run

INVARIANT: Every content-hash in a snapshot's tree has either:
  a) a large chunk, OR
  b) a thin chunk pointing to a valid tar chunk
```

---

## Test Configuration

```csharp
// Test setup for Azurite
class AzuriteFixture : IAsyncDisposable
{
    private readonly AzuriteContainer _container;
    
    public string ConnectionString => _container.GetConnectionString();
    
    public static async Task<AzuriteFixture> CreateAsync()
    {
        var container = new AzuriteBuilder()
            .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
            .Build();
        await container.StartAsync();
        return new AzuriteFixture(container);
    }
}

// Shared across all integration tests — one Azurite per test run
// Each test gets its own container (Azure container, not Docker container)
```

TUnit lifecycle:

```csharp
[ClassDataSource<AzuriteFixture>(Shared = SharedType.Globally)]
public class ArchiveRestoreRoundtripTests(AzuriteFixture azurite)
{
    [Test]
    public async Task Archive_SingleLargeFile_Restores_Identically()
    {
        var containerName = $"test-{Guid.NewGuid():N}"[..32];
        using var localDir = TempDirectory.Create();
        
        // Create test file
        var testFile = localDir.CreateFile("large.bin", size: 2_000_000);
        
        // Archive
        var archiveResult = await Mediator.Send(new ArchiveCommand
        {
            LocalRoot = localDir.Path,
            AccountName = "devstoreaccount1",
            AccountKey = azurite.AccountKey,
            Container = containerName,
            Tier = AccessTier.Hot  // Azurite doesn't support Archive
        });
        archiveResult.Should().BeSuccess();
        
        // Restore to different location
        using var restoreDir = TempDirectory.Create();
        var restoreResult = await Mediator.Send(new RestoreCommand
        {
            LocalRoot = restoreDir.Path,
            AccountName = "devstoreaccount1",
            AccountKey = azurite.AccountKey,
            Container = containerName,
            Path = "large.bin"
        });
        restoreResult.Should().BeSuccess();
        
        // Verify byte-identical
        var original = await File.ReadAllBytesAsync(testFile);
        var restored = await File.ReadAllBytesAsync(
            Path.Combine(restoreDir.Path, "large.bin"));
        restored.Should().BeEquivalentTo(original);
    }
}
```

---

## Priority Order

If I had to rank what to test first:

```
MUST HAVE (data integrity):
  1. Encryption/hashing backwards compatibility (golden files)
  2. OpenSSL compatibility (worst-case recovery)
  3. Archive → restore roundtrip (basic + encrypted)
  4. FilePair edge cases (stale pointer, thin archive)
  5. Crash recovery (fault injection)

SHOULD HAVE (correctness):
  6. Deduplication verification
  7. Incremental archive behavior
  8. Ls correctness
  9. Tree blob construction
  10. Index shard parsing/merging

NICE TO HAVE (robustness):
  11. Architecture rules
  12. Special character handling
  13. Stress test at 100K files
  14. Property-based invariant tests
  15. Docker environment test
```

---

Any of these threads you want to dig into further? Or does this cover the testing strategy well enough to formalize the whole thing?

User: can you MINIMALLLY update #file:REQUIREMENTS.md based on this entire discussion

- key design decisions
- what new concepts did we introduce?
- were there ambiguous/conflicting requirements i initially gave but were cleared out in our discussion

the intent is that i will feed the file again into a new explore question for a next iteration to really distil the core requirements

GitHub Copilot: 

Let me re-read the full requirements file to plan precise, minimal edits. 

Read [](file:///Users/wouter/Documents/GitHub/Arius7/REQUIREMENTS.md)

Created 14 todos

Starting: *Apply all minimal REQUIREMENTS.md updates* (1/2)

Now I'll apply all the minimal, targeted updates in one pass: 

Multi-Replace String in Files