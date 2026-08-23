# Split Throughput (Hash + Upload) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Report hashing and upload throughput as two independently-honest rates instead of one arbitrated scalar, so the UI never shows 0 B/s during the scan/hash window or a stale rate at completion.

**Architecture:** `JobSink.BuildSnapshot` computes each stream's liveness from state it already holds and zeroes the rate of an idle/finished stream. The snapshot gains two fields (`HashThroughputBytesPerSec`, `UploadThroughputBytesPerSec`); the existing `ThroughputBytesPerSec` is redefined as their max (the pill's "dominant" rate). ETA is untouched — prediction stays separate from observation. The archive detail page renders the two rates as two labeled rows; the pill and restore are unchanged in code.

**Tech Stack:** C# 13 / .NET 10, TUnit (Microsoft.Testing.Platform), Angular (signals, standalone components), Vitest.

**Design spec:** `docs/history/superpowers/2026-08-23-split-throughput-design.md`

## Global Constraints

- No new NuGet or npm packages (Central Package Management; nothing needed here).
- C# tests use TUnit: `[Test]` + `await Assert.That(x).IsEqualTo(...)` / `.IsBetween(lo, hi)` / `.IsNull()`.
- New DTO fields are additive and **non-`required`** (default `0`) so persisted `state_json` snapshots still deserialize.
- The `[ETA]` diagnostic log line must remain a faithful mirror of the emitted snapshot (every wire field appears in it).
- Run C# tests from `src/Arius.Api.Tests`: `dotnet run --project Arius.Api.Tests.csproj -- --treenode-filter "/*/*/<Class>/<Method>"`.
- Run web tests from `src/Arius.Web`: `npm run test` (vitest). Typecheck the app with `npm run build`.

---

### Task 1: Backend — two throughput fields + independent rate block in `BuildSnapshot`

**Files:**
- Modify: `src/Arius.Api/Jobs/JobSnapshot.cs` (add two fields after line 19)
- Modify: `src/Arius.Api/Jobs/JobSink.cs` (`BuildSnapshot`, the `eta`/`reportedRate` region ~377-429)
- Test: `src/Arius.Api.Tests/Jobs/JobSinkProgressFixesTests.cs`

**Interfaces:**
- Produces: `JobSnapshot.HashThroughputBytesPerSec : double`, `JobSnapshot.UploadThroughputBytesPerSec : double`; `ThroughputBytesPerSec` redefined = `max(hashTp, uploadTp)`.
- Consumes: existing `Rates()` → `(double transfer, double hash)`, and counters `_totalBytes/_hashedBytes/_uploadedBytes/_queuedNewBytes/_newByteTotal/_newByteTotalFinal/_bytesRestored/_restoreTotalBytes`.

- [ ] **Step 1: Add the two DTO fields**

In `src/Arius.Api/Jobs/JobSnapshot.cs`, immediately after the `ThroughputBytesPerSec` line (19), insert:

```csharp
    public          double  HashThroughputBytesPerSec   { get; init; }   // local hashing rate; 0 once hashing is done or before it starts
    public          double  UploadThroughputBytesPerSec { get; init; }   // upload rate (archive) / download rate (restore); 0 when that stream is idle or done
```

- [ ] **Step 2: Rewrite the completion test and extend the scan test; add the split + restore tests**

In `src/Arius.Api.Tests/Jobs/JobSinkProgressFixesTests.cs`, **replace** the whole `Throughput_at_upload_completion_is_transfer_rate_not_stale_hash_rate` method and the `Throughput_during_scan_reports_the_hash_rate_not_zero` method with the four methods below (keep everything else in the file):

```csharp
    // ── Throughput: two independently-honest streams, each zeroed when idle/done ────
    [Test]
    public async Task Throughput_during_scan_reports_the_hash_rate_not_zero()
    {
        // Scan still running (total == 0) while files are hashed concurrently. On a large, dedup-heavy
        // repo this window lasts many minutes with nothing uploaded yet — the hash stream must be shown.
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();               // no SetTotals → total == 0 (enumeration not complete)

        s.SampleForEta(t0);                   // baseline
        s.AddHashed(20_000_000);              // 20 MB hashed over 1 s = 20 MB/s hash rate
        s.SampleForEta(t0.AddSeconds(1));

        var snap = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(snap.TotalBytes).IsEqualTo(0L);                                    // still scanning
        await Assert.That(snap.EtaSeconds).IsNull();                                         // estimating, by design
        await Assert.That(snap.HashThroughputBytesPerSec).IsBetween(19_000_000, 21_000_000); // live hashing surfaced
        await Assert.That(snap.UploadThroughputBytesPerSec).IsEqualTo(0.0);                  // nothing uploaded yet
        await Assert.That(snap.ThroughputBytesPerSec).IsBetween(19_000_000, 21_000_000);     // dominant = hash
    }

    [Test]
    public async Task Throughput_reports_the_upload_rate_while_hashing_is_done_and_upload_runs()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 1, bytes: 1_000_000_000);

        s.SampleForEta(t0);
        s.AddHashed(1_000_000_000);            // hashing complete (hashed == total) with a big hash EMA
        s.SampleForEta(t0.AddSeconds(1));

        s.SetNewByteTotal(100_000_000);        // 100 MB new to upload
        s.AddUploaded(Chunk('5'), stored: 0, original: 10_000_000);   // 10 MB over 1 s = 10 MB/s, 90 MB remain
        s.SampleForEta(t0.AddSeconds(2));

        var snap = s.BuildSnapshot(t0.AddSeconds(2));
        await Assert.That(snap.HashThroughputBytesPerSec).IsEqualTo(0.0);                     // hashing done → not shown
        await Assert.That(snap.UploadThroughputBytesPerSec).IsBetween(9_000_000, 11_000_000); // ~10 MB/s upload
        await Assert.That(snap.ThroughputBytesPerSec).IsBetween(9_000_000, 11_000_000);       // dominant = upload
    }

    [Test]
    public async Task Throughput_is_zero_once_both_streams_are_done()
    {
        // A huge instantaneous hash jump inflates the hash EMA (which never decays). Once hashing has
        // caught up to the total AND the upload has drained, BOTH streams read "done" (rate 0) — never
        // the stale ~1 GB/s hash rate the old tie-break surfaced.
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetTotals(files: 1, bytes: 1_000_000_000);

        s.SampleForEta(t0);
        s.AddHashed(1_000_000_000);            // 1 GB "hashed" in 1 s → ~1 GB/s hash EMA; hashed == total (done)
        s.SampleForEta(t0.AddSeconds(1));

        s.SetNewByteTotal(10_000_000);
        s.AddUploaded(Chunk('4'), stored: 0, original: 10_000_000);   // all 10 MB uploaded (done)
        s.SampleForEta(t0.AddSeconds(2));

        var snap = s.BuildSnapshot(t0.AddSeconds(2));
        await Assert.That(snap.EtaSeconds).IsEqualTo(0L);                    // both terms drained
        await Assert.That(snap.HashThroughputBytesPerSec).IsEqualTo(0.0);   // hashing done, not ~1 GB/s
        await Assert.That(snap.UploadThroughputBytesPerSec).IsEqualTo(0.0); // upload done
        await Assert.That(snap.ThroughputBytesPerSec).IsEqualTo(0.0);
    }

    [Test]
    public async Task Restore_throughput_reports_the_download_rate_on_the_upload_stream()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();
        s.SetRestoreTotals(files: 5, bytes: 10_000_000);

        s.SampleForEta(t0);
        s.ReportRestoreStreamed("f", 1_000_000);   // 1 MB downloaded over 1 s = 1 MB/s
        s.SampleForEta(t0.AddSeconds(1));

        var snap = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(snap.HashThroughputBytesPerSec).IsEqualTo(0.0);                  // no hashing on restore
        await Assert.That(snap.UploadThroughputBytesPerSec).IsBetween(900_000, 1_100_000); // download rate
        await Assert.That(snap.ThroughputBytesPerSec).IsBetween(900_000, 1_100_000);
    }
```

- [ ] **Step 3: Run the throughput tests to verify they fail**

Run: `cd src/Arius.Api.Tests && dotnet run --project Arius.Api.Tests.csproj -- --treenode-filter "/*/*/JobSinkProgressFixesTests/*"`
Expected: FAIL — the two "done"/split cases fail because `BuildSnapshot` still uses `reportedRate` and leaves the new fields at 0 (e.g. `Throughput_reports_the_upload_rate…` gets `UploadThroughputBytesPerSec == 0`).

- [ ] **Step 4: Replace the `eta`/`reportedRate` region of `BuildSnapshot` with separate ETA + rate blocks**

In `src/Arius.Api/Jobs/JobSink.cs`, replace the block that currently begins at `long? eta;` and ends at the closing brace of the final `else` (the `etaIsProvisional = …;` line, ~377-407) with:

```csharp
        var newByteTotalFinal = _newByteTotalFinal;
        var uploadDenom       = newByteTotalFinal ? Interlocked.Read(ref _newByteTotal) : totalNew;

        // ── ETA (prediction): one number, binding on the slower constraint. Unchanged behaviour. ──
        long? eta;
        var etaIsProvisional = false;
        if (isRestore)
        {
            eta = restoreTotal > 0 && transferRate > 0
                ? (long)Math.Ceiling(Math.Max(0L, restoreTotal - restored) / transferRate)
                : null;
        }
        else if (total == 0)
        {
            eta = null;                        // scan not complete → estimating
        }
        else
        {
            long? uploadEta = transferRate > 0 ? (long)Math.Ceiling(Math.Max(0L, uploadDenom - uploaded) / transferRate) : null;
            long? hashEta   = hashRate     > 0 ? (long)Math.Ceiling(Math.Max(0L, total - hashed)         / hashRate)     : null;
            // Bind on the slower constraint (ties resolve to upload, so a finished job doesn't read a stale hash term).
            eta = (hashEta is { } h && (uploadEta is not { } u || h > u)) ? hashEta : uploadEta;
            etaIsProvisional = eta is not null && !newByteTotalFinal;
        }

        // ── Throughput (observation): two independently-honest rates, each zeroed when its stream is idle/done. ──
        // The rates come from the two EMAs; liveness only gates WHETHER an already-smoothed rate is shown, so a
        // finished stream can never surface a stale value and the scan window shows live hashing (not 0).
        double hashTp, uploadTp;
        if (isRestore)
        {
            hashTp   = 0;                                                                 // restore never hashes
            uploadTp = restoreTotal > 0 && restored < restoreTotal ? transferRate : 0;   // the download rides the transfer EMA
        }
        else
        {
            var hashLive   = total == 0 || hashed < total;
            var uploadLive = uploadDenom > 0 && uploaded < uploadDenom;
            hashTp   = hashLive   ? hashRate     : 0;
            uploadTp = uploadLive ? transferRate : 0;
        }
        var dominantTp = Math.Max(hashTp, uploadTp);
```

Then in the `return new JobSnapshot { … }` initializer, replace the line:

```csharp
            EtaSeconds = eta, ThroughputBytesPerSec = reportedRate, Pct = pct, EtaIsProvisional = etaIsProvisional,
```

with:

```csharp
            EtaSeconds = eta, ThroughputBytesPerSec = dominantTp,
            HashThroughputBytesPerSec = hashTp, UploadThroughputBytesPerSec = uploadTp,
            Pct = pct, EtaIsProvisional = etaIsProvisional,
```

(Removing every use of the old `reportedRate` local — it no longer exists. `Math` is already used in the file.)

- [ ] **Step 5: Run the throughput tests to verify they pass**

Run: `cd src/Arius.Api.Tests && dotnet run --project Arius.Api.Tests.csproj -- --treenode-filter "/*/*/JobSinkProgressFixesTests/*"`
Expected: PASS (all methods, including the four above).

- [ ] **Step 6: Run the ETA tests to confirm no regression**

Run: `cd src/Arius.Api.Tests && dotnet run --project Arius.Api.Tests.csproj --no-build -- --treenode-filter "/*/*/JobSinkEtaTests/*"`
Expected: PASS — `Eta_is_hash_bound_when_there_is_nothing_to_upload` and `Adaptive_window_dampens_a_late_spike_more_than_an_early_one` still read the correct `ThroughputBytesPerSec` because `dominantTp` = the hash/transfer rate in those cases.

- [ ] **Step 7: Commit**

```bash
git add src/Arius.Api/Jobs/JobSnapshot.cs src/Arius.Api/Jobs/JobSink.cs src/Arius.Api.Tests/Jobs/JobSinkProgressFixesTests.cs
git commit -m "feat(jobs): split throughput into hash + upload rates

BuildSnapshot now emits HashThroughputBytesPerSec and
UploadThroughputBytesPerSec, each zeroed when its stream is idle/done;
ThroughputBytesPerSec becomes their max (the dominant rate). ETA logic
unchanged. Supersedes the total==0 Math.Max stop-gap.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Backend — mirror both rates in the `[ETA]` diagnostic line

**Files:**
- Modify: `src/Arius.Api/Jobs/JobSink.cs` (`LogEtaDiagnostics`, ~131-138)
- Test: `src/Arius.Api.Tests/Jobs/JobSinkEtaTests.cs` (`Eta_diagnostics_line_mirrors_the_snapshot_wire_fields`)

**Interfaces:**
- Consumes: `JobSnapshot.HashThroughputBytesPerSec`, `JobSnapshot.UploadThroughputBytesPerSec` (from Task 1).

- [ ] **Step 1: Extend the mirror test to require the two new tokens**

In `src/Arius.Api.Tests/Jobs/JobSinkEtaTests.cs`, in `Eta_diagnostics_line_mirrors_the_snapshot_wire_fields`, change the token array's first line from:

```csharp
                     "phase=upload", "status=running", "pct=", "eta=", "provisional=", "tp=", "warnings=",
```

to:

```csharp
                     "phase=upload", "status=running", "pct=", "eta=", "provisional=", "tp=", "hashTp=", "upTp=", "warnings=",
```

- [ ] **Step 2: Run the mirror test to verify it fails**

Run: `cd src/Arius.Api.Tests && dotnet run --project Arius.Api.Tests.csproj -- --treenode-filter "/*/*/JobSinkEtaTests/Eta_diagnostics_line_mirrors_the_snapshot_wire_fields"`
Expected: FAIL — the message does not contain `hashTp=`.

- [ ] **Step 3: Add the two fields to the log template and argument list**

In `src/Arius.Api/Jobs/JobSink.cs` `LogEtaDiagnostics`, change the first template line from:

```csharp
            "[ETA] job={JobId} phase={Phase} status={Status} pct={Pct} eta={EtaSeconds}s provisional={EtaIsProvisional} tp={ThroughputBytesPerSec:F0}B/s warnings={WarningCount}"
```

to:

```csharp
            "[ETA] job={JobId} phase={Phase} status={Status} pct={Pct} eta={EtaSeconds}s provisional={EtaIsProvisional} tp={ThroughputBytesPerSec:F0}B/s hashTp={HashThroughputBytesPerSec:F0}B/s upTp={UploadThroughputBytesPerSec:F0}B/s warnings={WarningCount}"
```

and in the argument list change:

```csharp
            snap.JobId, snap.Phase, snap.Status, snap.Pct, snap.EtaSeconds, snap.EtaIsProvisional, snap.ThroughputBytesPerSec, snap.WarningCount,
```

to:

```csharp
            snap.JobId, snap.Phase, snap.Status, snap.Pct, snap.EtaSeconds, snap.EtaIsProvisional, snap.ThroughputBytesPerSec, snap.HashThroughputBytesPerSec, snap.UploadThroughputBytesPerSec, snap.WarningCount,
```

- [ ] **Step 4: Run the mirror test (and the whole Jobs namespace) to verify pass**

Run: `cd src/Arius.Api.Tests && dotnet run --project Arius.Api.Tests.csproj -- --treenode-filter "/*/Arius.Api.Tests.Jobs/*/*"`
Expected: PASS (all Jobs tests).

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Api/Jobs/JobSink.cs src/Arius.Api.Tests/Jobs/JobSinkEtaTests.cs
git commit -m "chore(jobs): mirror hash/upload throughput in the [ETA] diagnostic line

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: Web — DTO fields + `throughputRow` display helper

**Files:**
- Modify: `src/Arius.Web/src/app/core/api/api-models.ts` (`JobSnapshot` interface, after line 99)
- Modify: `src/Arius.Web/src/app/shared/job-format.ts` (add `throughputRow`)
- Test: `src/Arius.Web/src/app/shared/job-format.spec.ts`
- Modify (fixtures): `src/Arius.Web/src/app/shared/job-format.spec.ts`, `src/Arius.Web/src/app/core/state/job-pill.store.spec.ts`, `src/Arius.Web/src/app/core/api/realtime.service.spec.ts`

**Interfaces:**
- Produces: `throughputRow(rate: number | null | undefined, producedBytes: number | null | undefined): string` — the right-hand value for one row: the formatted rate while live, `'done'` once the stream has moved bytes but stopped, `'—'` before it starts.
- Consumes: `JobSnapshot.hashThroughputBytesPerSec`, `JobSnapshot.uploadThroughputBytesPerSec`.

- [ ] **Step 1: Add the two fields to the TS `JobSnapshot`**

In `src/Arius.Web/src/app/core/api/api-models.ts`, immediately after line 99 (`throughputBytesPerSec: number;`) insert:

```typescript
  hashThroughputBytesPerSec: number;     // local hashing rate; 0 once hashing is done or before it starts
  uploadThroughputBytesPerSec: number;   // upload (archive) / download (restore) rate; 0 when idle or done
```

- [ ] **Step 2: Add the fields to the three test fixtures**

In each of `job-format.spec.ts`, `job-pill.store.spec.ts` (both the `snap()` helper), and `realtime.service.spec.ts` (the `snapshot()` helper), add the two properties to the object literal next to `throughputBytesPerSec: 0,`:

```typescript
    hashThroughputBytesPerSec: 0, uploadThroughputBytesPerSec: 0,
```

- [ ] **Step 3: Write the failing `throughputRow` tests**

In `src/Arius.Web/src/app/shared/job-format.spec.ts`, import `throughputRow` by adding it to the existing import on line 2, then add this describe block after the `formatThroughput` block (after line 80):

```typescript
describe('throughputRow', () => {
  it('shows the formatted rate while the stream is live', () => {
    expect(throughputRow(2_400_000, 5_000_000)).toBe('2.4 MB/s');
  });
  it('reads "done" once the stream has moved bytes but its rate is zero', () => {
    expect(throughputRow(0, 5_000_000)).toBe('done');
  });
  it('reads an em dash before the stream has produced anything', () => {
    expect(throughputRow(0, 0)).toBe('—');
    expect(throughputRow(null, null)).toBe('—');
  });
});
```

- [ ] **Step 4: Run the web tests to verify the new ones fail**

Run: `cd src/Arius.Web && npm run test`
Expected: FAIL — `throughputRow is not a function` / not exported.

- [ ] **Step 5: Implement `throughputRow`**

In `src/Arius.Web/src/app/shared/job-format.ts`, immediately after the `formatThroughput` function (after line 30) add:

```typescript
/** One throughput row's value: the formatted rate while the stream is live, "done" once it has moved
 *  bytes but its rate has dropped to 0, and "—" before it has produced anything. */
export function throughputRow(rate: number | null | undefined, producedBytes: number | null | undefined): string {
  if ((rate ?? 0) > 0) return formatThroughput(rate);
  return (producedBytes ?? 0) > 0 ? 'done' : '—';
}
```

- [ ] **Step 6: Run the web tests to verify pass**

Run: `cd src/Arius.Web && npm run test`
Expected: PASS (all suites, including the three fixtures that now compile with the new fields).

- [ ] **Step 7: Commit**

```bash
git add src/Arius.Web/src/app/core/api/api-models.ts src/Arius.Web/src/app/shared/job-format.ts src/Arius.Web/src/app/shared/job-format.spec.ts src/Arius.Web/src/app/core/state/job-pill.store.spec.ts src/Arius.Web/src/app/core/api/realtime.service.spec.ts
git commit -m "feat(web): add split throughput fields and throughputRow helper

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Web — two-row Throughput tile on the archive detail page

**Files:**
- Modify: `src/Arius.Web/src/app/features/jobs/job-detail.component.ts` (import + tile markup ~166-170, helper exposure ~467)

**Interfaces:**
- Consumes: `throughputRow` (Task 3), `snap()?.hashThroughputBytesPerSec`, `snap()?.uploadThroughputBytesPerSec`, `snap()?.hashedBytes`, `snap()?.uploadedBytes`.

- [ ] **Step 1: Import and expose `throughputRow` in the component**

In `src/Arius.Web/src/app/features/jobs/job-detail.component.ts`, add `throughputRow` to the `job-format` import (line 10). Then on the helper-exposure line 467 (`protected formatThroughput = formatThroughput; …`) append:

```typescript
  protected throughputRow = throughputRow;
```

- [ ] **Step 2: Replace the single-value Throughput tile with two labeled rows**

Replace the tile body currently at lines 166-170:

```html
              <div style="background:#fafafb;border:1px solid #f0f0f2;border-radius:11px;padding:13px 15px">
                <div style="font-size:11px;color:#a1a1aa;text-transform:uppercase;letter-spacing:.03em">Throughput</div>
                <div style="font-size:19px;font-weight:700;color:#18181b;margin-top:3px">{{ formatThroughput(snap()?.throughputBytesPerSec) }}</div>
                <div style="font-size:11.5px;color:#a1a1aa;margin-top:1px">sustained</div>
              </div>
```

with:

```html
              <div style="background:#fafafb;border:1px solid #f0f0f2;border-radius:11px;padding:13px 15px">
                <div style="font-size:11px;color:#a1a1aa;text-transform:uppercase;letter-spacing:.03em">Throughput</div>
                <div style="display:flex;justify-content:space-between;align-items:baseline;margin-top:6px">
                  <span style="font-size:11.5px;color:#a1a1aa">Hashing</span>
                  <span style="font-size:15px;font-weight:700;color:#18181b">{{ throughputRow(snap()?.hashThroughputBytesPerSec, snap()?.hashedBytes) }}</span>
                </div>
                <div style="display:flex;justify-content:space-between;align-items:baseline;margin-top:3px">
                  <span style="font-size:11.5px;color:#a1a1aa">Upload</span>
                  <span style="font-size:15px;font-weight:700;color:#18181b">{{ throughputRow(snap()?.uploadThroughputBytesPerSec, snap()?.uploadedBytes) }}</span>
                </div>
              </div>
```

- [ ] **Step 3: Typecheck + run web tests**

Run: `cd src/Arius.Web && npm run build && npm run test`
Expected: build succeeds (template + `throughputRow` typecheck), all vitest suites PASS. (No component-template unit test — the display logic lives in `throughputRow`, covered in Task 3; this task is pure wiring, verified by the typechecking build.)

- [ ] **Step 4: Commit**

```bash
git add src/Arius.Web/src/app/features/jobs/job-detail.component.ts
git commit -m "feat(web): show hashing and upload rates as two rows on the detail page

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Full-suite verification

- [ ] **Step 1: Run the whole Api Jobs test namespace**

Run: `cd src/Arius.Api.Tests && dotnet run --project Arius.Api.Tests.csproj -- --treenode-filter "/*/Arius.Api.Tests.Jobs/*/*"`
Expected: PASS.

- [ ] **Step 2: Run the whole web suite + build**

Run: `cd src/Arius.Web && npm run test && npm run build`
Expected: PASS + successful build.

- [ ] **Step 3 (optional, recommended): drive the real app**

Invoke the `verify` (or `run`) skill to archive a small local folder and confirm the detail page shows "Hashing N MB/s" during scan and "Upload …" once new data uploads, with each row reading "done"/"—" appropriately.

---

## Notes for the implementer

- The `pill` (`job-pill.component.ts`) and `subEta` caption (`job-detail.component.ts:376-380`) intentionally keep reading `throughputBytesPerSec`; it is now the honest dominant rate, so they need no change.
- The restore detail grid has no Throughput tile (Restored · Ready to download · Rehydrating) and is intentionally left unchanged; restore only surfaces throughput via the pill's dominant rate (= its download rate).
- `HashedBytes`/`UploadedBytes` are already on the wire, so the web "done vs —" distinction needs no extra fields.
