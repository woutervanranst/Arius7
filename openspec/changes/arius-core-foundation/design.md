## Context

Arius is a greenfield C# archival tool targeting Azure Blob Archive tier for cheap long-term storage of large file collections (up to 500M files / 1 TB). A previous version exists with encrypted chunks already in archive storage — these must remain compatible. The metadata layer (snapshots, indexing, tree structure) is being redesigned from scratch.

Key constraints:
- Archive tier blobs require rehydration (hours, costly per transaction) before reading
- Small file rehydration is prohibitively expensive → must bundle into tar archives
- Memory must be bounded and constant regardless of file count
- Files can be 10 GB+ → streaming throughout, never load fully in memory
- Must be recoverable with standard tools (openssl, gzip, tar) in worst case
- Runs in Docker on a Synology NAS
- Encryption is optional but all-or-nothing per repository

## Goals / Non-Goals

**Goals:**
- Implement three use cases: `archive`, `restore`, `ls`
- Content-addressable storage with file-level deduplication
- Concurrent pipeline architecture (Channels) maximizing CPU and network throughput
- Bounded memory (~30 MB constant at 500M file scale)
- Crash-recoverable, idempotent archive and restore operations
- Backwards-compatible encryption with previous Arius version chunks
- Clean abstraction: Core knows nothing about Azure Blob specifically
- Comprehensive testing including encryption compatibility and crash recovery

**Non-Goals:**
- Block-level / sub-file deduplication (file-level only)
- Snapshot deletion or garbage collection (designed hook only, not implemented)
- Web UI (future, out of scope)
- S3 or other storage backends (architecture supports it, not implemented)
- Incremental hashing optimization (every archive run re-hashes all files)
- Real-time sync or file watching

## Decisions

### 1. Solution Structure

Three projects with strict dependency rules:

```
Arius.Cli → Arius.Core → IBlobStorageService ← Arius.AzureBlob
```

- **Arius.Core**: Mediator commands, domain logic, pipeline orchestration. No Azure references.
- **Arius.Cli**: System.CommandLine verbs, progress display, user interaction.
- **Arius.AzureBlob**: `IBlobStorageService` implementation using Azure.Storage.Blobs.

Core defines interfaces (`IBlobStorageService`, `IEncryptionService`); implementations are injected. This allows future backends (S3, local filesystem) without touching Core.

**Alternatives considered:**
- Single project: simpler but couples CLI and storage concerns, prevents reuse
- Core referencing Azure SDK directly: faster to build but locks in the backend

### 2. Archive Pipeline — Channel-Based Concurrent Stages

```
Enumerate (×1) → Hash (×N) → Dedup (×1, batched) → Route → Upload Pool (×N)
                                                      │
                                            ┌─────────┴─────────┐
                                            ▼                   ▼
                                      Large File           Tar Builder (×1)
                                      Pipeline             → Channel<SealedTar>
                                            │                   │
                                            └─────────┬─────────┘
                                                      ▼
                                              Upload Pool (×N, shared)
                                                      │
                                              Index Updater (at end)
                                                      │
                                              Manifest Writer (disk) → External Sort
                                                      │
                                              Tree Builder (×1, bottom-up)
                                                      │
                                              Create Snapshot
```

Each stage connected by bounded `Channel<T>`. Backpressure is automatic — if uploads are slow, hashing pauses.

**Stage concurrency:**

| Stage | Concurrency | Bound by |
|-------|-------------|----------|
| Enumerate | 1 | FS sequential |
| Hash | N (CPU cores) | CPU |
| Dedup check | 1 (batched by prefix) | manages in-flight set |
| Route | 1 | trivial |
| Large file upload | N (4-8) | Network |
| Tar builder | 1 | stateful accumulator |
| Tar upload | N (shared pool) | Network |
| Index upload | 1 (at end) | Sequential shard merge |
| Tree builder | 1 | Sequential bottom-up |
| Pointer writer | N | Trivially parallel |

**Alternatives considered:**
- File-by-file sequential: too slow at scale
- Unbounded parallelism: memory and connection exhaustion
- Multiple tar builders: worse fill rate, more partial tars

### 3. Tar Builder — Single Builder, Directory Affinity, Seal-and-Hand-Off

One tar builder accumulates small files. When the target size (64 MB) is reached, it seals the tar, hands it off to the upload pool via `Channel<SealedTar>`, and immediately starts a new tar. This overlaps tar building with uploading.

Files inside the tar are named by their content-hash (not original path). This makes tars content-addressable and path-independent.

Depth-first enumeration provides natural directory affinity — files from the same directory tend to arrive together, so a directory restore is likely to hit fewer tar bundles.

The tar builder streams to a temp file on disk (not memory). Peak temp disk: ~64 MB.

### 4. Three Chunk Types in `chunks/`

Every content-hash has a blob in `chunks/`, regardless of bundling strategy:

- **`large`** (arius-type=large): gzip+encrypted file data. Blob name = content-hash.
- **`tar`** (arius-type=tar): tar+gzip+encrypted bundle. Blob name = tar-hash.
- **`thin`** (arius-type=thin): ~80-byte pointer (body = tar-hash). Blob name = content-hash. Created for each small file after its tar is uploaded.

Thin chunks solve two problems:
1. **Uniform lookup**: `HEAD chunks/<content-hash>` works for any file, large or small.
2. **Crash recovery**: if archive crashes after tar upload but before index update, re-run discovers thin chunks via HEAD and recovers the content-hash → chunk-hash mapping from the thin chunk body.

**Alternatives considered:**
- Sidecar manifest per tar: orphan risk on crash when tar is rebuilt differently
- No thin chunks (index only): crash before index upload loses all mappings, requires re-upload

### 5. Chunk Index — Tiered Cache, Upload at End

65,536 shards by 2-byte content-hash prefix. Each shard is a text file:
```
<content-hash> <chunk-hash> <original-size> <compressed-size>\n
```

Compressed with gzip (and encrypted if passphrase set) before upload.

**Tiered lookup during dedup check:**

```
L1: In-memory LRU (configurable, default 512 MB) → ~700 parsed shards
L2: Local disk cache (~/.arius/cache/<repo-id>/chunk-index/) → all downloaded shards
L3: Azure Blob (chunk-index/<prefix>/index) → download on miss
```

The dedup stage buffers hashes into batches (~10K), groups by prefix, and resolves each prefix through the three tiers. This amortizes shard downloads across many lookups.

**Index is uploaded once at the end** of the archive run (not incrementally). This is safe because thin chunks provide crash recovery for the content-hash → chunk-hash mapping.

**Alternatives considered:**
- Incremental shard upload after each batch: more Azure transactions, shard contention during writes
- Full in-memory index: ~32 GB at 500M files, won't fit
- Bloom filter for existence check: false positives require fallback anyway, added complexity

### 6. Merkle Tree — Full Blob Hash Including Metadata

Tree blobs are JSON, one per directory:
```json
{
  "entries": [
    {"name": "photo.jpg", "type": "file", "hash": "a1b2...", "size": 4200000,
     "created": "2024-06-15T10:30:00Z", "modified": "2024-06-15T10:30:00Z"},
    {"name": "subdir/", "type": "dir", "hash": "d4e5..."}
  ]
}
```

Tree hash = SHA256 of the full serialized JSON (passphrase-seeded if encrypted). Metadata changes (e.g., `touch`) produce a new tree hash. This is intentional — the snapshot should reflect the actual filesystem state.

Tree blobs are cached locally forever (content-addressed = immutable).

**Tree construction** is a two-phase process:
1. During pipeline: write all completed files to a manifest temp file (unsorted).
2. After pipeline: external sort the manifest by path, then stream through sorted entries building tree blobs bottom-up, one directory at a time. Memory: one directory at a time.

### 7. Crash Recovery — Blob Metadata Receipts

Each uploaded blob carries metadata:
- `arius-complete`: true (set after body upload finishes — absence means incomplete)
- `arius-type`: large | tar | thin
- Size metadata (original-size, chunk-size)

On re-run after crash:
1. For each content-hash to upload: `HEAD chunks/<content-hash>`
   - 404 → upload fresh
   - 200 + `arius-complete` → skip (recover index entry from blob metadata / thin chunk body)
   - 200 + no `arius-complete` → incomplete, overwrite
2. Recovered entries merge into in-memory index
3. Normal pipeline continues (dedup against recovered + cached index)
4. Index shards uploaded at end

Crash detection: write `.archive-in-progress` marker at start, delete on snapshot creation. If present on startup → recovery path.

### 8. Restore Pipeline — Stream-and-Discard, No Cache

```
Resolve Snapshot → Walk Tree → Conflict Check → Chunk Resolution →
  Cost Estimation + User Confirmation →
    Phase 1: Download available chunks (streaming) →
    Phase 2: Start rehydration for archive-tier → Exit
    (User re-runs later for remaining)
```

**No local chunk cache.** Chunks are streamed directly: download → decrypt → gunzip → extract → write to final path. For tar bundles, the tar is streamed through and entries extracted on the fly. Peak temp disk: 0 (streaming) for large files, 0 for tars with streaming tar reader.

All files needing a given chunk are grouped and extracted in a single pass. The chunk is never stored locally.

This avoids the 2× disk problem (1 TB cache + 1 TB restored files).

**Restore is non-blocking:** download what's available now, kick off rehydration requests for archive-tier chunks, exit. User re-runs the same idempotent command later. Each run checks what's already restored (hash match → skip) and what's newly rehydrated.

### 9. Encryption — Pluggable Stream Wrapper

The encryption layer wraps streams:
```csharp
IEncryptionService {
    Stream WrapForEncryption(Stream inner);
    Stream WrapForDecryption(Stream inner);
    byte[] ComputeHash(byte[] data);       // SHA256(passphrase + data) or SHA256(data)
    byte[] ComputeHash(Stream data);       // streaming variant
}
```

Two implementations: `PassphraseEncryptionService` (AES-256-CBC) and `PlaintextPassthroughService` (no-op wrapper, plain SHA256). Selected at startup based on `--passphrase` presence.

AES-256-CBC format: openssl-compatible, `Salted__` prefix, PBKDF2 with SHA-256 and 10K iterations. Backwards compatible with previous Arius version.

Hash construction: `SHA256(passphrase_bytes + data_bytes)` — literal byte concatenation, not HMAC. Locked for backwards compatibility.

### 10. Progress Reporting — Mediator Events

Pipeline stages emit progress events through Mediator:

```
FileScanned, FileHashing, FileHashed, ChunkUploading, ChunkUploaded,
TarBundleSealing, TarBundleUploaded, SnapshotCreated
```

The CLI subscribes and renders a live display showing:
- Aggregate progress (scanned, hashed, deduped, uploaded)
- In-flight files: which files are currently being hashed and uploaded (with progress %)
- Tar bundle status (sealing, uploading)

For restore: cost estimation breakdown, rehydration status, files restored count.

### 11. Local Cache Layout

```
~/.arius/cache/<repo-id>/
    chunk-index/           ← L2 disk cache for index shards
    filetrees/             ← tree blob cache (immutable, indefinite)
```

`<repo-id>` = SHA256(accountname + container)[:12]. No chunk data is cached (stream-and-discard restore). In Docker, `~/.arius/cache/` is on a mounted volume.

## Risks / Trade-offs

**[Full re-hash on every archive] → Accepted trade-off**
Every archive run re-hashes all binary files (pointers never trusted as cache). At 500M × 2KB = 1 TB this takes ~15 min on SSD. The alternative (mtime-based skip) adds complexity and a local state file. Accepted: simplicity wins, re-hash is I/O-bound and parallelized.

**[Orphan thin chunks on crash] → Low impact**
If archive crashes after some thin chunks are created but before the snapshot, those thin chunks remain orphaned until the next successful run (which creates the correct snapshot). They don't cause data loss — they're small (~80 bytes each) Cool-tier blobs. Future GC can clean them.

**[Orphan tar chunks on crash] → Accepted cost**
If a tar is uploaded but the archive crashes and re-runs with different file ordering, a new tar with a different hash is created. The old tar is orphaned in archive tier. Cost: ~64 MB × $0.00012/month = negligible. Future GC can clean.

**[Proportional compressed size estimate] → Approximation**
Per-file compressed size in tar bundles is estimated proportionally (file_size / total_uncompressed × total_compressed). Actual per-file compression varies. Acceptable for cost estimation — worst case the estimate is off by ~50%.

**[External sort for 500M-file manifest] → Disk-intensive**
The on-disk manifest at 500M entries is ~18 GB compressed. External sort requires temp disk space and time (~15 min). This only occurs for very large archives. Acceptable.

**[Single tar builder] → Sequential bottleneck for small files**
The tar builder is single-threaded. At 500M small files, tar creation is the bottleneck if accumulation is slower than upload. In practice, tar sealing triggers upload and the builder continues immediately — the upload pool handles parallelism. If this becomes a bottleneck, the builder can be parallelized later (multiple builders with prefix-based partitioning).

## Open Questions

- **Spectre.Console vs Terminal.Gui**: Spectre.Console is simpler (progress bars, tables) while Terminal.Gui provides a full TUI. Decide based on UX needs for the progress display.
- **Empty directories**: Include in tree blobs or skip? Git skips them. Arius should probably skip (an empty dir has no files to archive).
- **Pointer file for file at exactly the threshold**: Treat as large or small? Define: `>=` threshold → large.
