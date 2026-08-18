# Archive progress / ETA fixes + `[ETA]` log mirroring

**Date:** 2026-07-21
**Status:** Design — awaiting approval
**Source of truth:** `arius-20260721.txt` (a real `ARIUS_LOG_LEVEL=Debug` run), analysed via the `[ETA]` trace.

## Problem

A real archive run (`7d4ff3d2…`, 75 min, 1071 GB scanned, ~10.5 GB actually new/uploaded — heavily
deduplicated) exposed four anomalies in the archive progress reporting. All four were read directly
off the `[ETA]` debug trace and cross-checked against the code.

| # | Anomaly | Evidence (from trace) | Root cause |
|---|---------|-----------------------|-----------|
| A | Progress **bar runs backwards** (23×) — visible in the pill ring/% and jobs list | `pct 96→92`, `93→87`, `80→65` … each coincides with `newTotal` growing | Server `pct = uploaded / totalNew`. `totalNew` (`_queuedNewBytes`) is fed by `ChunkUploadingEvent` (fires as each chunk *starts uploading*), so the denominator is discovered from the same stream that does the work and grows **51×** (0.20 GB → 10.48 GB). Denominator grows ⇒ pct regresses. |
| B | **ETA spikes to 58 h** for a 75-min job | `eta=210042s (58.3h) at pct=4`, right after scan completes | `uploadDenom = max(totalNew, total − deduped)`. The instant `total` becomes known, `deduped` badly lags (`672 GB` vs `total 1071 GB`), so `total − deduped ≈ 399 GB` at ~2.3 MB/s ⇒ ~58 h. Decays only as dedup catches up. |
| C | **pct and ETA disagree** mid-run | bar ≈ 90 % while ETA implies far more work | pct denominator = `totalNew`; ETA denominator = `max(totalNew, total − deduped)`. Different denominators. |
| D | **Throughput reads 16.4 GB/s** at completion | last 6 ticks `rate=16.42 GB/s` | `FileHashingEvent` is published *before* the hash is computed (`ArchiveCommandHandler.cs:347`) and credits the full file size up front, so `hashed` is really "bytes entering the hasher"; the `hashRate` EMA is inflated to tens of GB/s and never decays (`FoldRate` holds on flat ticks). At `eta=0` the tie-break `l >= u` picks the hash branch, surfacing the stale hash rate as the user-facing throughput. |

Anomaly E (`total = 0` for the first ~60 s while 674 GB is already "hashed") is inherent to enumeration
(total is only known at `ScanComplete`); B's gating turns that window into an honest "estimating…"
instead of a wild number, so E needs no separate fix.

## Where each value surfaces in Arius.Web (verified)

- **Pill** (`job-pill.component.ts`): ring + "N %" use the server `snapshot.pct` (regresses); line 2 =
  `formatEta(etaSeconds)` · `formatThroughput(throughputBytesPerSec)`.
- **Detail page** (`job-detail.component.ts`): the layered bar uses `archiveBarLayers()` over
  **`totalBytes`** (stable) — so the *detail* bar does **not** regress; but `bigEta`/`subEta`/tiles read
  `etaSeconds`, `etaIsUpperBound`, `throughputBytesPerSec` directly (so B and D are visible here).
- **Jobs list**: `JobDto.pct` (persisted / live snapshot pct) — regresses with the pill.

## Design decisions (agreed)

1. **ETA behaviour while hashing/dedup incomplete:** *phase-split*.
2. **Scope:** *also fix Core* (credit `hashed` on completion).
3. **`[ETA]` log mirroring:** *raw wire fields* (mirror the `SnapshotDto` the web consumes; no
   formatter duplication).

## The fixes

All snapshot math lives in `JobSink.BuildSnapshot` (`Arius.Api/Jobs/JobSink.cs`).

### Fix A — monotonic pct (backwards bar)

```
pct = total > 0 ? clamp((uploaded + deduped) * 100 / total, 0, 100) : 0
```

This is exactly the "filled fraction" the detail-page bar already shows (`archiveBarLayers.deduped`
band). Numerator terms only grow, denominator (`total`) is fixed after scan ⇒ **monotonic**. The pill
and jobs list stop regressing and now agree with the detail bar. (One forward jump remains: pct jumps
0 → ~63 % when `total` becomes known, reflecting hashing/dedup already done during the concurrent scan —
forward, not backward, so acceptable.)

Pointer-only skew (`deduped` can exceed `total` when pointer-only files count full content size while
`total` counts them as 0) is absorbed by the `clamp(…, 0, 100)`.

### Fix B — phase-split ETA (58 h spike + pct/eta consistency)

Keep the existing **`eta = max(hashTerm, uploadTerm)`** structure (it already handles hash-bound and
upload-bound archives gracefully — the `max` means the ETA never collapses to "≤ 1 sec" while a term of
real work remains). The 58 h spike comes entirely from the **upload denominator**, so that is what
changes:

```
hashTerm    = hashRate     > 0 ? ceil(max(0, total − hashed)      / hashRate)     : null   // archive only
uploadTerm  = transferRate > 0 ? ceil(max(0, uploadDenom − uploaded) / transferRate) : null
uploadDenom = _newByteTotalFinal ? _newByteTotal : totalNew        // was: max(totalNew, total − deduped)
eta         = (archive && total == 0) ? null : max(hashTerm, uploadTerm)   // null if both terms null
```

- **Drop `total − deduped`.** It was the sole source of the spike (loose while `deduped` lags). The
  additive `totalNew` (queued new bytes) is a well-behaved lower bound that converges to the true
  new-byte total, so `uploadTerm` climbs gently instead of spiking to 58 h.
- **New Core event `RoutingCompleteEvent(long NewBytes)`** — published when the dedup/route stage
  (Stage 3) drains (after the `await foreach` at `ArchiveCommandHandler.cs:515`), carrying
  `incrementalSize` (the exact original bytes routed for upload). The **sink** gains
  `SetNewByteTotal(long)` → stores `_newByteTotal`, sets `_newByteTotalFinal = true`. After it fires the
  denominator is **exact**, so the ETA tightens and stops being an upper bound. This is skip-safe (it
  is an event, not `hashed >= total`, so unreadable/skipped files can't wedge the gate).
- **`etaIsUpperBound = archive && !_newByteTotalFinal`** — provisional "≤" until routing completes,
  exact after. (Replaces the old `hashed < total`, which was fragile once `hashed` is credited on
  completion.)
- **`reportedRate`** (the user-facing "sustained" throughput) = **the rate of whichever term binds**:
  `hashTerm` strictly the larger ⇒ `hashRate`; otherwise ⇒ `transferRate`. Two consequences: a
  fully-deduped hash-only archive still shows its hash rate (existing behaviour preserved), and the
  end-of-upload case (`eta == 0`, both terms 0, upload-bound) now reports `transferRate` instead of the
  stale hash rate — **this is Fix D**. Restore is unchanged (`transferRate`, never an upper bound).

`totalNewBytes`/`AddQueuedNew` are **left as-is** — they still feed the web "Uploaded X of Y new data"
display. (That "Y" still grows during the run; it is cosmetic, not one of the reported anomalies, and
out of scope here.)

### Fix C — throughput no longer leaks the stale hash rate (16 GB/s spike)

Folded into Fix B's `reportedRate` rule: the throughput is the rate of the **binding** ETA term. The
16 GB/s came from the old `eta == 0` tie-break `l >= u` selecting the hash branch; with the binding
rule (hash rate only while `hashTerm` is strictly the larger term) the end-of-upload case is
upload-bound and reports `transferRate`. The fully-deduped hash-only archive still shows its hash rate
(covered by the existing `Eta_is_hash_bound…` test). No separate change beyond Fix B.

### Fix (Core) — credit `hashed` on completion

- `FileHashedEvent` gains `long FileSize` (defaulted `= 0` to avoid churn across ~12 existing call
  sites, real value passed at `ArchiveCommandHandler.cs:397`).
- New `FileHashedForwarder : INotificationHandler<FileHashedEvent>` → `sink.AddHashed(n.FileSize)`.
- `FileHashingForwarder` keeps only `SetPhase("hash-route")` (drops `AddHashed`).

Effect: `hashed` counts *completed* hashes, so the "Hashed & routed" bar and the hash-term ETA track
real work for hash-bound archives (for dedup-heavy/cached repos it still completes fast — correctly).
Because the phase-split gate is now the `RoutingCompleteEvent` (not `hashed >= total`), skipped/unreadable
files — which never publish `FileHashedEvent` — do **not** break the gate.

New forwarders (`FileHashedForwarder`, `RoutingCompleteForwarder`) are auto-registered by
`services.AddMediator()` (source-gen discovers `INotificationHandler`s).

### `[ETA]` log = raw wire fields

Rewrite `JobSink.LogEtaDiagnostics` so the line is the `SnapshotDto` the web actually receives — every
field the Angular client reads, in wire (camelCase-ish) order, so a log line can be compared 1:1 to what
the UI renders:

```
[ETA] job={JobId} phase={Phase} status={Status} pct={Pct} eta={EtaSeconds}s bound={EtaIsUpperBound} tp={ThroughputBytesPerSec}B/s
      | archive total={TotalBytes} totalNew={TotalNewBytes} scanned={ScannedBytes}/{ScannedFiles}f hashed={HashedBytes} uploaded={UploadedBytes} deduped={DedupedBytes}/{DedupedFiles}f warnings={WarningCount}
      | restore restoreTotal={RestoreTotalBytes}/{RestoreTotalFiles}f restored={BytesRestored}/{FilesRestored}f chunks total={ChunksTotal} avail={ChunksAvailable} rehyd={ChunksRehydrated} needs={ChunksNeedingRehydration} pending={ChunksPending}
```

The log logs the exact `JobSnapshot` values (no client-side re-derivation), so what the web computes
from them (bar layers, `formatEta`, `formatThroughput`) is reproducible from the line.

## Testing (lock-down)

`JobSink` already has a clock seam (`_now`) and counter setters — unit-testable without SignalR.

- **A monotonic pct:** drive scan→hash→dedup→upload with a growing `totalNew`; assert `pct` never
  decreases and equals `(uploaded+deduped)/total`.
- **B no spike:** reproduce the log's ordering (total known while deduped lags); assert ETA before
  `RoutingComplete` is the small hash-term with `EtaIsUpperBound=true`, never a huge value; after
  `RoutingComplete` assert exact `(_newByteTotal−uploaded)/rate` and `EtaIsUpperBound=false`.
- **C throughput:** with an inflated `hashRate` and modest `transferRate`, assert
  `throughputBytesPerSec == transferRate` including at `eta==0`.
- **Core hashed-on-completion:** assert `AddHashed` is driven by `FileHashedEvent`, not
  `FileHashingEvent`; `FileHashingEvent` only advances the phase. Add a Core-level assertion that
  `RoutingCompleteEvent(incrementalSize)` fires once with the correct total.
- **Regression fixtures (implemented in `JobSinkProgressFixesTests`):** each anomaly's exact condition
  from the trace — growing `totalNew` (backwards bar), `deduped` lagging `total` after scan (58 h spike),
  an inflated hash EMA at `eta == 0` (16 GB/s) — reproduced and asserted against the fixed behaviour.
- **Web:** no change — the DTO shape is unchanged; `pct`, `etaSeconds`, `throughputBytesPerSec` just carry
  better values, so `job-format.spec.ts` and the components are unaffected.

## Actual test inventory

- `JobSinkProgressFixesTests` (5): pct is `(uploaded+deduped)/total`; pct never regresses; ETA uses the
  queued-new denominator not `total−deduped`; routing-complete makes the ETA exact and drops the "≤";
  throughput at completion is the transfer rate not the stale hash rate.
- `ArchiveForwardersHashedRoutingTests` (3): `FileHashedForwarder` credits hashed bytes;
  `FileHashingForwarder` only advances the phase; `RoutingCompleteForwarder` fixes the exact total.
- `ArchiveFastHashTests` (+1, Core): a real archive run publishes `FileHashedEvent` with the file size and
  one `RoutingCompleteEvent` carrying the exact new-byte total (`incrementalSize`).
- `JobSinkEtaTests`: `Eta_is_an_upper_bound_until_routing_completes` (updated to the new gate) and a new
  `Eta_diagnostics_line_mirrors_the_snapshot_wire_fields`.

## Out of scope

- Redesigning `totalNewBytes` / the "of Y new data" display (Y still grows; cosmetic, unreported).
- The ~60 s `total = 0` scan window (inherent; now shows honest "estimating…").
```
