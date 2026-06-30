# Fast-hash: a local hashcache to skip re-reading unchanged files

> Status: **approved design — pending implementation plan**
> Date: 2026-06-29
> Scope: `archive` hashing performance + pointer-file default. One spec, two phases.

## Problem

`archive` re-reads and re-hashes **every** local binary on every run (`ArchiveCommandHandler`
Stage 2 streams each file through `IEncryptionService.ComputeHashAsync`). For a large
(~1 TB), mostly-stable repository — the motivating case is a Synology NAS running Arius in a
Docker container — that means re-reading ~1 TB each run even when almost nothing changed.
Whether the bottleneck is disk/network I/O or CPU, the only way to make a stable archive fast
is to **stop re-reading files whose content has not changed**.

Detecting "unchanged" without reading all the bytes is inherently a **heuristic**. Arius
values correctness over throughput ([AGENTS.md](../../../AGENTS.md) "Scale and durability"), so
the heuristic is **opt-in** and every uncertainty falls back to a full re-hash.

## Goals

- Skip re-reading + re-hashing binaries whose content is unchanged, on an explicit opt-in.
- Keep the default behaviour byte-for-byte identical to today (full re-hash).
- Make the change-detection decision **independent of timestamp precision/timezone fragility**
  across Windows / macOS / Linux (Synology Docker) — the cross-platform worry that motivated
  this work.
- Make `--no-pointers` the effective default (pointers add filesystem noise), without losing
  data or breaking hosts.
- Require **no pointer-format change and no repository migration**; existing v5 and v7 repos
  work unchanged.

## Non-goals

- Sub-file content-defined chunking (a one-byte edit still re-uploads the whole file).
- Concurrent multi-machine archiving of the same files (see Invariants).
- Tamper resistance against a privileged adversary (the heuristic targets *accidental* change,
  not deliberate evasion).

---

## Decisions (resolved during brainstorming)

| # | Decision | Choice |
|---|---|---|
| 1 | Trust model | **Opt-in** `--fast-hash`. Default stays full-hash. |
| 2 | Verdict signals | `size` + platform change-signals (`ctime`/`inode`/`dev`) + **sparse fingerprint** (deterministic spot-hashes). **`mtime` is stored but NOT in the verdict.** |
| 3 | ctime+inode fast-path | **Built in v1** (target is local POSIX/Windows native filesystems; cache is always local). |
| 4 | Cold/empty cache | **Full-hash on miss** (safest). No snapshot/pointer seeding (documented seam). |
| 5 | Pointer format | **No v7.1, no migration.** Pointers stay bare-hash v7. |
| 6 | Pointer default | `--write-pointers` (default off) replaces `--no-pointers` on `archive`; `--remove-local` implies it. |
| 7 | Source of truth | Remote (chunk-index + snapshots). Hashcache is a disposable local accelerator, validated **against the live file**, never against a pointer. |

### Why no v7.1 / no migration

The snapshot already stores `path → content-hash, created, modified` per file
(`FileTreeModels.FileEntry`); the chunk index stores the size. Enriching the pointer would
(a) only help `--write-pointers` users (pointers are off by default), (b) create a second
source of fast-hash metadata that can **drift** from the cache, and (c) force a messy v5→v7.1
migration that **cannot populate the sparse fingerprint** for pointer-only (`--remove-local`)
files, since their binary isn't local. The hashcache owns the fingerprint; the pointer stays a
bare content-hash.

---

## Architecture

### Component overview

```
archive (Stage 2: Hash workers ×4)
        │
        ▼
  HashCacheService  ──►  HashCacheLocalStore (SQLite, single-writer)
        │                 ~/.arius/{account}-{container}/hash/cache.sqlite
        │
        ├─ TryGetVerifiedHash(path, liveSignals)  → ContentHash | miss
        └─ Upsert(path, signals, sparse_fp, hash)

  RelativeFileSystem
        └─ TryGetChangeSignals(path) → (ctime, inode, dev)?   // platform-specific
```

- **`HashCacheService`** — the facade Stage 2 consumes. Owns the verdict ladder and the sparse
  fingerprint computation. Modeled on `ChunkIndexService` (facade over a private SQLite store).
- **`HashCacheLocalStore`** — sole owner of the SQLite schema/connections/transactions; writes
  serialize on a gate + busy-retry, exactly like `ChunkIndexLocalStore`.
- **`RelativeFileSystem.TryGetChangeSignals`** — new IO-boundary method returning the
  platform's cheap change signals, or `null` when unavailable (then the floor applies). It
  returns `null` on **network filesystems** by design (`GetDriveType()==DRIVE_REMOTE` / UNC on
  Windows; CIFS/NFS `f_type` on Linux), because a redirector's `ChangeTime`/file-id can be
  cached or unstable — see "Behaviour on network filesystems" below.

### The hashcache (SQLite)

Location: `~/.arius/{account}-{container}/hash/cache.sqlite` (new
`RepositoryLocalStatePaths.GetHashCacheRoot`). WAL, `synchronous = normal`,
`user_version = 1`. Local, disposable, rebuilt by full-hashing on loss.

Schema (one row per binary-present path):

| column | type | role |
|---|---|---|
| `path` | TEXT PK | repository-relative canonical path |
| `size` | INTEGER | bytes |
| `mtime` | INTEGER | UTC ticks — diagnostics only, **not** in the verdict |
| `ctime` | INTEGER NULL | inode change time / Windows `ChangeTime` (UTC ticks) when available |
| `inode` | TEXT NULL | inode (POSIX, 64-bit) / Windows `FileId` (**128-bit** on ReFS) — stored as text for width-uniformity, like `dev` |
| `dev` | TEXT NULL | device id / Windows `VolumeSerialNumber` |
| `signal_set` | INTEGER | provenance tag: which signals this row was captured with (platform/capability) |
| `sparse_fp` | BLOB | combined fingerprint (32 bytes) |
| `fp_algo` | INTEGER | fingerprint-scheme version |
| `content_hash` | TEXT | cached `ContentHash` (hex) |
| `last_verified` | INTEGER | UTC ticks — seam for future age/audit policy |

### Platform signal mapping

`TryGetChangeSignals` returns the same abstract triple on every platform; only the source API
differs. To be recorded in the ADR:

| Abstract signal | POSIX (Linux/macOS) | Windows |
|---|---|---|
| `ctime` | `statx`/`stat` `st_ctim` (inode change time) | `FILE_BASIC_INFO.ChangeTime` |
| `inode` | `st_ino` (64-bit) | `FILE_ID_INFO.FileId` (`FILE_ID_128`; 64-bit NTFS, 128-bit ReFS) |
| `dev` | `st_dev` | `FILE_ID_INFO.VolumeSerialNumber` |

`ChangeTime` is **not** settable via the documented `SetFileTime` (only Creation/Access/Write
are) — moving it requires the native `NtSetInformationFile`, the same deliberate/abnormal-API
class as backdating POSIX `ctime`. The 128-bit `FileId` makes ID reuse even less likely than a
POSIX 64-bit inode, strengthening the inode-change guard.

**Provenance guard:** two different mismatches, two different fallbacks, so we never compare
incomparable values:
- **`fp_algo` mismatch** (fingerprint scheme bumped) → the stored `sparse_fp` is incomparable →
  **full-hash**.
- **`signal_set` / `dev` mismatch** (cache moved to another platform/volume, or signals
  unavailable) → only the `ctime`/`inode` fast-path is disabled; the platform-independent
  `sparse_fp` is still valid → fall to the **sparse-fingerprint floor**.

Neither can produce a wrong "unchanged". This is what makes the cross-platform-sequential story
(below) safe.

### The `--fast-hash` verdict ladder (per file, in the Stage-2 hash worker)

```
stat → liveSize
sig  = fs.TryGetChangeSignals(path)            // (ctime, inode, dev)? — may be null
row  = cache.lookup(path)

if row is null                                 → FULL-HASH, upsert          // cache miss
elif row.fp_algo != CURRENT_FP_ALGO            → FULL-HASH, upsert          // fp incomparable
elif liveSize != row.size                      → FULL-HASH, upsert          // size changed
elif sig present
     and sig.signal_set == row.signal_set
     and (sig.dev, sig.inode, sig.ctime)
          == (row.dev, row.inode, row.ctime)   → UNCHANGED: reuse row.content_hash  // skip ALL reads
                                                 (refresh last_verified)
else:
    liveFp = computeSparseFp(file, liveSize)    // ≤ ~1 MiB of reads (or whole small file)
    if liveFp == row.sparse_fp                 → UNCHANGED: reuse row.content_hash
                                                 (refresh ctime/inode/dev/mtime/last_verified)
    else                                       → FULL-HASH, upsert          // content changed
```

- The `ctime` fast lane requires `dev`+`inode`+`ctime` to *all* match (same file, untouched):
  the kernel bumps `ctime` on any inode change and it can't be set to an arbitrary value through
  normal syscalls, so this is content-unchanged with very high confidence — **no reads at all**.
  A changed `inode`/`dev` (file replaced, or volume changed) simply fails this condition and
  falls to the floor; it never trusts a cross-inode `ctime`.
- The sparse-fingerprint branch is the **portable floor**: it runs when change-signals are
  absent (exFAT/FAT/network FS), when the file was replaced (`inode`/`dev` changed), or when
  `ctime` moved but size is unchanged (distinguishes a benign `chmod`/metadata touch from a real
  content change).
- A reused `content_hash` flows into the existing dedup/router stage unchanged, so a
  verified-unchanged file is never re-read and never re-uploaded.

**Safety argument.** A misprediction toward "changed" only causes a wasted full re-hash (safe).
The only unsafe misprediction is "unchanged when actually changed"; every signal added to the
"declare unchanged" gate can only *add* reasons to say "changed", so it never increases the
unsafe direction.

The two unsafe failure modes are **per-path, not conjunctive** — a file reaches "unchanged" via
exactly one path and is exposed to exactly one mode:
- **ctime fast-lane** failure **(a)**: `ctime` unchanged despite a content change. On a native
  local filesystem the kernel bumps `ctime`/`ChangeTime` on *any* content write, so this
  **cannot happen by accident** — defeating it is deliberate timestomping: POSIX needs a clock
  backdate (privileged) or raw block-device write; Windows needs the native
  `NtSetInformationFile` (the documented `SetFileTime` can't touch `ChangeTime`). Either way it
  is abnormal, never a side effect of a normal write. The fast-lane is therefore watertight
  against accidental corruption.
- **sparse-fingerprint floor** failure **(b)**: a content change that keeps identical size *and*
  misses every sampled region. This is the real residual, but narrow — it applies only to files
  that were *touched* (so they fell off the fast-lane) yet kept identical size, and it shrinks
  as `K` grows with size. The in-place fixed-size-record edit is its archetype; the file-type /
  audit seam is its mitigation.

Both modes are accepted only under the opt-in `--fast-hash` and are documented in the ADR.

### Behaviour on network filesystems (SMB/CIFS/NFS)

Not a supported target (an archive runs *on* the machine holding the files — see Invariants),
but it must degrade safely rather than be undefined. Over SMB a redirector exposes
`ChangeTime`/file-id values that may be cached or unstable across reconnects, so trusting them
risks a false "unchanged". Therefore `TryGetChangeSignals` returns `null` on network mounts and
the verdict falls to the **sparse-fingerprint floor**: `size` + a few ranged reads per file.
The result is "works, just slower" — still far cheaper than full-hash (≤ ~16 MiB read per file
vs the whole file), with the zero-read `ctime` lane disabled and no correctness asterisk.

### Sparse fingerprint

- **Size-scaled sampling** (not a fixed block count): block `B = 256 KiB`; number of regions
  `K = clamp(ceil(size / stride), 4, 64)` with `stride ≈ 1 GiB`. So bytes read grow with size
  but stay bounded: **1 MiB for small files … 16 MiB for very large files** (a 3 TB file →
  capped at 64 blocks = 16 MiB, regardless of size). Final `B`/`stride`/`Kmax` are tuned by
  benchmark.
- Regions are at **deterministic offsets** from size: head at `0`, tail at `size - B`, and the
  rest evenly spaced — `offset_i = floor(i · (size - B) / (K - 1))`, `i = 0..K-1`. Determinism
  is what lets the same regions be re-sampled each run (size is the precondition for comparing).
- Files `≤ K·B` are read **whole** (sampling degenerates to the full small file — cheap per
  file, a genuine content check, and one sequential read with no seeks).
- `sparse_fp = SHA256(size ‖ region-bytes)` — a **single combined** 32-byte fingerprint (not
  per-region; any mismatch ⇒ full-hash, so which region changed is irrelevant).
- Versioned by `fp_algo`; changing `B`/`stride`/`Kmax`/offset scheme bumps it and invalidates
  old rows safely.
- **Reads the raw file directly** via the seekable `FileStream` (`fs.OpenRead`) — *not* through
  the compress/encrypt wrappers. On local disk the head/interior/tail seeks are cheap random
  reads; on a network FS each region is one ranged read (a round-trip), and small files avoid
  per-region round-trips by being read whole. A file that shrank between `stat` and read yields
  a short tail read → treated as changed → full-hash (safe).
- **Coverage limit (documented):** for genuinely enormous in-place-mutating files (VM images,
  large databases) even 16 MiB across 64 points is sparse, so a constant-size mid-file edit can
  be missed. Such files are poor `--fast-hash` candidates; the mitigation is the file-type /
  `last_verified` audit-escalation **seam** — full-hash by type or age — not denser sampling.

### Cache population on normal (non-`--fast-hash`) runs

A normal run already streams every byte through `ComputeHashAsync`. The sparse fingerprint is
computed from that **same stream at zero extra I/O** (tee the sampled regions as they pass),
the change-signals are captured, and the row is upserted. So a normal archive warms the cache
and a subsequent `--fast-hash` run is immediately effective — no wasted first pass.

### Logging (trust + debuggability for an opt-in heuristic)

Per file (debug): the decision and its reason, e.g.
`[fast-hash] {path} → reused (ctime match)` ·
`→ reused (size+fp match)` ·
`→ full-hash (size 100→120)` ·
`→ full-hash (fp differs)` ·
`→ full-hash (ctime moved, fp differs)` ·
`→ full-hash (cache miss)` ·
`→ full-hash (inode changed)`.
Per run (info): a summary — `reused N (ctime M, fp K), rehashed R; reasons: …`.

---

## Pointer default flip (`--write-pointers`)

- **CLI**: remove `--no-pointers` from `archive`; add `--write-pointers` (default **off**).
  `restore` keeps its own `--no-pointers` (default writes pointers) — unchanged.
- **Behaviour**: with pointers off, suppress pointer creation/refresh for files that have a
  local binary. **Pointer-only (thin) entries are always preserved**, and legacy v5 pointer-only
  files are still upgraded in place — they are the sole local record for a removed binary.
- **`--remove-local` implies `--write-pointers`** (removing the binary while writing no pointer
  would orphan the path). This replaces today's mutual-exclusion *error* with an implication.
- **Data safety**: `--no-pointers` keeps the binary, so nothing is lost locally; the remote
  (chunk index + snapshots) stays authoritative.

### Host impact (verified in code)

- **`ls` / ListQuery** derives `LocalPointer` from on-disk presence (`LocalDirectoryReader`).
  With pointers off, binary-present files simply lack the `LocalPointer` flag and show as
  `LocalBinary | Repository` — correct, not broken. Pointer-only files still show `LocalPointer`.
- **Explorer** colours a pointer indicator from that flag — it reads "no local pointer", truthful.
- **Api** passes `NoPointers` through `JobRunner`; flipping the Core default propagates (the
  host's own job default is a separate config choice).
- **Web** archive pane is redesigned (below).

### Web archive pane redesign

Today: two mutually-exclusive checkboxes (`Remove local binaries` / `Skip pointer files`) plus
a "these are mutually exclusive" note (a footgun). Replace with:

- A segmented/radio **"On disk after archive:"**
  - **Keep files only** — *default* (no pointers)
  - **Keep files + pointers** — `--write-pointers`
  - **Replace files with pointers** — `--remove-local` (implies pointers)
- A separate toggle **`⚡ Fast hash — skip re-reading unchanged files`** (`--fast-hash`) with a
  one-line tooltip about the trust trade.

The radio makes invalid combinations unrepresentable, so the mutual-exclusion note is removed.

---

## Phasing

**Phase 1 — fast-hash core**
- `RepositoryLocalStatePaths.GetHashCacheRoot`; `HashCacheLocalStore` (schema, single-writer);
  `HashCacheService` (verdict ladder + sparse fingerprint).
- `RelativeFileSystem.TryGetChangeSignals` (Linux/macOS `stat`/`statx`, Windows
  `GetFileInformationByHandleEx`; `null` fallback, incl. network-FS detection).
- `--fast-hash` option wired through `ArchiveCommand` → `ArchiveVerb`; Stage 2 integration;
  inline cache population on normal runs; logging.

**Phase 2 — pointer default + hosts**
- `--write-pointers` (default off) replacing `--no-pointers` on `archive`; `--remove-local`
  implication; preserve/upgrade pointer-only entries.
- Web archive pane redesign; Api/host default alignment; restore-side left unchanged.

**Documented seams (not built)**
- Cold-start cache seeding from the snapshot (mtime-trust) — declined for now.
- `last_verified`-driven age/audit re-verification and file-type escalation policy (also the
  mitigation for enormous in-place-mutating files the sparse fingerprint under-covers).
- mtime-only ultra-fast tier (schema already stores mtime).

---

## Invariants

- **An archive is local and never concurrently multi-platform.** Sequential cross-platform use
  (e.g. archive on Windows → restore on Linux → archive on Windows) is supported because each
  machine's hashcache is independent and a stored signal is only ever compared to a live signal
  read from the same filesystem that produced it. To be recorded in AGENTS.md.
- **The hashcache is a disposable local accelerator.** Losing/corrupting it costs time (a
  full-hash run), never data. The remote is the source of truth.
- **The cache is validated against the live file, never against a pointer.** Pointers are not in
  the fast-hash loop.
- **The default path is unchanged.** Without `--fast-hash`, hashing behaviour is byte-for-byte
  as today.
- **`mtime` is never load-bearing in v1** — the verdict uses content-derived signals (`size`,
  `ctime`/`inode`, sparse fingerprint), keeping decisions independent of timestamp
  precision/timezone across platforms.

## Testing

- **Unit** — each ladder branch: cache miss, provenance/`signal_set`/`dev` mismatch, `fp_algo`
  bump, `inode`/`dev` change, size change, `ctime` match (skip reads), `ctime` moved + fp match,
  fp differ. Sparse-fp determinism, deterministic offsets, size-scaled `K` (small whole-read;
  large file capped at `Kmax`), and short-tail-read-on-shrink → changed. Provenance guard.
- **Cross-platform** — the size+sparse-fp verdict is OS-independent by construction (assert
  identical decisions Linux/Windows); `TryGetChangeSignals` returns sane values on native FS,
  `null` on network/unsupported FS (→ floor, asserted on a CIFS/NFS-like mount).
- **Integration / E2E** — warm cache skips re-read (assert via hash/read events); cache deletion
  forces full re-hash; `--fast-hash` off = today's behaviour; `--write-pointers` default off;
  `--remove-local` still writes pointers; a file edited preserving size but changing a sampled
  region is detected.

## Docs to update

- **New ADR** (ADR-0021): opt-in change-detection heuristic, the sparse-fingerprint gap, the
  disposable-local-cache + not-concurrently-multi-platform invariant, and the **platform signal
  mapping** (POSIX `st_ctim`/`st_ino`/`st_dev` ↔ Windows `ChangeTime`/128-bit `FileId`/
  `VolumeSerialNumber`, incl. the `SetFileTime`-can't-set-`ChangeTime` caveat).
- **`docs/design/`** — new `core/shared/hashcache.md`; update `archive-command.md` (Stage 2) and
  `encryption.md` (hashing seam).
- **`docs/glossary.md`** — `hashcache`, `sparse fingerprint`, `fast-hash`.
- **`AGENTS.md`** — the local/not-concurrently-multi-platform archive invariant.
- **`README.md`** — `--fast-hash` and the pointer default, in human terms.
