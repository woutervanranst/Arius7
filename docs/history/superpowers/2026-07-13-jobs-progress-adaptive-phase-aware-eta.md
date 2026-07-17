# Adaptive, Phase-Aware Archive ETA Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the archive "Est. finish" / throughput panels correct for dedup-heavy runs and stable over time, by modelling the ETA as the max of two concurrent constraints (local hashing + upload) with a smoothing window that widens as the job runs.

**Architecture:** All quantitative logic lives server-side in `JobSink` (the client only formats). The single upload-rate EMA is split into two EMAs (transfer = upload+restore, and hash) sampled each ~1s tick. Both use a **continuous-time** EMA whose time constant τ scales with elapsed time (`τ = clamp(elapsed·f, τ_min, τ_max)`), so small jobs stay responsive and long jobs settle. `BuildSnapshot` computes `ETA = max(localEta, uploadEta)` for archive (restore keeps its single download term), reports the **binding** constraint's rate as throughput, and flags the estimate as an upper bound (`EtaIsUpperBound`) until hashing completes. The web renders "≤ …" for a bounded estimate and "Finishing up" at the `snapshot` phase.

**Tech Stack:** C# (.NET, TUnit tests, Microsoft.Testing.Platform) for `Arius.Api`; Angular + TypeScript (Vitest) for `Arius.Web`.

## Global Constraints

- **No Arius.Core changes.** The last commit (`e160109f`) already wired the phase signals (`starting→scan→hash-route→upload→snapshot`) and `FinalizingSnapshotEvent`. Everything here is `Arius.Api` (`JobSink`) + `Arius.Web` formatting only.
- **Restore behaviour is unchanged** — restore keeps a single download-rate term; the max()-of-two model and `EtaIsUpperBound` apply to archive only.
- **The three existing `JobSinkEtaTests` must keep passing** (null-until-known, never-negative, holds-across-flat-tick) — verify explicitly.
- **JSON is camelCase** (`ThroughputBytesPerSec` ⇄ `throughputBytesPerSec`), so the new `EtaIsUpperBound` field surfaces as `etaIsUpperBound`.
- **New snapshot fields are non-`required`** (default), matching the existing restore fields — so `LifecycleScenarioTests.cs:26` (which omits them) keeps compiling.
- **Two counter-based gates** (not phase-based): "estimating…" while `TotalBytes == 0` (authoritative scan-complete = `ScanCompleteEvent`); "≤ upper bound" while `HashedBytes < TotalBytes`.
- **τ tuning constants** (soft — tune later against real runs): `EmaTauElapsedFraction = 0.1`, `EmaTauMinSeconds = 3.0`, `EmaTauMaxSeconds = 600.0`.

---

### Task 1: Two-rate adaptive EMA + max()-of-constraints ETA in `JobSink`

**Files:**
- Modify: `src/Arius.Api/Jobs/JobSnapshot.cs` (add `EtaIsUpperBound` field)
- Modify: `src/Arius.Api/Jobs/JobSink.cs:230-339` (EMA fields, `SampleForEta`, rate accessors, `BuildSnapshot` ETA block, debug log)
- Test: `src/Arius.Api.Tests/Jobs/JobSinkEtaTests.cs`

**Interfaces:**
- Consumes (existing, unchanged): `SetTotals(long files, long bytes)`, `AddQueuedNew(long)`, `AddDeduped(long)`, `AddHashed(long)`, `AddUploaded(ChunkHash, long, long)`, `SetRestoreTotals(long, long)`, `ReportRestoreStreamed(string, long)`, `SampleForEta(DateTimeOffset)`, `BuildSnapshot(DateTimeOffset)`, `_now` clock seam.
- Produces: `JobSnapshot.EtaSeconds` (now `max(localEta, uploadEta)` for archive), `JobSnapshot.ThroughputBytesPerSec` (binding constraint's rate), new `JobSnapshot.EtaIsUpperBound` (bool).

- [ ] **Step 1: Add the `EtaIsUpperBound` field to the snapshot record**

In `src/Arius.Api/Jobs/JobSnapshot.cs`, after the `Pct` line (currently line 20) add:

```csharp
    public required int     Pct            { get; init; }   // byte-weighted; legacy consumers read this
    public          bool    EtaIsUpperBound { get; init; }   // archive: true while hashing incomplete (new-bytes not yet final) → render "≤"
```

(Keep the existing `WarningCount` / `Stats` lines that follow.)

- [ ] **Step 2: Write the failing tests**

Append these four tests to `src/Arius.Api.Tests/Jobs/JobSinkEtaTests.cs` (inside the class):

```csharp
    [Test]
    public async Task Eta_is_hash_bound_when_there_is_nothing_to_upload()
    {
        // Fully-deduped archive: 100 MB to hash, 0 new bytes to upload. The OLD upload-only model
        // read ~null/0 here; the new model must surface the remaining HASH time.
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 100, bytes: 100_000_000);
        s.AddQueuedNew(0);                 // nothing new queued
        s.AddDeduped(100_000_000);         // everything deduped → total-deduped = 0

        s.SampleForEta(t0);                // baseline
        s.AddHashed(10_000_000);           // 10 MB hashed
        s.SampleForEta(t0.AddSeconds(1));  // 10 MB/s hash rate; 90 MB remaining → 9 s

        var snap = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(snap.EtaSeconds).IsNotNull();
        await Assert.That(snap.EtaSeconds!.Value).IsBetween(8, 10);
        await Assert.That(snap.ThroughputBytesPerSec).IsBetween(9_500_000, 10_500_000);  // reports the HASH rate
        await Assert.That(snap.EtaIsUpperBound).IsTrue();                                  // hashing not done
    }

    [Test]
    public async Task Eta_is_upper_bound_until_hashing_completes()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 10, bytes: 10_000_000);
        s.AddQueuedNew(10_000_000);

        s.SampleForEta(t0);
        s.AddUploaded(ChunkHash.Parse(new string('b', 64)), 0, 1_000_000);
        s.AddHashed(4_000_000);                                   // 4 MB of 10 MB hashed
        s.SampleForEta(t0.AddSeconds(1));
        await Assert.That(s.BuildSnapshot(t0.AddSeconds(1)).EtaIsUpperBound).IsTrue();

        s.AddHashed(6_000_000);                                   // hashing now complete (10 MB = total)
        s.SampleForEta(t0.AddSeconds(2));
        await Assert.That(s.BuildSnapshot(t0.AddSeconds(2)).EtaIsUpperBound).IsFalse();
    }

    [Test]
    public async Task Restore_eta_uses_download_rate_and_is_not_an_upper_bound()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetRestoreTotals(files: 5, bytes: 10_000_000);

        s.SampleForEta(t0);
        s.ReportRestoreStreamed("f", 1_000_000);                  // 1 MB downloaded
        s.SampleForEta(t0.AddSeconds(1));                         // 1 MB/s; 9 MB remaining → 9 s

        var snap = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(snap.EtaSeconds!.Value).IsBetween(8, 10);
        await Assert.That(snap.EtaIsUpperBound).IsFalse();
    }

    [Test]
    public async Task Adaptive_window_dampens_a_late_spike_more_than_an_early_one()
    {
        // Same 1 MB/s warm-up + same 3 MB spike; the spike lands after a long elapsed on one sink
        // (wide window → small alpha → barely moves) and after a short elapsed on the other.
        static ChunkHash Chunk(int i) => ChunkHash.Parse(i.ToString("x").PadLeft(64, '0'));
        static double RateAfterSpike(int warmupTicks)
        {
            var t0 = DateTimeOffset.UnixEpoch;
            var s  = new JobSink();
            s.SetTotals(1, 1_000_000_000);
            s.AddQueuedNew(1_000_000_000);
            var t = t0;
            s.SampleForEta(t);                                   // start (anchors elapsed)
            for (var i = 0; i < warmupTicks; i++)
            {
                s.AddUploaded(Chunk(i), 0, 1_000_000);           // steady 1 MB/s
                t = t.AddSeconds(1);
                s.SampleForEta(t);
            }
            s.AddUploaded(Chunk(10_000), 0, 3_000_000);          // 3 MB spike this tick
            t = t.AddSeconds(1);
            s.SampleForEta(t);
            return s.BuildSnapshot(t).ThroughputBytesPerSec;     // upload binds → this is the transfer rate
        }

        var early = RateAfterSpike(warmupTicks: 1);              // elapsed ≈ 2 s → τ = 3 s
        var late  = RateAfterSpike(warmupTicks: 120);            // elapsed ≈ 121 s → τ ≈ 12 s
        await Assert.That(early).IsGreaterThan(1_000_000.0);
        await Assert.That(late).IsGreaterThan(1_000_000.0);
        await Assert.That(late).IsLessThan(early);               // wider window = steadier
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test src/Arius.Api.Tests --treenode-filter "/*/*/JobSinkEtaTests/*"`
Expected: the four new tests FAIL (no `EtaIsUpperBound` compile / wrong ETA / throughput 0); the three original tests still compile.

- [ ] **Step 4: Replace the EMA state block**

In `src/Arius.Api/Jobs/JobSink.cs`, replace the smoothed-throughput field block (currently lines 230-236):

```csharp
    // ── Smoothed throughput (EMA over ~1s samples) → a stable ETA ────────────────
    // A job is either archive or restore, never both, so the two progress-byte counters sum into one clock.
    private readonly object _sampleLock = new();
    private double _emaRate;                        // EMA-smoothed bytes/sec
    private DateTimeOffset? _lastSampleAt;
    private long _lastSampleProgress;
    private const double EmaAlpha = 0.2;            // ~5-tick (≈5s) effective smoothing at the 1s cadence
```

with:

```csharp
    // ── Smoothed throughput (two EMAs, adaptive window) → a stable, phase-aware ETA ──
    // Archive work is two concurrent streams: local hashing and upload. Restore has one (download),
    // which rides the transfer EMA (upload==0 for restore). Each EMA is continuous-time — its alpha is
    // derived from the real gap dt and a time constant τ that widens with elapsed time, so a 2-minute job
    // stays responsive while a multi-hour job settles.
    private readonly object _sampleLock = new();
    private double _transferRate;                   // EMA bytes/sec of uploaded+restored (network)
    private double _hashRate;                        // EMA bytes/sec of hashed (local)
    private DateTimeOffset? _startedAt;              // first-sample anchor for the elapsed-scaled window
    private DateTimeOffset? _lastSampleAt;
    private long _lastTransferProgress;
    private long _lastHashProgress;
    private const double EmaTauElapsedFraction = 0.1;   // τ grows at 1/10 of elapsed…
    private const double EmaTauMinSeconds      = 3.0;   // …floored (small jobs stay responsive)…
    private const double EmaTauMaxSeconds      = 600.0; // …and capped at 10 min (long jobs stay steady).
```

- [ ] **Step 5: Replace `SampleForEta` and the rate accessor**

Replace `SampleForEta` and `RateBytesPerSec` (currently lines 238-272):

```csharp
    /// <summary>Fold the latest instantaneous throughput into two EMAs (transfer = upload+restore, and hash).
    /// A flat tick HOLDS a rate rather than decaying it, so a between-chunk gap can't inflate the ETA — only
    /// sustained progress moves an estimate. The smoothing alpha is derived from the real gap and a time
    /// constant τ that widens with elapsed time, so short jobs react quickly and long jobs stop swinging.</summary>
    public void SampleForEta(DateTimeOffset now)
    {
        lock (_sampleLock)
        {
            var transfer = Interlocked.Read(ref _uploadedBytes) + Interlocked.Read(ref _bytesRestored);
            var hashed   = Interlocked.Read(ref _hashedBytes);
            _startedAt ??= now;
            if (_lastSampleAt is { } last)
            {
                var dt = (now - last).TotalSeconds;
                if (dt > 0)
                {
                    var elapsed = (now - _startedAt.Value).TotalSeconds;
                    var tau     = Math.Clamp(elapsed * EmaTauElapsedFraction, EmaTauMinSeconds, EmaTauMaxSeconds);
                    var alpha   = 1 - Math.Exp(-dt / tau);
                    _transferRate = FoldRate(_transferRate, transfer - _lastTransferProgress, dt, alpha);
                    _hashRate     = FoldRate(_hashRate,     hashed   - _lastHashProgress,     dt, alpha);
                    _lastSampleAt = now;
                    _lastTransferProgress = transfer;
                    _lastHashProgress     = hashed;
                }
            }
            else
            {
                _lastSampleAt = now;
                _lastTransferProgress = transfer;
                _lastHashProgress     = hashed;
            }
        }
    }

    /// <summary>One EMA fold. A non-positive delta (flat tick) holds the prior rate; the first positive sample
    /// seeds the EMA with the raw instantaneous rate (so a single observation isn't damped from zero).</summary>
    private static double FoldRate(double rate, long delta, double dt, double alpha)
    {
        if (delta <= 0) return rate;
        var instant = delta / dt;
        return rate <= 0 ? instant : alpha * instant + (1 - alpha) * rate;
    }

    private (double transfer, double hash) Rates()
    {
        lock (_sampleLock) return (_transferRate, _hashRate);
    }
```

- [ ] **Step 6: Rewrite the ETA/throughput block in `BuildSnapshot`**

In `BuildSnapshot`, replace the rate fetch (currently line 284):

```csharp
        var rate     = RateBytesPerSec();
```

with:

```csharp
        var (transferRate, hashRate) = Rates();
```

Then replace the ETA + pct preamble (currently the `etaDenominator` / `eta` lines 300-304), i.e. this block:

```csharp
        // ETA uses a STABLE upload-work estimate so it doesn't read "seconds" early: totalNew (queuedNewBytes) is
        // the exact new-bytes-to-upload but is only discovered incrementally as routing queues chunks, so early on
        // it sits far below the real total (→ absurdly low ETA). max(totalNew, total−deduped) is known from the
        // scan, converges to the truth as routing completes, and the max() avoids the pointer-only underflow that
        // total−deduped alone would hit (deduped can exceed scanned bytes). Restore's total is known up front.
        var etaDenominator = isRestore ? restoreTotal : Math.Max(totalNew, Math.Max(0L, total - deduped));
        long? eta = etaDenominator > 0 && rate > 0 ? (long)Math.Ceiling(Math.Max(0L, etaDenominator - progress) / rate) : null;
```

with:

```csharp
        // ETA models the run as concurrent constraints and takes the slower (max):
        //  • upload: (upperBoundNewBytes − uploaded) / transferRate. The denominator max(totalNew, total−deduped)
        //    is a scan-known UPPER bound that tightens to the exact new-bytes as routing/dedup completes, and the
        //    max() avoids the pointer-only underflow total−deduped alone would hit.
        //  • local (archive only): (total − hashed) / hashRate — the read/hash backlog. This is what makes a
        //    fully-deduped archive read its true (hash-bound) time instead of "seconds".
        // Restore has no local term (its bytes are known up front and only download). While the scan is still
        // running (total == 0) an archive stays "estimating" — a partial totalNew would under-read.
        long? eta;
        double reportedRate;
        var etaIsUpperBound = false;
        if (isRestore)
        {
            eta = restoreTotal > 0 && transferRate > 0
                ? (long)Math.Ceiling(Math.Max(0L, restoreTotal - restored) / transferRate)
                : null;
            reportedRate = transferRate;
        }
        else if (total == 0)
        {
            eta = null;                    // scan not complete → estimating
            reportedRate = transferRate;
        }
        else
        {
            var uploadDenom = Math.Max(totalNew, Math.Max(0L, total - deduped));
            long? uploadEta = transferRate > 0 ? (long)Math.Ceiling(Math.Max(0L, uploadDenom - uploaded) / transferRate) : null;
            long? localEta  = hashRate     > 0 ? (long)Math.Ceiling(Math.Max(0L, total - hashed)         / hashRate)     : null;
            // Bind on the slower constraint. Report that constraint's rate so "sustained N MB/s" always explains
            // the ETA (upload rate when upload-bound, hash rate when hash-bound).
            if (localEta is { } l && (uploadEta is not { } u || l >= u)) { eta = localEta;  reportedRate = hashRate; }
            else                                                          { eta = uploadEta; reportedRate = transferRate; }
            etaIsUpperBound = eta is not null && hashed < total;   // new-bytes total not final until hashing done
        }
```

Then update the snapshot assignment (currently line 322):

```csharp
            EtaSeconds = eta, ThroughputBytesPerSec = rate, Pct = pct,
```

to:

```csharp
            EtaSeconds = eta, ThroughputBytesPerSec = reportedRate, Pct = pct, EtaIsUpperBound = etaIsUpperBound,
```

- [ ] **Step 7: Update the `[ETA]` debug trace to log both rates**

In `LogEtaDiagnostics` (currently line 117), extend the format string and args so the split rates are traceable. Change the `rate={Rate:F0}B/s` token to `xfer={Xfer:F0}B/s hash={Hash:F0}B/s bound={Bound}` and add the args. Replace:

```csharp
            "[ETA] job={JobId} phase={Phase} pct={Pct} eta={Eta}s rate={Rate:F0}B/s | archive up={Uploaded} newTotal={TotalNew} hashed={Hashed} scanned={Scanned} total={Total} deduped={Deduped} | restore restored={Restored}/{RestoreTotal}B files={FilesRestored}/{RestoreTotalFiles} chunks total={ChunksTotal} avail={ChunksAvailable} rehyd={ChunksRehydrated} needs={ChunksNeedingRehydration} pending={ChunksPending}",
            JobId, snap.Phase, snap.Pct, snap.EtaSeconds, snap.ThroughputBytesPerSec,
```

with:

```csharp
            "[ETA] job={JobId} phase={Phase} pct={Pct} eta={Eta}s bound={Bound} rate={Rate:F0}B/s | archive up={Uploaded} newTotal={TotalNew} hashed={Hashed} scanned={Scanned} total={Total} deduped={Deduped} | restore restored={Restored}/{RestoreTotal}B files={FilesRestored}/{RestoreTotalFiles} chunks total={ChunksTotal} avail={ChunksAvailable} rehyd={ChunksRehydrated} needs={ChunksNeedingRehydration} pending={ChunksPending}",
            JobId, snap.Phase, snap.Pct, snap.EtaSeconds, snap.EtaIsUpperBound, snap.ThroughputBytesPerSec,
```

- [ ] **Step 8: Run the full ETA test class to verify all seven pass**

Run: `dotnet test src/Arius.Api.Tests --treenode-filter "/*/*/JobSinkEtaTests/*"`
Expected: PASS — 3 original + 4 new = 7 tests.

- [ ] **Step 9: Build the API to confirm no other references broke**

Run: `dotnet build src/Arius.Api`
Expected: Build succeeded (no reference to the removed `_emaRate`/`RateBytesPerSec`/`EmaAlpha`).

- [ ] **Step 10: Commit**

```bash
git add src/Arius.Api/Jobs/JobSink.cs src/Arius.Api/Jobs/JobSnapshot.cs src/Arius.Api.Tests/Jobs/JobSinkEtaTests.cs
git commit -m "feat(jobs): phase-aware, adaptive archive ETA (max of hash + upload, elapsed-scaled window)"
```

---

### Task 2: Render the upper bound and finalize state on the web

**Files:**
- Modify: `src/Arius.Web/src/app/core/api/api-models.ts:98-100` (add `etaIsUpperBound`)
- Modify: `src/Arius.Web/src/app/shared/job-format.ts:4-10` (`formatEta` upper-bound prefix)
- Modify: `src/Arius.Web/src/app/features/jobs/job-detail.component.ts:355-371` (`finishTime`, `bigEta`)
- Test: `src/Arius.Web/src/app/shared/job-format.spec.ts`
- Modify (test factories): `src/Arius.Web/src/app/core/api/realtime.service.spec.ts:5-12`, `src/Arius.Web/src/app/core/state/job-pill.store.spec.ts:10-17`

**Interfaces:**
- Consumes: `JobSnapshot.etaSeconds`, `JobSnapshot.throughputBytesPerSec`, `JobSnapshot.phase`, and the new `JobSnapshot.etaIsUpperBound` (from Task 1).
- Produces: `formatEta(seconds, isUpperBound?)` → `"≤ ~2.0 h left"` when bounded; component `bigEta()`/`finishTime()` show `"Finishing up"` at the `snapshot` phase.

- [ ] **Step 1: Add `etaIsUpperBound` to the `JobSnapshot` interface**

In `src/Arius.Web/src/app/core/api/api-models.ts`, in the `JobSnapshot` interface after `throughputBytesPerSec` (line 99):

```typescript
  etaSeconds: number | null;
  throughputBytesPerSec: number;
  etaIsUpperBound: boolean;   // archive: true while hashing incomplete → the estimate is an upper bound ("≤")
  pct: number;
```

- [ ] **Step 2: Add `etaIsUpperBound: false` to the three test snapshot factories**

`src/Arius.Web/src/app/shared/job-format.spec.ts` — in the `snap()` factory literal (line ~7), add `etaIsUpperBound: false,` next to `throughputBytesPerSec: 0,`:

```typescript
    uploadedBytes: 0, dedupedBytes: 0, dedupedFiles: 0, etaSeconds: null, throughputBytesPerSec: 0, etaIsUpperBound: false,
```

`src/Arius.Web/src/app/core/state/job-pill.store.spec.ts` — same edit in its `snap()` factory (line ~11), add `etaIsUpperBound: false,` to the object literal.

`src/Arius.Web/src/app/core/api/realtime.service.spec.ts` — same edit in its `snapshot()` factory (line ~6), add `etaIsUpperBound: false,` to the object literal.

- [ ] **Step 3: Write the failing `formatEta` test**

In `src/Arius.Web/src/app/shared/job-format.spec.ts`, add:

```typescript
describe('formatEta upper bound', () => {
  it('prefixes ≤ when the estimate is an upper bound, and never for an unknown eta', () => {
    expect(formatEta(7200, true)).toBe('≤ ~2.0 h left');
    expect(formatEta(7200, false)).toBe('~2.0 h left');
    expect(formatEta(7200)).toBe('~2.0 h left');       // default is not-bounded
    expect(formatEta(null, true)).toBe('estimating…');  // unknown wins over the bound
  });
});
```

- [ ] **Step 4: Run the web tests to verify the new test fails**

Run: `npm --prefix src/Arius.Web run test`
Expected: FAIL — `formatEta(7200, true)` returns `"~2.0 h left"` (no `≤` prefix yet). Factory edits from Step 2 keep the rest compiling.

- [ ] **Step 5: Implement the `formatEta` upper-bound prefix**

In `src/Arius.Web/src/app/shared/job-format.ts`, replace `formatEta` (lines 4-10):

```typescript
/** "~12 min left" / "estimating…" (null until totalNewBytes is known). */
export function formatEta(seconds: number | null | undefined): string {
  if (seconds == null) return 'estimating…';
  if (seconds < 60) return `~${Math.max(1, Math.round(seconds))} sec left`;
  if (seconds < 3600) return `~${Math.round(seconds / 60)} min left`;
  return `~${(seconds / 3600).toFixed(1)} h left`;
}
```

with:

```typescript
/** "~12 min left" / "≤ ~2.0 h left" (bounded) / "estimating…" (null until known).
 *  `isUpperBound` (archive, pre-hash-complete) prefixes "≤ " to signal the estimate is provisional. */
export function formatEta(seconds: number | null | undefined, isUpperBound = false): string {
  if (seconds == null) return 'estimating…';
  const body =
    seconds < 60   ? `~${Math.max(1, Math.round(seconds))} sec left`
  : seconds < 3600 ? `~${Math.round(seconds / 60)} min left`
  :                  `~${(seconds / 3600).toFixed(1)} h left`;
  return isUpperBound ? `≤ ${body}` : body;
}
```

- [ ] **Step 6: Run the web tests to verify they pass**

Run: `npm --prefix src/Arius.Web run test`
Expected: PASS — the new `formatEta upper bound` test and all existing specs.

- [ ] **Step 7: Wire `bigEta` and `finishTime` in the detail component**

In `src/Arius.Web/src/app/features/jobs/job-detail.component.ts`, replace `finishTime` (lines 355-359):

```typescript
  protected readonly finishTime = computed(() => {
    const eta = this.snap()?.etaSeconds;
    if (eta == null) return 'estimating…';
    return new Date(Date.now() + eta * 1000).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
  });
```

with:

```typescript
  protected readonly finishTime = computed(() => {
    const s = this.snap();
    if (s?.phase === 'snapshot') return 'Finishing up';   // uploads drained; only metadata finalize remains
    const eta = s?.etaSeconds;
    if (eta == null) return 'estimating…';
    const clock = new Date(Date.now() + eta * 1000).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
    return s?.etaIsUpperBound ? `≤ ${clock}` : clock;
  });
```

Then replace `bigEta` (lines 368-371):

```typescript
  protected readonly bigEta = computed(() => {
    if (this.status() === 'rehydrating') return this.hydratedBy() || 'Waiting on Azure';
    return formatEta(this.snap()?.etaSeconds);
  });
```

with:

```typescript
  protected readonly bigEta = computed(() => {
    if (this.status() === 'rehydrating') return this.hydratedBy() || 'Waiting on Azure';
    if (this.snap()?.phase === 'snapshot') return 'Finishing up';
    return formatEta(this.snap()?.etaSeconds, this.snap()?.etaIsUpperBound ?? false);
  });
```

- [ ] **Step 8: Type-check / build the web app**

Run: `npm --prefix src/Arius.Web run build`
Expected: build succeeds (the `JobSnapshot` interface change is satisfied everywhere).

- [ ] **Step 9: Commit**

```bash
git add src/Arius.Web/src/app/core/api/api-models.ts \
        src/Arius.Web/src/app/shared/job-format.ts \
        src/Arius.Web/src/app/shared/job-format.spec.ts \
        src/Arius.Web/src/app/features/jobs/job-detail.component.ts \
        src/Arius.Web/src/app/core/api/realtime.service.spec.ts \
        src/Arius.Web/src/app/core/state/job-pill.store.spec.ts
git commit -m "feat(web): render '≤' upper-bound ETA and 'Finishing up' finalize state"
```

---

### Task 3: Full verification

**Files:** none (verification only).

- [ ] **Step 1: Run the whole API test project**

Run: `dotnet test src/Arius.Api.Tests`
Expected: PASS (ETA tests + all others).

- [ ] **Step 2: Run the whole web test suite**

Run: `npm --prefix src/Arius.Web run test`
Expected: PASS.

- [ ] **Step 3: Confirm the traceable behaviour is available**

Note for manual/live checking (no code): run an archive with `ARIUS_LOG_LEVEL=Debug` and watch the `[ETA]` lines — `xfer`/`hash` rates, `bound`, and `eta` should show the local (hash) term binding on a dedup-heavy run and the estimate steadying as the job runs. This is the real-run signal that the panels now behave; τ constants can be tuned from these traces if needed.

---

## Self-Review

**Spec coverage:**
- "Upload not always the bottleneck / hash-bound for deduped archive" → Task 1 local term + `Eta_is_hash_bound_when_there_is_nothing_to_upload`.
- "Estimating until scan complete" → Task 1 `total == 0 → null`.
- "Upper bound during dedup, tightening" → Task 1 `uploadDenom = max(totalNew, total−deduped)` + `EtaIsUpperBound`; Task 2 "≤" rendering.
- "Truly correct once hashing done" → `EtaIsUpperBound` flips false at `hashed >= total` (`Eta_is_upper_bound_until_hashing_completes`).
- "Adaptive window by job size" → Task 1 elapsed-scaled τ + `Adaptive_window_dampens_a_late_spike_more_than_an_early_one`.
- "Restore untouched" → restore single-term branch + `Restore_eta_uses_download_rate_and_is_not_an_upper_bound`.
- "≤ ~2 h wording OK" → Task 2 `formatEta` + test.
- "Finalize state" → Task 2 `phase === 'snapshot'` → "Finishing up".

**Placeholder scan:** none — every step has exact code/commands.

**Type consistency:** C# `EtaIsUpperBound` (bool, non-required) ⇄ JSON `etaIsUpperBound` ⇄ TS `etaIsUpperBound: boolean`. `Rates()` returns `(double transfer, double hash)`, consumed as `transferRate`/`hashRate`. `FoldRate(double, long, double, double) → double`. `formatEta(seconds, isUpperBound = false)` — the extra param is optional so existing single-arg callers are unaffected.
