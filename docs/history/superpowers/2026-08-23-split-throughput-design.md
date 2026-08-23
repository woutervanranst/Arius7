# Split throughput into hash rate + upload rate — design

**Status:** approved (brainstorm) · **Date:** 2026-08-23 · **Area:** `Arius.Api` jobs progress, `Arius.Web`

## Problem

A single `JobSnapshot.ThroughputBytesPerSec` is multiplexed over two genuinely concurrent, physically
different streams: **local hashing** (disk read + hash) and **network upload**. `BuildSnapshot` collapses
the two internal EMAs (`_hashRate`, `_transferRate`) into one number by "report the rate of whichever ETA
term binds." Every throughput anomaly to date is an edge case in that *arbitration*, not the measurement:

| Symptom | Arbitration fault |
|---|---|
| `tp=0B/s` for ~65 min during scan of a 480 GB / 543k-file repo, while `hashed` climbed to 150 GB | no ETA exists while `total == 0`, so the code fell through to `reportedRate = transferRate`, which is 0 on a dedup-heavy repo (nothing uploaded) |
| 16 GB/s "throughput" at completion (prior Fix D) | the `eta == 0` tie-break picked the stale, never-decaying hash EMA |

The observed evidence lives in `/Users/wouter/Downloads/arius-20260823.txt` (job
`b66cf994…`): `tp=0B/s` from `07:25` until `08:30:59`, then jumping to `tp≈79 MB/s` the instant `total`
became known. A stop-gap (`reportedRate = Math.Max(hashRate, transferRate)` in the `total == 0` branch)
is currently in the working tree on `fix-progress` (with a passing regression test); this design
supersedes it with a structural fix.

## Root cause

The throughput scalar conflates two streams, forcing a "which stream is active/binding" decision at every
phase boundary — and the consumer (or a fall-through default) keeps getting it wrong. The fix is to **stop
collapsing**: expose each stream's rate, and put the "is this stream live?" decision in the one place that
holds the facts (`JobSink`), not in the display.

## Principle

Separate the **prediction** from the **observation**:

- **ETA** (`EtaSeconds`, `EtaIsProvisional`) — a *prediction*. Stays a single number, keeps its existing
  binding-term logic (`eta = max(hashTerm, uploadTerm)`). **Unchanged.**
- **Throughput** — an *observation*. Becomes two independently-honest rates, each zeroed by the backend
  when its stream is not live. The two no longer have to agree with, or be derived from, the ETA.

## Design (backend owns liveness; web is a dumb renderer)

### 1. `JobSink.BuildSnapshot` — independent rate block

Keep the `eta` / `etaIsProvisional` branch logic exactly as-is. Remove the `reportedRate` selection
(including the `Math.Max` scan stop-gap) and compute the three throughput fields independently:

```
// archive
hashLive    = total == 0 || hashed < total
uploadDenom = newByteTotalFinal ? newByteTotal : totalNew
uploadLive  = uploadDenom > 0 && uploaded < uploadDenom
hashTp      = hashLive   ? hashRate     : 0
upTp        = uploadLive ? transferRate : 0

// restore (no local hashing; the transfer EMA is the download stream)
hashTp      = 0
upTp        = (restoreTotal > 0 && restored < restoreTotal) ? transferRate : 0

dominantTp  = max(hashTp, upTp)
```

`hashRate` / `transferRate` are the existing EMAs from `Rates()`; `hashTp`/`upTp` only gate *whether* the
already-smoothed rate is shown. Liveness is derived from state the sink already has — no new counters, no
timestamps.

### 2. DTO — `JobSnapshot` (`Arius.Api/Jobs/JobSnapshot.cs`) + `api-models.ts`

| Field | Meaning |
|---|---|
| `HashThroughputBytesPerSec` (new) | `hashTp` — hashing rate, 0 when hashing is done / not started |
| `UploadThroughputBytesPerSec` (new) | `upTp` — upload rate (archive) / download rate (restore), 0 when idle/done |
| `ThroughputBytesPerSec` (**kept, redefined**) | `dominantTp = max(hashTp, upTp)` — the pill's single dominant rate |

Keeping `ThroughputBytesPerSec` (redefined) means: the pill needs no code change, persisted `state_json`
snapshots still deserialize, and no other consumer breaks. The two new fields drive the detail tile.

### 3. Web — detail "Throughput" tile (archive `@else` branch only)

The single tile becomes two labeled rows. Per row, from that row's rate + existing counters:

| Condition | Render |
|---|---|
| rate > 0 | `formatThroughput(rate)` + label ("hashing" / "uploading") |
| rate == 0 && stream produced bytes (`hashedBytes>0` / `uploadedBytes>0`) | "done" |
| otherwise | "—" |

Restore detail grid is **unchanged** (it has no throughput tile: Restored · Ready to download ·
Rehydrating).

### 4. Web — pill

No code change. `throughputBytesPerSec` is now the honest dominant rate, so `~12 min · 78 MB/s` works and
no longer reads 0 during scan. For restore the dominant rate is the download rate.

### 5. Diagnostics — `[ETA]` log line

Extend `LogEtaDiagnostics` to mirror both new fields (e.g. `hashTp=…B/s upTp=…B/s` alongside `tp=…`), so
the logged line stays a faithful, replayable copy of the emitted snapshot.

## Behavioural contract change

`Throughput_at_upload_completion_is_transfer_rate_not_stale_hash_rate` currently asserts the tile shows the
**transfer rate** the instant upload finishes. Under the new model both streams are then done, so the tile
shows **"done" (rate 0)**. This matches the approved UI (a finished stream reads "done", not a lingering
number) and still satisfies Fix D's intent (never the stale hash rate). The test is updated accordingly.

## Testing

`Arius.Api.Tests/Jobs` (TUnit):

- **Scan window** (extend existing `Throughput_during_scan_reports_the_hash_rate_not_zero`): `total==0`,
  hashing at 20 MB/s ⇒ `HashThroughputBytesPerSec ≈ 20 MB/s`, `UploadThroughputBytesPerSec == 0`,
  `ThroughputBytesPerSec ≈ 20 MB/s`, `EtaSeconds` null.
- **During upload** (new): `uploaded < denom`, transfer moving, hashing done ⇒ `upTp == transferRate`,
  `hashTp == 0`, `dominantTp == transferRate`.
- **At completion** (rewrite of the Fix D test): hashing done + upload done ⇒ `hashTp == 0`, `upTp == 0`,
  `ThroughputBytesPerSec == 0` (never the ~1 GB/s hash EMA).
- **Fully-deduped archive** (existing `Eta_is_hash_bound…`): still `hashTp ≈ hash rate`, `upTp == 0`.
- **Restore** (new): `restored < restoreTotal`, download moving ⇒ `upTp == transferRate`, `hashTp == 0`.
- **`[ETA]` mirror** (extend `Eta_diagnostics_line_mirrors_the_snapshot_wire_fields`): assert the line
  contains `hashTp=` and `upTp=`.

Web: `job-format` / `job-detail` unit specs cover the row label states (rate / "done" / "—"); pill spec
unchanged in behaviour.

## Out of scope

- A download-rate tile on the **restore** detail page (none exists today).
- Any change to the ETA number, `EtaIsProvisional`, or the layered progress bar.
- `TotalNewBytes` / "of X new data" cosmetics (tracked elsewhere).

## Files touched

- `src/Arius.Api/Jobs/JobSink.cs` — rate block in `BuildSnapshot`, `LogEtaDiagnostics` line.
- `src/Arius.Api/Jobs/JobSnapshot.cs` — two new fields.
- `src/Arius.Web/src/app/core/api/api-models.ts` — two new fields.
- `src/Arius.Web/src/app/features/jobs/job-detail.component.ts` — two-row Throughput tile.
- `src/Arius.Web/src/app/shared/job-format.ts` (+ specs) — row-label helper if warranted.
- `src/Arius.Api.Tests/Jobs/JobSink*Tests.cs` — cases above.
