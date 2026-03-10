## Context

Arius is a greenfield deduplicated encrypted backup tool targeting Azure Blob Storage Archive tier. It draws architectural inspiration from restic's repository format (content-addressable storage, pack files, snapshots, trees, indexes) but diverges significantly to accommodate archive-tier economics and constraints.

Key constraints that drive the design:
- **Archive tier rehydration**: Data packs require 1–15 hours to become readable. Restore is inherently multi-phase.
- **Operation pricing**: Archive charges per 10K operations. Fewer, larger blobs are cheaper.
- **Minimum storage duration**: Archive has 180-day early deletion penalty. Prune must account for this.
- **Cold tier for metadata**: Config, keys, locks, index, snapshots, and trees live in Cold tier — instantly readable but with pricier access than Hot.
- **No local state dependency**: The repository must be fully recoverable from Azure with only a passphrase. Local SQLite cache is a performance optimization, not a requirement.

The system has two frontends (CLI, Web UI) sharing business logic via the Mediator pattern, with `IAsyncEnumerable<T>` as the streaming backbone.

## Goals / Non-Goals

**Goals:**
- Restic-like CLI experience for backup, restore, forget, prune, snapshots, ls, find, diff, check, key management
- Cost-aware restore with upfront rehydration estimates and confirmation
- File Explorer-style web UI with real-time progress streaming
- Content-defined chunking with deduplication across snapshots
- AES-256-CBC encryption compatible with OpenSSL tooling
- Single Azure Blob container as the complete, self-describing repository
- Resumable operations (restore survives interruption, picks up where it left off)

**Non-Goals:**
- Compatibility with restic's repository format (spirit, not wire-compatible)
- Support for any backend other than Azure Blob Storage
- FUSE mount (restic's `mount` command) — not applicable to archive tier
- `self-update` or `debug` commands
- Multi-repository copy (`restic copy`) — may be added later
- Real-time/continuous backup (this is point-in-time snapshot-based)

## Decisions

### D1: Repository Layout

```
{container}/
├── config                    # Encrypted JSON: repo ID, version, chunker params
├── keys/{id}                 # Key files: scrypt params + encrypted master key
├── snapshots/{id}            # Encrypted JSON: time, tree root, paths, tags, host
├── index/{id}                # Encrypted JSON: blob hash → (pack ID, offset, length)
├── trees/{hash}              # Encrypted JSON: directory entries (name, type, size, content hashes)
└── data/{prefix}/{pack-id}   # Encrypted pack files containing chunked file data
```

**Tiering:** Everything except `data/` lives in Cold tier. `data/` lives in Archive tier.

**vs restic:** Trees are stored as standalone Cold-tier blobs, NOT packed into archive-tier data packs. This enables instant directory browsing without rehydration. Tree blobs are content-addressed (SHA-256 of content) and deduplicated across snapshots.

**Rationale:** Metadata is <0.1% of total repository size. Cold-tier storage cost is negligible. The UX gain of instant `ls`/`find`/browsing is worth it.

### D2: Chunking — Gear Hash CDC

**Choice:** Gear hash content-defined chunking.
**Alternatives considered:**
- Fixed-size chunking: Simple but terrible dedup (one inserted byte shifts all boundaries)
- Rabin fingerprint (restic's choice): More complex, requires irreducible polynomial selection
- FastCDC: Good but adds normalized chunking complexity we don't need yet
- Byte-pattern matching (zero runs): Non-uniform chunk sizes depending on content

**Parameters (configurable at repo init, stored in config):**
- Min chunk size: 256 KB
- Average chunk size: 1 MB
- Max chunk size: 4 MB

**Gear table:** 256 entries of `uint64`, generated with a deterministic seed (stored in config) for reproducibility across machines.

**Rationale:** Gear hash is 3 lines of core logic (`hash = (hash << 1) + gear[byte]`, check mask), gives uniform chunk distribution regardless of content, and naturally handles the boundary-resync property needed for dedup.

### D3: Encryption — AES-256-CBC with OpenSSL Compatibility

**Choice:** Reuse existing `CryptoExtensions` — AES-256-CBC, PKCS7 padding, OpenSSL `Salted__` prefix format, PBKDF2-SHA256 key derivation (10K iterations).
**Alternatives considered:**
- AES-256-GCM (authenticated encryption): More modern, but CBC + content-addressable hash verification gives equivalent integrity guarantees
- libsodium/NaCl: Overkill for this use case, adds native dependency

**Encryption scope:** Pack-level (entire pack is one encrypted stream). Since archive tier requires full-blob download anyway, per-blob encryption within packs adds complexity without benefit.

**Two-level key architecture:**
1. User passphrase → PBKDF2(passphrase, salt, 10K iterations, SHA-256) → derives a 32-byte key + 16-byte IV
2. This derived key decrypts the **key file** → yielding the **master key** (32 bytes)
3. The master key encrypts/decrypts ALL repository data (packs, trees, snapshots, index, config)

Key files are plain JSON (NOT encrypted by the master key) stored in `keys/`. Each key file contains: salt, iteration count, and the master key encrypted with the passphrase-derived key. This means:
- Password change = re-encrypt master key only, no data re-encryption
- Multiple passwords = multiple key files, same master key inside each
- Manual recovery = decrypt key file with passphrase to get master key, then use master key with OpenSSL

**Integrity model:** SHA-256 hash of encrypted pack = pack ID on Azure. On restore: verify ciphertext hash → decrypt → decompress → verify each plaintext blob hash against index. Corruption/tampering detected at either layer.

### D4: Pack File Format — TAR + gzip + OpenSSL

**Choice:** Use standard TAR archive format for packing, gzip for compression, and OpenSSL-compatible AES-256-CBC for encryption. This ensures packs can be manually recovered using only standard Unix tools.
**Alternatives considered:**
- Custom binary format (restic-style header-at-end): Compact but requires custom tooling for recovery. Since archive tier requires full-blob download anyway, random access within packs provides no benefit.
- ZIP: Less Unix-native, worse streaming characteristics.

**Pack pipeline:**
```
Chunks (plaintext blobs) 
    → TAR archive (blobs named by SHA-256 hash + manifest.json)
    → gzip compression
    → AES-256-CBC encryption (OpenSSL-compatible)
    → Upload to Azure
```

**TAR structure inside each pack:**
```
{blob-hash-1}.bin      # raw chunk data
{blob-hash-2}.bin      # raw chunk data
...
manifest.json          # { blobs: [{ hash, type, size }] }
```

**TAR overhead:** With gzip, overhead is negligible — TAR headers are 90%+ zeros and compress to nearly nothing. Even worst-case (4,800 × 2KB blobs in a 10 MB pack), compressed TAR overhead is <1%.

**Compression benefits at archive tier:**
- Reduces storage cost directly ($0.002/GB/month)
- Reduces rehydration transfer cost
- Pays for the TAR header overhead and then some

**Default pack size:** 10 MB (configurable via `--pack-size`).

**Rationale:** 10 MB balances operation cost (fewer packs = cheaper) against partial-restore granularity (don't rehydrate 128 MB for one 2 KB file). TAR + gzip + OpenSSL means any pack can be manually recovered with standard tools — critical for a backup system that may outlive its own software.

### D5: Locking — Azure Blob Lease

**Choice:** Azure Blob Storage lease mechanism instead of file-based locks.
**Alternatives considered:**
- File-based locks in cold tier: 90-day minimum storage for a lock that lives seconds; architecturally wrong

**Implementation:**
- Acquire lease on `config` blob for exclusive operations (prune, repair)
- Shared operations (backup, read) use a "lock blob" with lease for coordination
- Lease duration: 60 seconds, auto-renewed by background task
- Lease break on crash: Azure auto-releases after lease duration

### D6: Streaming Architecture — IAsyncEnumerable

All long-running operations produce `IAsyncEnumerable<TEvent>` streams:

```
Handler → IAsyncEnumerable<TEvent>
  ├── CLI: Spectre.Console Live/Progress rendering
  └── API: SignalR hub broadcast to connected web clients
```

Event types per operation are discriminated unions (abstract record + concrete records). Example: `RestoreEvent` → `RestorePlanReady | RehydrationProgress | FileRestored | RestoreComplete`.

**SignalR over SSE:** SignalR provides bidirectional communication (cancel from UI), automatic reconnection, and hub-based grouping. SSE is simpler but one-directional.

### D7: Mediator Pattern

**Choice:** Mediator (not MediatR) for request/handler dispatch.

Two handler interfaces:
- `IRequestHandler<TRequest, TResponse>` — single result (stats, cost estimate)
- `IStreamRequestHandler<TRequest, TItem>` — `IAsyncEnumerable<TItem>` (backup, restore, ls, find)

CLI and API both construct request objects and dispatch through Mediator. Handlers contain all business logic.

### D8: Restore — Overlapping Multi-Phase

```
Phase 1 (PLAN):     Load snapshot → walk trees → identify packs → cost estimate → confirm
Phase 2 (REHYDRATE): Initiate rehydration → poll status → stream progress
Phase 3 (RESTORE):   Download rehydrated packs → decrypt → extract → write files
```

Phases 2 and 3 overlap: as each pack becomes rehydrated, immediately download and restore from it. Rehydrated packs are downloaded to `~/.arius/hydrated/{pack-id}` staging directory, then deleted after extraction.

**Resumability:** Rehydration state tracked in Azure (blob tier status is queryable). On resume, query which packs are already rehydrated and continue from there. No local state needed for resumption.

### D9: Project Structure

```
Arius/
├── src/
│   ├── Arius.Core/             # Domain: chunking, crypto, repository, packing, snapshots, trees, index
│   ├── Arius.Azure/            # Azure Blob backend: upload, download, rehydrate, lease locking, tiering
│   ├── Arius.Cli/              # System.CommandLine CLI commands (Spectre.Console for rendering)
│   ├── Arius.Api/              # ASP.NET Core Minimal APIs + SignalR hubs
│   └── Arius.Web/              # Vue 3 + TypeScript SPA (File Explorer UI)
├── tests/
│   ├── Arius.Core.Tests/
│   ├── Arius.Azure.Tests/
│   └── Arius.Integration.Tests/
└── docker/
    └── Dockerfile              # Multi-stage: build API + Web → single container
```

### D10: Local Cache — Optional SQLite

SQLite database at `~/.arius/cache/{repo-id}.db` with tables for blobs, packs, trees, snapshots. Fully derived from Azure contents. Rebuilt on demand by downloading index/, snapshots/, and trees/ from cold tier.

Delta sync: cache stores a watermark (last-synced blob listing marker). On connect, only downloads new files since watermark.

**Critical invariant:** `result(with_cache) ≡ result(without_cache)`. Cache affects performance only, never correctness.

### D11: Manual Recoverability

**Design goal:** In a worst-case scenario where the Arius tool is unavailable, any file in the repository can be manually recovered using only standard tools: `az` CLI, `openssl`, `gzip`, `tar`, and `cat`.

**Recovery procedure (without Arius):**
1. Download key file from `keys/` (Cold tier, plain JSON) → extract encrypted master key, salt, iterations
2. Decrypt master key using passphrase: `echo <encrypted_master_key> | base64 -d | openssl enc -d -aes-256-cbc -pbkdf2 -iter 10000 -md sha256 -S <salt_hex> -pass pass:PASSPHRASE`
3. Download and decrypt a snapshot: `openssl enc -d ... -pass file:master.key` → get root tree hash
4. Download and decrypt the tree blob → get content hashes for the target file
5. Download and decrypt index file → map content hashes to pack IDs
6. Rehydrate the needed packs: `az storage blob set-tier --tier Hot`
7. Download, decrypt, decompress, extract the pack: `openssl enc -d ... | gunzip | tar x`
8. Concatenate the extracted blobs in order: `cat hash1.bin hash2.bin > restored-file`

**Rationale:** A backup tool must be more durable than the tool itself. TAR + gzip + OpenSSL are available on every Unix system and will remain so for decades. This is the ultimate disaster recovery guarantee.

## Risks / Trade-offs

- **[Cold tier read costs at scale]** → 15 GB index download for 500M-file repo costs ~$0.15. Acceptable. Mitigated by local cache delta sync.
- **[Archive 180-day early deletion]** → Prune must warn about/account for early deletion charges. Consider `--min-age` flag.
- **[Gear hash determinism]** → Gear table seed must be stored in repo config for cross-machine reproducibility. Different seed = different chunks = no dedup.
- **[CBC without MAC]** → No ciphertext authentication. Mitigated by content-addressable hash verification at both ciphertext and plaintext layers. Attacker can cause decryption of garbage, but it's detected immediately.
- **[Pack-level encryption blocks random access]** → Can't read single blob without decrypting entire pack. Acceptable for archive tier (full blob download required anyway).
- **[Rehydration costs can surprise users]** → Always show cost estimate before rehydration. Require explicit confirmation.
- **[SQLite cache can become stale]** → Always validate cache watermark against Azure on connect. Cache miss = rebuild from remote.

## Open Questions

- **Pack size guidance:** Should the CLI recommend larger pack sizes for large repos? e.g., "You have 100K+ packs, consider `--pack-size 64m` for lower operation costs."
- **Concurrent backup:** Multiple machines backing up to the same repo simultaneously — what coordination is needed beyond lease-based locking?
- **Bandwidth limiting:** Should the CLI support `--limit-upload` and `--limit-download` like restic?
