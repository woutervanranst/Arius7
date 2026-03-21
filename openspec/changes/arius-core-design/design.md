## Context

Arius is a content-addressable backup system targeting Azure Blob Archive tier for cheap long-term offline storage. The previous version used a monolithic SQLite database for state tracking, which breaks at the target scale of 500M+ files (the index alone would be tens of GB, requiring download and parse on every invocation).

The system must support three operations — archive, restore, ls — from a stateless local machine (all repository knowledge lives in blob storage). The local filesystem serves as the source of truth during archive; blob storage is the source of truth for restore and ls.

Existing archive-tier chunks in blob storage must remain compatible (openssl AES-256-CBC / tar / gzip). Snapshot format is new.

**Constraints:**
- Archive tier blobs cannot be read without rehydration (hours, costs per transaction)
- Must handle 500M files / 1 TB+ archives
- Client-side encryption optional: when passphrase provided, everything stored remotely is encrypted with no structural leakage; when omitted, data is plaintext
- Recoverable using open source tools (openssl, gzip, tar) as worst-case fallback
- Single archive machine at a time; restore/ls from any machine
- Runs in Docker on Synology NAS

## Goals / Non-Goals

**Goals:**
- Scalable state storage that doesn't require downloading a monolithic index
- Concurrent pipeline architecture for archive and restore operations
- Clean separation: Core (domain logic) → Storage abstraction → Azure Blob implementation
- Cross-platform (Windows/Linux) path handling
- Backwards-compatible chunk format with existing archive-tier blobs
- Extensible metadata format in snapshots for future fields
- Optional encryption with no structural leakage when enabled

**Non-Goals:**
- Snapshot deletion / garbage collection (snapshots are never deleted)
- Web UI (future, out of scope)
- S3 or local filesystem backend (future, but architecture supports it)
- Full-text filename search index (accept slow first traversal, flat index deferred)
- Snapshot migration from previous Arius version (separate effort)
- Bloom filter for global content existence checks (pointer files suffice)

## Decisions

### D1: Project Structure — Three-Project Separation

```
Arius.Cli          → System.CommandLine CLI, progress display
    ↓ depends on
Arius.Core         → Mediator commands, domain logic, pipeline orchestration
    ↓ depends on (interfaces only)
Arius.AzureBlob    → Azure.Storage.Blobs implementation of storage interfaces
```

Core defines `IStorageProvider`, `IBlobClient`, etc. AzureBlob implements them. CLI wires up DI and dispatches commands. Core uses Mediator (not MediatR) for command dispatch.

**Why Mediator over MediatR:** simpler, source-generated, no reflection. Suitable for a focused domain with few command types.

**Why separate AzureBlob project:** Core must not know about Azure. Future backends (S3, local FS) implement the same interfaces.

### D2: State Storage — Git-Style Encrypted Merkle Tree

Replace monolithic SQLite with a content-addressed tree of encrypted blobs, one per directory.

```
Container layout:
├── chunks/              ← Archive tier (configurable)
│   ├── <content-hash>   ← large file: gzip → encrypt
│   └── <tar-hash>       ← tar bundle: tar → gzip → encrypt
├── trees/               ← Cool tier
│   └── <tree-hash>.enc  ← one encrypted blob per directory
├── snapshots/           ← Cool tier
│   └── <UTC-timestamp>.enc  ← manifest: root tree hash + metadata
├── chunk-index/         ← Cool tier
│   └── <2-byte-prefix>/
│       └── index.enc    ← content-hash → tar-chunk-hash mappings
└── chunks-rehydrated/   ← Hot tier, temporary
    └── <hash>           ← rehydrated copies for restore
```

**Tree node format** (JSON, versioned, before encryption):
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
    },
    {
      "name": "subdir",
      "type": "tree",
      "hash": "<tree-hash>"
    }
  ]
}
```

**Alternatives considered:**
- Flat snapshot file (one blob per snapshot listing all files): doesn't scale to 500M files — single blob would be tens of GB
- Sharded flat index (partition by hash prefix): good for dedup lookups but bad for path-based browsing (`ls`)
- Batched tree nodes: fewer blobs but worse random access for `ls` and complex rebalancing. Individual blobs chosen for simplicity; full tree export can be added later as a read optimization.

### D3: Hashing — Passphrase-Seeded or Plain SHA-256

When a passphrase is provided, all hashes (content hashes, tree hashes) use `SHA256(passphrase + data)`. This:
- Prevents hash collision attacks (attacker can’t predict hashes without passphrase)
- Ensures blob names in storage are opaque (no structural leakage across repositories)
- Means identical content under different passphrases produces different hashes

When no passphrase is provided, hashes use plain `SHA256(data)`. Blob names are still content-addressed but are deterministic and not obscured.

Content hash = `SHA256([passphrase +] file bytes)`.
Tree hash = `SHA256([passphrase +] serialized tree node entries)`.

### D4: Encryption — Optional OpenSSL-Compatible AES-256-CBC

When `--passphrase` is provided:
```
Format: Salted__<8-byte-salt><ciphertext>
Key derivation: PBKDF2 with SHA-256, 10,000 iterations
Cipher: AES-256-CBC
```

Applied to: chunks, tree blobs, snapshot manifests, chunk index shards. Every blob in storage is encrypted with this format.

When `--passphrase` is omitted: no encryption is applied. Data is stored as plaintext (gzip-compressed where applicable). This is useful for testing, non-sensitive archives, or environments where storage-level encryption suffices.

**Why openssl-compatible:** worst-case recovery with standard tools. Download blob, run `openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -pass pass:<passphrase>`, pipe through `gunzip` (and `tar x` for bundles).

The encryption/decryption layer is implemented as a pluggable stream wrapper: when passphrase is present, streams are wrapped with encrypt/decrypt; when absent, streams pass through unmodified.

### D5: Small File Bundling — Fill-to-Threshold Tar Archives

Two independent configurable parameters:
- **Small file threshold** (default: 1 MB): files below this are tar-bundled
- **Tar target size** (default: 64 MB): tar is sealed when cumulative size reaches this

Files are added to the current tar buffer in filesystem walk order. When the buffer reaches the target size, it's sealed: `tar → gzip → encrypt → upload`. The chunk name is the hash of the encrypted tar blob.

Each file's content-hash → tar-chunk-hash mapping is recorded in the chunk index.

Content type: `application/aes256cbc+tar+gzip` for encrypted tar bundles, `application/aes256cbc+gzip` for encrypted solo large files, `application/tar+gzip` for plaintext tar bundles, `application/gzip` for plaintext solo large files.

### D6: Content-Hash → Chunk Resolution — Try Direct, Fall Back to Index

For restore, to find the chunk containing a file:
1. `HEAD chunks/<content-hash>` — if 200, it's a large file stored as a solo chunk. Download directly.
2. If 404, look up `chunk-index/<2-byte-prefix>/index.enc` — find the tar-chunk-hash, download and extract.

This avoids the chunk index entirely for large files (the common case by data volume).

**Chunk index sharding:** 2-byte content-hash prefix → 65,536 shards. At 500M small files: ~7,600 entries per shard, ~150-200 KB compressed per shard. Downloaded and cached locally on first access.

**Chunk index updates:** affected shards are rewritten during each archive run. Only shards containing new entries change.

### D7: Pointer Files — Local Cache, Content Hash Only

Format: `<filename>.pointer.arius` containing the hex content hash as plaintext.

- Used during archive to skip re-hashing unchanged files (compare hash to pointer)
- Not the source of truth — fully rebuildable by hashing local files
- If pointer is missing or mismatched: re-hash the binary, update the pointer
- If local binary is missing but pointer exists: binary was removed, pointer may be stale (archive records what's currently present)

### D8: Snapshot Naming — UTC with Sub-Second Precision

Format: `2026-03-21T140000.000Z` (ISO 8601-ish, safe for blob names, sortable).

No collision risk with sub-second precision. Snapshots are never deleted. Each snapshot manifest contains the root tree hash plus metadata (timestamp, file count, total size, Arius version).

### D9: Concurrent Pipeline Architecture

**Archive pipeline** (Channels with bounded capacity):

```
Enumerate FS → Hash Files → Decide → Upload → Finalize
              (N workers)            (M workers)
```

- **Enumerate**: single FS walker, produces `FileInfo` items. Graceful: skip unreadable files/folders with warning log.
- **Hash**: N worker tasks (N = processor count). Reads file, computes SHA-256. Compares with pointer file if present.
- **Decide**: determines skip (hash matches pointer + chunk exists), upload-large, or add-to-tar-buffer.
- **Upload**: M worker tasks for parallel uploads. Solo large files: gzip → encrypt → upload. Tar buffer: seals and uploads when full.
- **Finalize**: write/update pointer files, build tree bottom-up, upload new tree nodes, upload snapshot manifest.

**Concurrency guards:**
- `ConcurrentDictionary<hash, Task>` for chunk uploads — second thread awaits first's upload
- Single writer for tar buffer (or lock) — sequential file addition, seal+upload when full
- Same dedup for tree node uploads

**Restore pipeline** (Channels with bounded capacity):

```
Resolve → Rehydrate → Download → Decrypt+Extract → Write to Disk
                      (N workers)
```

- **Resolve**: walk tree to find target files, determine chunk hashes (direct or via chunk index)
- **Rehydrate**: two-phase — check `chunks-rehydrated/` first, immediately process available; submit rehydration for rest, poll in batches
- **Download**: N workers downloading from `chunks-rehydrated/` into local cache
- **Decrypt+Extract**: decrypt, gunzip, un-tar (for bundles), extract target files. Cache tar locally (reuse for multiple files from same bundle)
- **Write**: write files to disk, set metadata (dates), create pointer files
- **Cleanup**: delete `chunks-rehydrated/` blobs and local cache after full restore

**Concurrency guards:**
- `ConcurrentDictionary<chunkHash, Task>` for rehydration requests — dedup
- Local tar cache: download once, extract many
- Batch polling for rehydration status (not one-by-one)

### D10: Streaming Progress Updates

Core produces `IAsyncEnumerable<ProgressEvent>` or writes to a Channel that CLI reads. Events include:
- File hashed (path, hash, status: new/unchanged/updated)
- Upload started/completed (hash, size, type: chunk/tar/tree)
- Rehydration requested/completed (hash)
- Download started/completed (hash)
- File restored (path)
- Warning (path, reason: permission denied, etc.)

CLI renders these as streaming console output. Future web UI consumes the same stream.

### D11: Cross-Platform Path Handling

All paths stored in tree nodes use forward slash `/` as separator, regardless of OS. On Windows, paths are normalized on read/write. Reserved characters in filenames are handled at the storage layer (blob name encoding). Relative paths only — no drive letters, no absolute paths in snapshots.

## Risks / Trade-offs

**[Full tree traversal is slow on cold cache]** → At 500M files, downloading all 5M tree blobs takes ~8-17 minutes on first access. Mitigation: this only happens for full-text search or pointer rebuild (rare operations). Path-based `ls` is instant (4 tree blob downloads). A full tree export blob can be added later as a read optimization without changing the format.

**[65,536 chunk index shards on Azure Blob]** → Many small blobs in Cool tier. Mitigation: each shard is ~150-200 KB, total ~10 GB. Read cost is minimal (only access shards for files being restored). Shards are cached locally after first access.

**[Tar bundling wastes bandwidth on single-file restore]** → Downloading a 64 MB tar to extract one 2 KB file. Mitigation: at Archive tier, the per-blob rehydration cost dominates bandwidth cost. Fewer, larger blobs = cheaper restore overall. The tar target size is configurable.

**[500M HEAD requests during tree upload]** → Each tree node needs an existence check before upload. Mitigation: most tree nodes are unchanged between runs (same hash = already exists). Only new/modified directories produce new tree hashes. A typical weekly run with 10K changed files touches a few hundred tree nodes.

**[Passphrase loss = total data loss]** → When encryption is enabled, all content and metadata is encrypted. Mitigation: this is by design (client-side encryption). Document prominently. Consider future support for passphrase escrow or key splitting (out of scope). Without a passphrase, data is plaintext and always recoverable.

**[Plaintext mode exposes all data]** → When no passphrase is provided, all data in blob storage is readable by anyone with storage access. Mitigation: this is an explicit user choice. The system relies on Azure’s storage-level access controls. Document the security implications clearly.

**[Single tar buffer serialization point]** → Files are added to the tar buffer sequentially while uploads are parallel. Mitigation: tar addition is fast (mostly I/O into a memory buffer); the bottleneck is upload bandwidth, not tar construction. Multiple tar buffers could be used if this becomes a bottleneck.
