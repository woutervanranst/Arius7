---
status: accepted
date: 2026-06-29
decision-makers: Wouter Van Ranst
consulted: Claude (Anthropic)
informed: Arius agent team
confidence: high
---

# Opt-in change-detection hashcache for fast-hash archive runs

## Context and Problem Statement

`archive` re-reads and re-hashes every local binary on every run. For a large (~1 TB), mostly-stable repository — the primary target is a Synology DS918+ NAS running Arius in a Docker container — that means re-reading ~1 TB each run even when almost nothing changed. Whether the bottleneck is disk/network I/O or CPU, the only way to make a stable archive fast is to stop re-reading files whose content has not changed.

Detecting "unchanged" without reading all the bytes is inherently a heuristic. Arius values correctness over throughput: if the heuristic errs it must err toward re-hashing, never toward silently caching a wrong content hash. The question for this ADR is how to structure a correct, multi-platform, opt-in change-detection mechanism and a local cache for its results.

## Decision Drivers

* Correctness first: the default must remain a full read. An operator chooses to accept heuristic risk when they pass `--fast-hash`.
* The DS918+ runs Linux kernel 4.4, which predates `statx` (available from 4.11). Any platform interop must survive ENOSYS gracefully.
* Cross-platform portability: Windows, macOS, and Linux (POSIX) each have their own stable change-signal APIs. The abstraction must hide this behind a single nullable return.
* The hashcache is a local accelerator only. Losing it must cost one slow full-hash run, never data loss or a wrong snapshot.
* Pointers add filesystem noise; they should be opt-in rather than opt-out. `--remove-local` is the one case where a pointer is mandatory (removing the binary without leaving a local record loses the path entirely).

## Considered Options

* Trust OS `mtime` only — fast but well-known to be fragile across timezones, NAS clock drift, and intentional `touch`.
* Trust `inode + ctime` only — better, but inode numbers can be reused and ctime cannot be set intentionally on most filesystems (meaning accidental reuse is rare). Still a heuristic.
* Combine `inode + ctime` with a sparse byte-sample fingerprint — provides two independent failure-mode paths: the ctime fast-lane skips even the sparse read; the fingerprint floor catches ctime-equal content changes (timestamp stomping, inode reuse).
* Use Mono.Posix for POSIX interop — tried and removed; the package brings a large dependency for three syscalls, and its stat binding silently uses the 32-bit `stat` on some platforms.
* Implement hand-rolled `[LibraryImport]` interop — precise, zero-dependency, explicit struct layouts guarded per architecture.

## Decision Outcome

Chosen option: "opt-in `--fast-hash` with a two-lane verdict ladder (ctime fast-lane + sparse-fingerprint floor), hand-rolled libc interop (`statx`→`stat` fallback), and a disposable local SQLite hashcache", because it achieves the correctness-first goal (default unchanged; opt-in accepts the heuristic trade), survives the DS918+ 4.4 kernel via the `stat` fallback, and costs zero extra I/O on an unchanged file when `ctime`/`inode`/`dev` match.

Confidence: high. The verdict ladder is conservative: the ctime and fingerprint paths are *independent* (not conjunctive), each path can only produce a false hit if the OS reports unchanged signals for a changed file (clock/inode manipulation), and the file size check is a prerequisite for both paths.

Before:

```bash
arius archive ./photos --remove-local   # always full-reads every binary
```

After:

```bash
# First run (or no --fast-hash): full-read, populates the cache
arius archive ./photos

# Subsequent runs: unchanged files served from cache, changed files full-re-hashed
arius archive ./photos --fast-hash

# Pointer sidecars are opt-in; --remove-local requires --write-pointers
arius archive ./photos --write-pointers                 # write sidecars, keep binary
arius archive ./photos --remove-local --write-pointers  # remove binary, leave pointer
```

### Consequences and Tradeoffs

* Good, because stable large archives run dramatically faster once the cache is warm.
* Good, because correctness is unaffected for users who do not pass `--fast-hash`.
* Good, because any cache loss recovers transparently with one full-hash run.
* Good, because the sparse fingerprint catches timestamp-stomping (an mtime-only design could not).
* Bad, because a cache miss on a very large file triggers two reads instead of one (sparse sample + full hash) — the sparse sample is seeking rather than sequential, so this is mainly I/O latency, not throughput.
* Bad, because the `struct stat` x86_64 fallback is architecture-specific; non-x64 Linux without `statx` falls back to the fingerprint floor instead of the ctime fast-lane.
* Neutral, because the hashcache is a new local file (`~/.arius/<account>-<container>/hash/cache.sqlite`) that operators may need to account for in disk-space planning.

### Confirmation

`ArchiveFastHashTests` and `ArchivePointerDefaultTests` in `src/Arius.Core.Tests/` verify the verdict ladder (hit/miss/rehash paths) and pointer opt-in semantics. Architecture tests enforce that `NativeFileSignals` is only called through `RelativeFileSystem.TryGetChangeSignals`.

**On-hardware validation (target platform — Synology DS918+, kernel `4.4.302+`, 2026-06-30).** A cold then warm `--fast-hash` run over the same ~114-file tree logged `[fast-hash] summary: reused 0, rehashed 114` then `reused 114, rehashed 0`, with the per-file Debug lines showing `114 -> full-hash (cache miss)` then `114 -> reused (ctime match)` — **zero** `size+fp match` (floor) hits. Because 4.4.302 predates `statx` (4.11), `statx` returns ENOSYS and these signals come through the `stat` x86_64 fallback (`TryGetViaStat`): the bare `stat` libc symbol binds on DSM's glibc and the ctime fast-lane serves every unchanged file with zero byte reads. Captured with `ARIUS_LOG_LEVEL=Debug`.

## Pros and Cons of the Options

### Mono.Posix for POSIX interop

Tried, then removed.

* Bad, because it adds a large transitive dependency for three syscalls (`stat`, `statx`, `statfs`).
* Bad, because Mono.Posix's `stat` binding uses an internal struct layout that may silently misread fields on uncommon platforms.
* Bad, because it does not expose `statx`, so the architecture-stable layout cannot be used.

### Hand-rolled `[LibraryImport]` interop

* Good, because it is zero-dependency and ships in-tree with explicit `[StructLayout(LayoutKind.Explicit)]` byte offsets and size checks.
* Good, because `statx` (kernel ≥ 4.11) is architecture-stable (same layout on x86_64 and arm64); `stat` fallback guards itself to x64 only.
* Good, because every error path returns `null` → the caller falls back to the sparse-fingerprint floor, so a wrong read is always a performance miss, never a wrong content hash.
* Neutral, because the `struct statx` and `struct stat` (x86_64) layouts must be manually kept in sync with glibc; they are well-documented POSIX ABI and have been stable for decades.

## Detailed design notes

### The verdict ladder

`HashCacheService.TryReuse` applies checks in order; the first failure returns `Miss` and the caller full-hashes:

1. **Cache miss** — no row for the path → `Miss("cache miss")`.
2. **Algorithm bump** — `fp_algo` column differs from `SparseFingerprint.Algo` → `Miss("fp_algo bump")` (handles future sparse-fingerprint algorithm changes without a schema migration).
3. **Size change** — live file size ≠ cached size → `Miss("size …→…")`.
4. **ctime fast-lane** — `TryGetChangeSignals` returns non-null signals AND `(inode, dev, ctime)` all match the cached row → `Hit("ctime match")` with no byte reads. Updates `last_verified`.
5. **Sparse-fingerprint floor** — reads the deterministic sample regions, computes SHA-256 over `size ‖ sampled-bytes`, compares to the cached fingerprint → `Hit("size+fp match")` if equal (updates cached signals + `last_verified`); else `Miss("fp differs")`.

The ctime and fingerprint lanes are **independent** failure modes, not conjunctive. A file with no available signals (network FS, unsupported platform, `statx` ENOSYS on non-x64) skips directly to step 5.

### Platform signal mapping

| Platform | API | `ctime` field | `inode` field | `dev` field |
|---|---|---|---|---|
| Linux ≥ 4.11 | `statx` | `stx_ctime.tv_sec` + `tv_nsec` (→ UTC ticks) | `stx_ino` (u64, string) | `stx_dev_major:stx_dev_minor` |
| Linux 4.4 (Synology DS918+) | `stat` x86_64 fallback | `st_ctim.tv_sec` + `tv_nsec` | `st_ino` (u64) | decoded via glibc `gnu_dev_major`/`gnu_dev_minor` macros → `"major:minor"` to match `statx` format |
| macOS (arm64 only) | `stat` (Darwin 64-bit inode) | `st_ctimespec.tv_sec` + `tv_nsec` | `st_ino` (u64) | `st_dev` (int32, string) |
| Windows | `GetFileInformationByHandleEx` (`FileBasicInfo` + `FileIdInfo`) | `ChangeTime` (FILETIME → UTC ticks) | 128-bit `FileId` (hex string) | `VolumeSerialNumber` (u64, string) |

**Deliberate timestamp-stomp class (Windows).** `SetFileTime` on Windows cannot set `ChangeTime` — only the kernel's `NtSetInformationFile` can. This means `ChangeTime` is far harder to tamper with than `LastWriteTime`, making it a stronger signal. The hashcache relies on this property.

**Network filesystem exclusion.** On Linux, `statfs` detects CIFS/SMB2/NFS by `f_type` magic and returns `null` → the caller uses the fingerprint floor. On Windows, `GetDriveType` returns `DRIVE_REMOTE` → `null`. On macOS the source volume is assumed local (network detection is skipped). Any `TryGet` failure (exception, unsupported FS) also returns `null` → floor.

**macOS is arm64-only.** The Darwin `stat` path is guarded to Apple Silicon: on x86_64 macOS the bare `stat` symbol can resolve to the legacy 32-bit-inode struct, which would misread the field offsets and yield a silently-wrong signal. Intel Macs are unsupported — `TryGet` returns `null` there and the fingerprint floor is used.

### The statx → stat fallback for the Synology DS918+ (kernel 4.4)

`statx` was added in Linux 4.11; `ENOSYS` from `statx` means the kernel predates it. `TryGetViaStatx` returns `null` on any non-zero return code, and `TryGetLinux` then calls `TryGetViaStat`. The `struct stat` layout is architecture-specific; only the x86_64 layout is implemented (the DS918+ Intel Celeron J3455 is x86_64), guarded by `RuntimeInformation.ProcessArchitecture != Architecture.X64` → `null`. On a non-x64 Linux kernel < 4.11, both paths return `null` and the fingerprint floor is used.

**Validated on hardware (Synology DS918+, `uname -r` = `4.4.302+`).** The `[LibraryImport("libc", EntryPoint = "stat")]` binds the *bare* `stat` symbol. glibc historically exposed the syscall only through the versioned `__xstat(ver, path, buf)` wrapper, so there was a risk the bare-`stat` P/Invoke would fail to resolve (`EntryPointNotFoundException` → swallowed → `null` → ctime fast-lane unavailable). **This was tested on the target NAS and the bare `stat` symbol binds correctly.** The DS918+ runs kernel 4.4.302 (pre-`statx`), so `statx` returns ENOSYS and `TryGetViaStat` is the active path; a cold-then-warm `--fast-hash` run logged `114 -> full-hash (cache miss)` followed by `114 -> reused (ctime match)` with **zero** `size+fp match` (floor) hits — i.e. the `stat` fallback returns correct, stable signals and the ctime fast-lane serves every unchanged file with zero byte reads. **No `__xstat` fallback is needed.** (If a future DSM/glibc ever drops the bare `stat` export, the fix would be a `__xstat(_STAT_VER, …)` entry point reusing the same `LinuxStatBuf` layout; correctness is unaffected meanwhile because the sparse-fingerprint floor still avoids full re-reads.)

### The sparse fingerprint

`SparseFingerprint` computes a deterministic spot-hash of a file using evenly-spaced regions derived from the file size:

- **Block size (`B`)**: 256 KiB per region.
- **Stride (`S`)**: 1 GiB between region start points.
- **Region count (`k`)**: `clamp(ceil(size / S), MinBlocks=4, MaxBlocks=64)`.
- Small files (size ≤ k × B): one whole-file read (no seeks).
- Large files: k regions at offsets `floor(i × (size − B) / (k − 1))` for i = 0…k−1.
- Digest: SHA-256 over `size (8 LE bytes) ‖ region-bytes-concatenated`.

The regions are deterministic from the file size, so the same regions are re-sampled across runs. Coverage: for a 1 TB file, k = 64 regions × 256 KiB = 16 MiB sampled (0.0015 % of the file). For a 4 GiB file, k = 4 × 256 KiB = 1 MiB. The `Sampler` inner class captures regions during a sequential full-hash read at zero extra I/O cost.

**Benchmark-pending note.** The constants (`B = 256 KiB`, `S = 1 GiB`, `MinBlocks = 4`, `MaxBlocks = 64`) were chosen by analysis but have not yet been benchmarked on the DS918+ target. See the benchmark procedure below.

**Sparse-fingerprint coverage limit.** For files larger than ~64 GiB the sampled fraction drops below 0.024% and for very large files the fingerprint becomes a statistical, not exhaustive, change detector. This is acceptable given the opt-in trust model and that any false positive merely triggers a full re-hash on the next `--fast-hash` run.

### The disposable-cache invariant

The hashcache stores `(path, size, mtime, ctime, inode, dev, signal_set, sparse_fp, fp_algo, content_hash, last_verified)` keyed by `RelativePath`. `mtime` is stored for diagnostics only and is not used in any verdict. The cache is:

- **Local and per-machine.** `~/.arius/<account>-<container>/hash/cache.sqlite`. Each machine's cache is independent; signals are only compared within the same filesystem. Sequential cross-platform use (archive from Windows, then Linux) is supported: the `signal_set` column stores the provenance (`SignalSets.None = 0`, `Posix = 1`, `Windows = 2`), so a `Windows`-sourced row used from a Linux run will mismatch `signal_set` and fall through to the fingerprint floor.
- **Disposable.** Losing the file costs one full-hash run. It is never referenced by the remote repository and is never shared across machines. Unlike `ChunkIndexLocalStore` (which silently recreates a corrupt DB), a corrupt hashcache is surfaced as an actionable `HashCacheLocalStoreException` instructing the operator to delete the hashcache directory — there is no remote backing and no repair command, and a silent in-place rebuild is intentionally deferred (a future option). Recovery is the same one full-hash run.
- **Not concurrently multi-platform.** Concurrent archives from two machines into the same files are not supported. The hashcache is local; each machine has its own. The remote repository (chunk index + snapshots) is the source of truth for deduplication correctness; the hashcache only accelerates the hashing stage of a single machine's archive.

### Pointer opt-in (the pointer-default change)

Prior to this ADR, `archive` wrote `.pointer.arius` sidecars by default and `--no-pointers` suppressed them; `--remove-local` and `--no-pointers` were mutually exclusive (removing the binary while writing no pointer loses the path). This created unnecessary filesystem noise for users who never use the restore workflow from pointer-only state.

After this ADR:

- `WritePointers` defaults to `false`. Pass `--write-pointers` to write sidecars.
- `--remove-local` **requires** `--write-pointers`: removing the binary while writing no pointer would leave no local record of the file, so the combination `RemoveLocal && !WritePointers` is rejected up front — validated in **both** the CLI (`ArchiveVerb`) and the handler (`ArchiveCommandHandler`, the backstop for programmatic/API callers). An earlier revision *implied* pointers from `--remove-local`; that silent implication was replaced by this explicit validation so the dangerous combination is an error, not a magic auto-correct.
- `--no-pointers` is gone from `archive` (it is still present on `restore` where it has a different, valid use).
- The old `--remove-local` + `--no-pointers` mutual-exclusion validation is replaced by the `--remove-local` requires `--write-pointers` validation.
- Legacy v5 pointer-only files are still always upgraded in place (stage 5b), regardless of `WritePointers`.

## Benchmark procedure (for the DS918+ operator)

Do not attempt to run this without the physical hardware: the benchmark requires the target Synology DS918+ (kernel 4.4, Intel J3455 x86_64, NAS volume mounted as local ext4 under Docker) and a representative dataset.

1. Pick a stable representative dataset (e.g. 100 GB of photos that haven't changed recently).
2. **Cold-hash baseline** — run `arius archive <path>` (no `--fast-hash`) against the dataset. Record total wall-clock time and I/O bytes from `/proc/diskstats` or `iostat`.
3. **Warm-cache run** — immediately run `arius archive <path> --fast-hash` against the same unchanged dataset. Record the same metrics. Log output (`[fast-hash] summary: reused …, rehashed …`) shows the cache hit rate.
4. **Verify POSIX signals on the NAS volume** — in the warm run, check that `TryGetChangeSignals` returns non-null signals. Set `ARIUS_LOG_LEVEL=Debug` on the container and look for `[fast-hash] … -> reused (ctime match)` in `docker logs arius` (or `~/.arius/<account>-<container>/logs/arius-*.txt`). On the validated DS918+ target (kernel 4.4.302, pre-`statx`) the `stat` x86_64 fallback binds and `ctime match` is the expected, healthy outcome. If instead you see only `size+fp match` hits with no `ctime match`, the volume is most likely exposed as CIFS/NFS rather than a local mount (the network-FS guard returns `null` → floor); a regressed `stat` symbol binding would be the other suspect.
5. **Tune sampling constants** — if the warm-cache run is slower than expected and the fingerprint floor is dominating (many `size+fp match` vs `ctime match`), reduce `BlockSize` or `MaxBlocks` in `SparseFingerprint.cs` and re-run. If false positive misses are observed (files reported as `fp differs` but content did not change), increase `BlockSize` or `MaxBlocks`. Record the final values and rationale in this ADR or a follow-up.
6. **Document results** — note the hardware, dataset size, baseline time, warm-cache time, and hit/rehash counts. These are the inputs for future tuning decisions.

## More Information

- Design spec (original intent, before implementation): [design.md](../history/agentic-plans/2026-06-29-fast-hash/design.md), frozen in `docs/history/` alongside its [plan](../history/agentic-plans/2026-06-29-fast-hash/plan.md) and [hardening review](../history/agentic-plans/2026-06-29-fast-hash/code-review.txt)
- Implementation: `src/Arius.Core/Shared/HashCache/` and `src/Arius.Core/Shared/FileSystem/NativeFileSignals.cs`
- Tests: `src/Arius.Core.Tests/Features/ArchiveCommand/ArchiveFastHashTests.cs` and `ArchivePointerDefaultTests.cs`
