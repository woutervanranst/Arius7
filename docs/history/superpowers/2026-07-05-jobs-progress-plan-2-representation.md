# Jobs progress — Plan 2: consistent byte/counter representation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make archive/restore progress denominators consistent and non-underflowing, fixing review findings #5, #6, #8, #9, #11 — server-side in `JobSink` + two new forwarders + the rehydration poller, client-side by extracting the bar-percent math into tested pure functions.

**Architecture:** Archive: every layer divides by `totalBytes`; the "new bytes to upload" figure (`TotalNewBytes`, and the pct/ETA denominator) is computed **additively** from a newly-forwarded `ChunkUploadingEvent` instead of the underflowing `totalBytes − dedupedBytes`. Restore: forward the already-emitted `ChunkResolutionCompleteEvent.TotalChunks` as the single hydration denominator (it was being dropped). Rehydration quiet-window: key `firstChunkSeen` off the `rehydrated` bucket (chunks newly ready via rehydration), not the absolute available count. Phase text: distinguish "still hashing" from "nothing new to upload" via `hashedBytes ≥ totalBytes`.

**Tech Stack:** .NET 10, Martin Othamar `Mediator`, TUnit + Shouldly (`Arius.Api.Tests`), the Plan-1 scripted-fake-Core harness (`Arius.Api.Integration.Tests`), Angular 19 standalone + signals.

## Global Constraints

- **No `Arius.Core` changes.** Both new forwarders consume events Core **already emits** (`ChunkUploadingEvent`, `ChunkResolutionCompleteEvent`) — the Api simply stops ignoring them.
- All archive byte quantities are **original/uncompressed** units (per `ChunkUploadedEvent.OriginalSize`'s documented intent). Do not mix in stored/compressed bytes.
- The layered bar's invariant (`shared/layered-bar/layered-bar.component.ts`): three fills, each a subset of the previous, **all as % of the same dataset**. Archive: all ÷ `totalBytes`. Restore: hydration layers ÷ `chunksTotal` (chunk-space), download layer ÷ `restoreTotalBytes` (byte-space) — restore is honestly two overlapping phases (rehydration is conditional; online-tier chunks download immediately), documented on the component.
- `dotnet test --treenode-filter` (plain `--filter` silently runs 0 tests under TUnit/MTP). Web build gate: `cd src/Arius.Web && npx ng build --configuration development` (0 errors).
- TUnit style: `[Test] public async Task` + `await Assert.That(x).IsEqualTo(y)` (see `src/Arius.Api.Tests/Jobs/JobSinkAggregateTests.cs`).

---

## File structure

**Server (`src/Arius.Api/`):**
- Modify `Jobs/JobSink.cs` — add `_queuedNewBytes` + `AddQueuedNew`; add `_chunksTotal`/`_chunkBytesTotal` + `SetChunkTotals`; rework `BuildSnapshot` archive arithmetic (additive newBytes) + restore snapshot fields; `WithLiveRehydrationCounts` carries the rehydrated-bucket count.
- Modify `Jobs/JobSnapshot.cs` — `TotalNewBytes` doc; add `ChunksTotal`, `ChunkBytesTotal`.
- Modify `Hubs/ArchiveForwarders.cs` — add `ChunkUploadingForwarder`.
- Modify `Hubs/RestoreForwarders.cs` — add `ChunkResolutionCompleteForwarder`.
- Modify `Jobs/PersistedJobState.cs` — `RestoreResumeState.RehydratedCount` (replaces `AvailableOrRehydratedCount`).
- Modify `Jobs/RehydrationPollingService.cs:42` — `firstChunkSeen = resume.RehydratedCount > 0`.

**Client (`src/Arius.Web/`):**
- Modify `core/api/api-models.ts` — add `chunksTotal`, `chunkBytesTotal` to `JobSnapshot`.
- Modify `shared/job-format.ts` — add pure `archiveBarLayers`/`restoreBarLayers`; fix `phaseSentence` (#11).
- Modify `features/jobs/job-detail.component.ts` + `features/jobs/jobs.component.ts` — replace inline bar computeds with the pure functions.
- Modify `shared/layered-bar/layered-bar.component.ts` — doc the restore two-phase relaxation.
- Test: `shared/job-format.spec.ts` (new).

**Tests (`src/Arius.Api.Tests/`, `src/Arius.Api.Integration.Tests/`):** extend `Jobs/JobSinkAggregateTests.cs`; new `Jobs/RepresentationTests.cs`; new integration `RepresentationScenarioTests.cs`.

---

## Task 1: Archive — additive `TotalNewBytes` + `ChunkUploadingForwarder` (fix #5)

**Files:**
- Modify: `src/Arius.Api/Jobs/JobSink.cs` (fields `:109`, mutators `:119-125`, `BuildSnapshot` `:166-216`)
- Modify: `src/Arius.Api/Hubs/ArchiveForwarders.cs` (add a forwarder)
- Test: `src/Arius.Api.Tests/Jobs/RepresentationTests.cs` (new)

**Interfaces:**
- Produces: `JobSink.AddQueuedNew(long originalSize)` (accumulates `_queuedNewBytes`); `JobSnapshot.TotalNewBytes` now = additive queued-new bytes (0 until the first chunk is queued); archive `Pct`/`EtaSeconds` denominator = `_queuedNewBytes`. `ChunkUploadingForwarder : INotificationHandler<ChunkUploadingEvent>`.
- Consumes: `ChunkUploadingEvent(ChunkHash ChunkHash, long Size)` (`Arius.Core.Features.ArchiveCommand`).

**Background:** today `BuildSnapshot` computes `totalNew = total > 0 ? Math.Max(0, total - deduped) : 0` (`JobSink.cs:172`). Pointer-only files emit `FileScannedEvent.FileSize == 0` (excluded from `_totalBytes`) but `FileDedupedEvent.OriginalSize` at full size (added to `_dedupedBytes`), so `deduped` can exceed `total` → `totalNew` clamps to 0 → archive pct/ETA stuck. Fix: derive "new bytes to upload" additively from `ChunkUploadingEvent` (fired per chunk queued for upload), which never involves pointer-only files.

- [ ] **Step 1: Verify `ChunkUploadingEvent.Size` is original/uncompressed units**

Run: `grep -n "ChunkUploadingEvent" src/Arius.Core/Features/ArchiveCommand/*.cs`
Read the publish site in `ArchiveCommandHandler.cs`. Confirm `Size` is the chunk's **uncompressed/original** size (parallel to `ChunkUploadedEvent.OriginalSize` and the `CreateUploadProgress(chunkHash, uncompressed size)` factory). If it is **stored/compressed** instead, STOP and report NEEDS_CONTEXT — the additive total must be in the same original units as `_uploadedBytes`.

- [ ] **Step 2: Write the failing test**

Create `src/Arius.Api.Tests/Jobs/RepresentationTests.cs`:

```csharp
using Arius.Api.Hubs;
using Arius.Api.Jobs;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.Hashes;

namespace Arius.Api.Tests.Jobs;

public class RepresentationTests
{
    [Test]
    public async Task Pointer_heavy_archive_does_not_underflow_TotalNewBytes()
    {
        // Steady state: 1000 pointer-only deduped files (scanned as 0 bytes, deduped at full size)
        // + one new 100 MB file that actually uploads.
        var s = new JobSink();
        s.SetTotals(files: 1001, bytes: 100_000_000);      // scan excludes pointer-only (0 bytes each)
        s.AddDeduped(original: 1_000_000_000);             // pointer-only dedup at full size (> total)
        s.AddQueuedNew(100_000_000);                       // one new chunk queued for upload
        s.AddUploaded(stored: 60_000_000, original: 100_000_000);

        var snap = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
        // Additive new-bytes, NOT total - deduped (which would be Max(0, 100M - 1000M) = 0):
        await Assert.That(snap.TotalNewBytes).IsEqualTo(100_000_000L);
        await Assert.That(snap.Pct).IsEqualTo(100);        // uploaded 100M of 100M new
    }

    [Test]
    public async Task TotalNewBytes_is_zero_until_a_chunk_is_queued()
    {
        var s = new JobSink();
        s.SetTotals(files: 5, bytes: 500);
        s.AddScanned(500);
        var snap = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
        await Assert.That(snap.TotalNewBytes).IsEqualTo(0L);   // routing not yet produced upload work
    }

    [Test]
    public async Task ChunkUploadingForwarder_accumulates_queued_new_bytes()
    {
        var s = new JobSink();
        await new ChunkUploadingForwarder(s).Handle(new ChunkUploadingEvent(ChunkHash.Parse(new string('a', 64)), 700), default);
        await new ChunkUploadingForwarder(s).Handle(new ChunkUploadingEvent(ChunkHash.Parse(new string('b', 64)), 300), default);
        var snap = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
        await Assert.That(snap.TotalNewBytes).IsEqualTo(1000L);
    }
}
```

- [ ] **Step 3: Run to verify RED**

Run: `dotnet test src/Arius.Api.Tests --treenode-filter "/*/*/RepresentationTests/*"`
Expected: FAIL — `AddQueuedNew`/`ChunkUploadingForwarder` don't exist and `TotalNewBytes` is still `total - deduped`.

- [ ] **Step 4: Add the field + mutator to `JobSink`**

In the fields block (`JobSink.cs:109`), add `_queuedNewBytes` to the archive line:

```csharp
    private long _totalFiles, _totalBytes, _scannedBytes, _hashedBytes, _uploadedBytes, _dedupedBytes, _dedupedFiles, _queuedNewBytes;
```

Add the mutator next to `AddUploaded` (after `JobSink.cs:122`):

```csharp
    /// <summary>Accumulates the original (uncompressed) size of each new chunk queued for upload — the
    /// additive "new bytes to upload" total. Independent of the deduped/total subtraction, so it never
    /// underflows for pointer-only-heavy repos. Final once routing completes; 0 until the first queue.</summary>
    public void AddQueuedNew(long originalSize) => Interlocked.Add(ref _queuedNewBytes, originalSize);
```

- [ ] **Step 5: Rework `BuildSnapshot` archive arithmetic**

Replace `JobSink.cs:169-172` (the `total`/`deduped`/`uploaded`/`totalNew` block) with:

```csharp
        var total    = Interlocked.Read(ref _totalBytes);
        var deduped  = Interlocked.Read(ref _dedupedBytes);
        var uploaded = Interlocked.Read(ref _uploadedBytes);
        var hashed   = Interlocked.Read(ref _hashedBytes);
        // "New bytes to upload" is the sum of queued new-chunk sizes (additive) — NOT total - deduped,
        // which underflows when pointer-only files (scanned as 0 bytes) are deduped at full size.
        var totalNew = Interlocked.Read(ref _queuedNewBytes);
        var rate     = RateBytesPerSec(now);
```

Then update the archive denominator/progress/pct. Replace `JobSink.cs:181-189` with:

```csharp
        var denominator       = isRestore ? restoreTotal : totalNew;
        var progress          = isRestore ? restored     : uploaded;

        long? eta = denominator > 0 && rate > 0 ? (long)Math.Ceiling(Math.Max(0L, denominator - progress) / rate) : null;
        var pct = denominator > 0
            ? (int)Math.Clamp(progress * 100 / denominator, 0, 100)
            : (isRestore && restoreTotalFiles > 0
                ? (int)Math.Clamp(Interlocked.Read(ref _filesRestored) * 100 / restoreTotalFiles, 0, 100)
                // Archive before any upload work is known: reflect scan/hash progress so the ring isn't stuck at 0.
                : (!isRestore && total > 0 ? (int)Math.Clamp(hashed * 100 / total, 0, 100) : 0));
```

(`_hashedBytes` is now read as `hashed` at the top; keep the existing `HashedBytes = Interlocked.Read(ref _hashedBytes)` line in the snapshot initializer or change it to `HashedBytes = hashed` — either is fine, just don't double-declare.)

- [ ] **Step 6: Add `ChunkUploadingForwarder`**

In `src/Arius.Api/Hubs/ArchiveForwarders.cs`, add after `ChunkUploadedForwarder` (`:65`):

```csharp
public sealed class ChunkUploadingForwarder(JobSink sink) : INotificationHandler<ChunkUploadingEvent>
{
    public ValueTask Handle(ChunkUploadingEvent n, CancellationToken ct)
    {
        sink.AddQueuedNew(n.Size);   // additive "new bytes to upload" total (upload-progress denominator)
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 7: Run to verify GREEN + no regression**

Run: `dotnet test src/Arius.Api.Tests --treenode-filter "/*/*/RepresentationTests/*"` → all 3 PASS.
Run: `dotnet test src/Arius.Api.Tests/Arius.Api.Tests.csproj` (full) → all green (note: `JobSinkAggregateTests.Byte_layers_and_dedup_accumulate_as_original_bytes` asserts `TotalNewBytes == 2000` from `total(3000) - deduped(1000)`; that test now needs `s.AddQueuedNew(2000)` added, OR update its expectation to `0`. Fix it in Task 2's step alongside the other aggregate-test touch-ups — for now, if it fails, add `s.AddQueuedNew(2000);` before `BuildSnapshot` in that test and keep the `== 2000` assertion, since 2000 IS the new-bytes-to-upload for that fixture).

Actually make that fix now: in `src/Arius.Api.Tests/Jobs/JobSinkAggregateTests.cs`, in `Byte_layers_and_dedup_accumulate_as_original_bytes`, add `s.AddQueuedNew(2000);` after the `s.AddUploaded(...)` line so `TotalNewBytes` stays `2000` under the additive model. In `Archive_forwarders_populate_byte_layers`, add `await new ChunkUploadingForwarder(s).Handle(new ChunkUploadingEvent(ChunkHash.Parse(new string('d', 64)), 2000), default);` and (if it asserts `TotalNewBytes`) keep it at `2000`.

- [ ] **Step 8: Commit**

```bash
git add src/Arius.Api/Jobs/JobSink.cs src/Arius.Api/Hubs/ArchiveForwarders.cs src/Arius.Api.Tests/Jobs/RepresentationTests.cs src/Arius.Api.Tests/Jobs/JobSinkAggregateTests.cs
git commit -m "fix(api): additive archive TotalNewBytes via ChunkUploadingEvent (no pointer-only underflow)"
```

---

## Task 2: Restore — forward `ChunkResolutionCompleteEvent.TotalChunks` (fix #8 server side)

**Files:**
- Modify: `src/Arius.Api/Jobs/JobSink.cs` (add fields + `SetChunkTotals` + snapshot fields)
- Modify: `src/Arius.Api/Jobs/JobSnapshot.cs` (add `ChunksTotal`, `ChunkBytesTotal`)
- Modify: `src/Arius.Api/Hubs/RestoreForwarders.cs` (add a forwarder)
- Test: `src/Arius.Api.Tests/Jobs/RepresentationTests.cs`

**Interfaces:**
- Produces: `JobSink.SetChunkTotals(int totalChunks, long totalChunkBytes)`; `JobSnapshot.ChunksTotal` (int), `JobSnapshot.ChunkBytesTotal` (long). `ChunkResolutionCompleteForwarder : INotificationHandler<ChunkResolutionCompleteEvent>`.
- Consumes: `ChunkResolutionCompleteEvent(int TotalChunks, int LargeCount, int TarCount, long TotalChunkBytes)` (`Arius.Core.Features.RestoreCommand`).

**Background:** the web reconstructs the chunk total as `chunksAvailable + chunksRehydrated + chunksPending`, **omitting `chunksNeedingRehydration`** → the hydration bar over-reports and the "Planned" legend undercounts. Core already emits the true `TotalChunks` on `ChunkResolutionCompleteEvent`, but no forwarder consumes it. Forward it.

- [ ] **Step 1: Write the failing test** (append to `RepresentationTests.cs`)

```csharp
    [Test]
    public async Task ChunkResolutionCompleteForwarder_sets_authoritative_chunk_total()
    {
        var s = new JobSink();
        await new ChunkResolutionCompleteForwarder(s).Handle(
            new Arius.Core.Features.RestoreCommand.ChunkResolutionCompleteEvent(TotalChunks: 427, LargeCount: 12, TarCount: 40, TotalChunkBytes: 2_760_000_000), default);
        await new RestoreForwardersProbe(s).SetRehydration(available: 145, rehydrated: 0, needs: 282, pending: 0);

        var snap = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
        await Assert.That(snap.ChunksTotal).IsEqualTo(427);            // authoritative, includes needs-rehydration
        await Assert.That(snap.ChunkBytesTotal).IsEqualTo(2_760_000_000L);
        // sanity: the four buckets sum to the authoritative total
        await Assert.That(snap.ChunksAvailable + snap.ChunksRehydrated + snap.ChunksNeedingRehydration + snap.ChunksPending)
            .IsEqualTo(snap.ChunksTotal);
    }
```

Delete the `RestoreForwardersProbe` line and instead call the real forwarder — replace the `SetRehydration` line with:

```csharp
        await new RehydrationStatusForwarder(s).Handle(
            new Arius.Core.Features.RestoreCommand.RehydrationStatusEvent(Available: 145, Rehydrated: 0, NeedsRehydration: 282, Pending: 0), default);
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test src/Arius.Api.Tests --treenode-filter "/*/*/RepresentationTests/*"`
Expected: FAIL — `ChunkResolutionCompleteForwarder`, `SetChunkTotals`, `ChunksTotal`, `ChunkBytesTotal` don't exist.

- [ ] **Step 3: Add fields + mutator to `JobSink`**

In the restore fields line (`JobSink.cs:110`), add totals:

```csharp
    private long _restoreTotalFiles, _restoreTotalBytes, _filesRestored, _bytesRestored, _chunkBytesTotal;
    private volatile int _rehydAvailable, _rehydRehydrated, _rehydNeeds, _rehydPending, _chunksTotal;
```

Add the mutator after `SetRestoreTotals` (`JobSink.cs:127`):

```csharp
    /// <summary>Records the authoritative distinct-chunk total (and their byte total) from
    /// ChunkResolutionCompleteEvent — the single denominator for the restore hydration bar, so it no longer
    /// has to be (wrongly) reconstructed from a subset of the rehydration buckets.</summary>
    public void SetChunkTotals(int totalChunks, long totalChunkBytes)
    { _chunksTotal = totalChunks; Interlocked.Exchange(ref _chunkBytesTotal, totalChunkBytes); }
```

- [ ] **Step 4: Add snapshot fields**

In `JobSnapshot.cs`, in the restore block (after `:30`), add:

```csharp
    public int  ChunksTotal                { get; init; }   // authoritative distinct-chunk total (ChunkResolutionCompleteEvent)
    public long ChunkBytesTotal            { get; init; }
```

In `BuildSnapshot` (after `JobSink.cs:214` `ChunksPending = _rehydPending,`), add:

```csharp
            ChunksTotal = _chunksTotal,
            ChunkBytesTotal = Interlocked.Read(ref _chunkBytesTotal),
```

Also update `Hubs/JobsHub.cs` `EmptySnapshot(...)` (the object initializer that sets all required + restore fields, ~`:110-118`) to include `ChunksTotal = 0, ChunkBytesTotal = 0,` so it still compiles (these are non-required `init` fields, so this is only needed if the analyzer/tests reference them — add for completeness/consistency with the other chunk fields listed there).

- [ ] **Step 5: Add the forwarder**

In `src/Arius.Api/Hubs/RestoreForwarders.cs`, add after `TreeTraversalCompleteForwarder` (`:26`):

```csharp
public sealed class ChunkResolutionCompleteForwarder(JobSink sink) : INotificationHandler<ChunkResolutionCompleteEvent>
{
    public ValueTask Handle(ChunkResolutionCompleteEvent n, CancellationToken ct)
    {
        sink.SetChunkTotals(n.TotalChunks, n.TotalChunkBytes);
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 6: Run GREEN + full suite**

Run: `dotnet test src/Arius.Api.Tests --treenode-filter "/*/*/RepresentationTests/*"` → PASS.
Run: `dotnet test src/Arius.Api.Tests/Arius.Api.Tests.csproj` → all green.

- [ ] **Step 7: Commit**

```bash
git add src/Arius.Api/Jobs/JobSink.cs src/Arius.Api/Jobs/JobSnapshot.cs src/Arius.Api/Hubs/RestoreForwarders.cs src/Arius.Api/Hubs/JobsHub.cs src/Arius.Api.Tests/Jobs/RepresentationTests.cs
git commit -m "fix(api): forward ChunkResolutionCompleteEvent.TotalChunks as the restore chunk denominator"
```

---

## Task 3: Rehydration quiet-window — `firstChunkSeen` = newly hydrated (fix #6)

**Files:**
- Modify: `src/Arius.Api/Jobs/PersistedJobState.cs` (`RestoreResumeState`)
- Modify: `src/Arius.Api/Jobs/JobSink.cs` (`WithLiveRehydrationCounts`)
- Modify: `src/Arius.Api/Jobs/RehydrationPollingService.cs:42`
- Modify: `src/Arius.Api/Jobs/JobRunner.cs` (the `ResumeParamsFor(...)`/resume construction sites that set `AvailableOrRehydratedCount`, if any)
- Test: `src/Arius.Api.Tests/Jobs/RehydrationScheduleTests.cs` (extend) + `RepresentationTests.cs`

**Interfaces:**
- Produces: `RestoreResumeState.RehydratedCount` (replaces `AvailableOrRehydratedCount`); `JobSink.WithLiveRehydrationCounts` now sets `RehydratedCount = _rehydRehydrated`.
- Consumes: `RehydrationSchedule.IsDue(..., bool firstChunkSeen)` (unchanged signature).

**Background:** `firstChunkSeen = resume.AvailableOrRehydratedCount > 0` uses `available + rehydrated`. A Standard restore that includes any online-tier chunk has `available > 0` from the start, so it trips the 15-min tight cadence for the whole ~15 h window. The intended signal is *"a chunk newly became ready via rehydration"* — that is exactly the `rehydrated` bucket (`RehydrationStatusEvent.Rehydrated` = was archive-tier, now rehydrated), which is 0 until rehydration produces a ready chunk.

- [ ] **Step 1: Write the failing test** (append to `RehydrationScheduleTests.cs`)

```csharp
    [Test]
    public async Task Standard_with_only_always_available_chunks_stays_quiet()
    {
        // available>0 but rehydrated==0 → NOT "first chunk seen" → quiet window still applies.
        var start = DateTimeOffset.UnixEpoch;
        var due = RehydrationSchedule.IsDue(
            now: start + TimeSpan.FromMinutes(20), startedAt: start, lastRunAt: start,
            priority: "Standard", firstChunkSeen: false);   // firstChunkSeen driven by rehydrated>0, which is false here
        await Assert.That(due).IsFalse();
    }
```

(This asserts the schedule already behaves correctly given `firstChunkSeen: false` — the fix is at the *caller* that computes `firstChunkSeen`. The behavioral fix is proven end-to-end in Task 4's integration test; this unit test documents the intended input.)

- [ ] **Step 2: Rename the resume field**

In `PersistedJobState.cs:30`, replace:

```csharp
    public          int                        AvailableOrRehydratedCount { get; init; }
```

with:

```csharp
    /// <summary>Count of chunks that have become ready via rehydration (the RehydrationStatusEvent.Rehydrated
    /// bucket). &gt; 0 means "a chunk newly hydrated" → tighten the poller cadence. NOT available+rehydrated:
    /// always-online chunks must not trip the quiet window.</summary>
    public          int                        RehydratedCount { get; init; }
```

- [ ] **Step 3: Update `WithLiveRehydrationCounts`** (`JobSink.cs:244-245`):

```csharp
    public RestoreResumeState WithLiveRehydrationCounts(RestoreResumeState resume) =>
        resume with { RehydratedCount = _rehydRehydrated };
```

- [ ] **Step 4: Update the poller** (`RehydrationPollingService.cs:42`):

```csharp
            var firstChunkSeen = resume.RehydratedCount > 0;
```

- [ ] **Step 5: Fix any other `AvailableOrRehydratedCount` references**

Run: `grep -rn "AvailableOrRehydratedCount" src/Arius.Api`
Expected after Steps 2-4: zero hits. If `JobRunner.cs` `ResumeParamsFor(...)` or a resume-construction site set `AvailableOrRehydratedCount`, update it to `RehydratedCount` (or drop it — it defaults to 0 and is set live by `WithLiveRehydrationCounts`). Confirm the solution builds.

- [ ] **Step 6: Run GREEN + full suite + build**

Run: `dotnet test src/Arius.Api.Tests --treenode-filter "/*/*/RehydrationScheduleTests/*"` → PASS.
Run: `dotnet build src/Arius.Api/Arius.Api.csproj` → clean.
Run: `dotnet test src/Arius.Api.Tests/Arius.Api.Tests.csproj` → all green.

- [ ] **Step 7: Commit**

```bash
git add src/Arius.Api/Jobs/PersistedJobState.cs src/Arius.Api/Jobs/JobSink.cs src/Arius.Api/Jobs/RehydrationPollingService.cs src/Arius.Api/Jobs/JobRunner.cs src/Arius.Api.Tests/Jobs/RehydrationScheduleTests.cs
git commit -m "fix(api): rehydration quiet-window keys off newly-rehydrated chunks, not always-available"
```

---

## Task 4: Harness integration test — representation end-to-end (proves #5, #8, #6)

**Files:**
- Create: `src/Arius.Api.Integration.Tests/RepresentationScenarioTests.cs`

**Interfaces:**
- Consumes: `AriusApiFactory` (Plan 1), `ScenarioRegistry`, `ArchiveScenario`/`RestoreScenario`, `JobRunner`, `AppDatabase.GetJob`, the job's persisted `state_json` snapshot.

- [ ] **Step 1: Write the test**

Create `src/Arius.Api.Integration.Tests/RepresentationScenarioTests.cs`:

```csharp
using System.Text.Json;
using Arius.Api.AppData;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Jobs;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests;

public class RepresentationScenarioTests
{
    [Test]
    public async Task Pointer_heavy_archive_reports_additive_new_bytes_not_underflow()
    {
        await using var factory = new AriusApiFactory();
        var srcDir = Path.Combine(Path.GetTempPath(), $"arius-itest-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(srcDir);
        var repoId = factory.SeedRepository(localPath: srcDir);

        factory.Scenarios.SetArchive(repoId, new ArchiveScenario(
            Events:
            [
                new ScanCompleteEvent(TotalFiles: 1001, TotalBytes: 100_000_000),           // pointer-only files scanned as 0
                new FileDedupedEvent(ContentHash.Parse(new string('b', 64)), OriginalSize: 1_000_000_000), // pointer-only dedup, full size
                new ChunkUploadingEvent(ChunkHash.Parse(new string('c', 64)), 100_000_000), // one new chunk queued
                new ChunkUploadedEvent(ChunkHash.Parse(new string('c', 64)), StoredSize: 60_000_000, OriginalSize: 100_000_000),
            ],
            Result: new ArchiveResult
            {
                Success = true, FilesScanned = 1001, EntriesExcluded = 0, FilesUploaded = 1, FilesDeduped = 1000,
                OriginalSize = 1_100_000_000, IncrementalSize = 100_000_000, IncrementalStoredSize = 60_000_000,
                FastHashReused = 0, FastHashRehashed = 1001, RootHash = null, SnapshotTime = DateTimeOffset.UnixEpoch,
            }));

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var jobId = Guid.NewGuid().ToString();
        await runner.RunArchiveAsync(repoId, jobId, "Archive", false, false, false);

        var db = factory.Services.GetRequiredService<AppDatabase>();
        var job = db.GetJob(jobId)!;
        await Assert.That(job.Status).IsEqualTo("completed");
        var snap = JsonSerializer.Deserialize<PersistedJobState>(job.StateJson!)!.Snapshot;
        await Assert.That(snap.TotalNewBytes).IsEqualTo(100_000_000L);   // additive, not Max(0, 100M - 1000M)=0
    }

    [Test]
    public async Task Restore_reports_authoritative_chunk_total_including_needs_rehydration()
    {
        await using var factory = new AriusApiFactory();
        var dest = Path.Combine(Path.GetTempPath(), $"arius-itest-dst-{Guid.NewGuid():N}");
        var repoId = factory.SeedRepository(localPath: dest);

        factory.Scenarios.SetRestore(repoId, new RestoreScenario(
            PreCostEvents:
            [
                new SnapshotResolvedEvent(DateTimeOffset.UnixEpoch, default),
                new TreeTraversalCompleteEvent(FileCount: 100, TotalOriginalSize: 3_000_000),
                new ChunkResolutionCompleteEvent(TotalChunks: 427, LargeCount: 12, TarCount: 40, TotalChunkBytes: 2_760_000_000),
                new RehydrationStatusEvent(Available: 145, Rehydrated: 0, NeedsRehydration: 282, Pending: 0),
            ],
            CostPrompt: null,   // nothing pending pre-cost in this snapshot shape; no prompt
            PostApproveEvents: [ new FileRestoredEvent(RelativePath.Parse("a"), 3_000_000) ],
            Result: new RestoreResult { Success = true, FilesRestored = 100, FilesSkipped = 0, ChunksPendingRehydration = 0, ErrorMessage = null }));

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var jobId = Guid.NewGuid().ToString();
        await runner.RunRestoreAsync(repoId, jobId, "test", null, [], false, false);

        var db = factory.Services.GetRequiredService<AppDatabase>();
        var job = db.GetJob(jobId)!;
        var snap = JsonSerializer.Deserialize<PersistedJobState>(job.StateJson!)!.Snapshot;
        await Assert.That(snap.ChunksTotal).IsEqualTo(427);   // includes the 282 needing rehydration
    }
}
```

> If a restore with `CostPrompt: null` and `NeedsRehydration > 0` doesn't persist a snapshot on the path it takes (e.g. it completes without parking), adjust the scenario so the run reaches a state that calls `SaveJobState` (the completed path does). If `RunRestoreAsync`'s completed branch persists the snapshot (it calls `SaveJobState` on completion — see `JobRunner.cs:267`), the assertion reads it there. Confirm which terminal/persist path this scenario hits and assert against `db.GetJob(jobId).Status` accordingly; if needed, read the live snapshot via `AttachToJob` instead.

- [ ] **Step 2: Run + full project suite**

Run: `dotnet test src/Arius.Api.Integration.Tests --treenode-filter "/*/*/RepresentationScenarioTests/*"` → 2 ran, PASS.
Run: `dotnet test --project src/Arius.Api.Integration.Tests/Arius.Api.Integration.Tests.csproj` → all green (Plan-1 tests + these).

- [ ] **Step 3: Commit**

```bash
git add src/Arius.Api.Integration.Tests/RepresentationScenarioTests.cs
git commit -m "test(api): integration coverage for additive archive new-bytes + authoritative restore chunk total"
```

---

## Task 5: Client — tested pure bar-layer functions + phase-text fix (#9, #8-web, #11)

**Files:**
- Modify: `src/Arius.Web/src/app/core/api/api-models.ts` (add `chunksTotal`, `chunkBytesTotal`)
- Modify: `src/Arius.Web/src/app/shared/job-format.ts` (add `archiveBarLayers`/`restoreBarLayers`; fix `phaseSentence`)
- Modify: `src/Arius.Web/src/app/shared/layered-bar/layered-bar.component.ts` (doc the restore relaxation)
- Test: `src/Arius.Web/src/app/shared/job-format.spec.ts` (new)

**Interfaces:**
- Produces: `archiveBarLayers(s: JobSnapshot): { scanned: number; middle: number; top: number }` — all ÷ `totalBytes`. `restoreBarLayers(s: JobSnapshot): { scanned: number; middle: number; top: number }` — `scanned: 100`, `middle: (chunksAvailable + chunksRehydrated) / chunksTotal * 100`, `top: bytesRestored / restoreTotalBytes * 100`. Fixed `phaseSentence`.
- Consumes: `JobSnapshot` (now with `chunksTotal`, `chunkBytesTotal`).

- [ ] **Step 1: Determine the web unit-test runner**

Run: `cd src/Arius.Web && cat package.json | grep -A1 '"test"'` and `ls karma.conf.js vitest.config.* 2>/dev/null`.
- If a runner exists (Karma/Jasmine `ng test`, or Vitest), use it and write the spec in that dialect.
- If **no** unit runner exists, report DONE_WITH_CONCERNS after implementing + `ng build`, and note that render/format verification for these pure functions falls to Plan 3's e2e (which drives the real Web against the scripted-fake Api). Do NOT add a whole test-runner toolchain in this task without confirming — flag it for a decision.

- [ ] **Step 2: Add the mirror fields** to `src/Arius.Web/src/app/core/api/api-models.ts` `JobSnapshot` (after `chunksPending`, `:109`):

```typescript
  chunksTotal: number;
  chunkBytesTotal: number;
```

- [ ] **Step 3: Add the pure functions + fix `phaseSentence`** in `src/Arius.Web/src/app/shared/job-format.ts`

Replace `phaseSentence` (`:51-59`) and add the two layer functions:

```typescript
/** One phase sentence for the pill / overview row / detail header. */
export function phaseSentence(s: JobSnapshot, kind: string): string {
  const gb = (n: number) => (n / 1e9).toFixed(2) + ' GB';
  if (kind === 'restore') {
    if (s.chunksNeedingRehydration > 0 && s.bytesRestored === 0) return `Rehydrating — ${s.chunksNeedingRehydration} chunks from Archive tier`;
    return `Restoring — ${s.filesRestored} of ${s.restoreTotalFiles} files`;
  }
  // Still scanning/hashing (routing hasn't produced the new-bytes total yet) → estimating.
  // Once hashing is done (hashedBytes ≥ totalBytes), the new-bytes total is known — even if it's 0.
  if (s.totalBytes === 0 || s.hashedBytes < s.totalBytes) return 'Scanning & hashing — estimating…';
  if (s.totalNewBytes === 0) return 'No new data — finalizing snapshot';
  return `Uploading — ${gb(s.uploadedBytes)} of ${gb(s.totalNewBytes)}`;
}

/** Archive layered-bar percentages — all three layers over the SAME dataset (totalBytes), so the bar
 *  never has a layer overtake the one below it. The Uploaded layer converges to just under 100%; the
 *  remaining gap is the deduplicated bytes (which were never uploaded). */
export function archiveBarLayers(s: JobSnapshot): { scanned: number; middle: number; top: number } {
  const d = s.totalBytes;
  return d > 0
    ? { scanned: s.scannedBytes * 100 / d, middle: s.hashedBytes * 100 / d, top: s.uploadedBytes * 100 / d }
    : { scanned: 0, middle: 0, top: 0 };
}

/** Restore layered-bar percentages. Two overlapping phases: hydration (chunk-space, over the authoritative
 *  chunksTotal INCLUDING chunks still needing rehydration) and download-to-disk (byte-space). Online-tier
 *  chunks download immediately, so these layers overlap rather than gate. */
export function restoreBarLayers(s: JobSnapshot): { scanned: number; middle: number; top: number } {
  return {
    scanned: 100,
    middle: s.chunksTotal > 0 ? (s.chunksAvailable + s.chunksRehydrated) * 100 / s.chunksTotal : 0,
    top: s.restoreTotalBytes > 0 ? s.bytesRestored * 100 / s.restoreTotalBytes : 0,
  };
}
```

- [ ] **Step 4: Document the restore relaxation** on `layered-bar.component.ts` (extend the class JSDoc `:3-7`):

```typescript
/**
 * Byte-weighted layered progress bar (design README §Screens 2). ONE track, three overlapping fills.
 * Archive: all three fills are % of the same dataset (totalBytes), each a subset of the previous
 * (scanned ⊇ hashed ⊇ uploaded) — so it never jumps or hangs. Restore is two overlapping phases:
 * the hydration fill is chunk-space (over chunksTotal) and the restored fill is byte-space
 * (over restoreTotalBytes); each is independently monotonic 0→100. Archive palette blues, restore purples.
 */
```

- [ ] **Step 5: Write the unit spec** (`src/Arius.Web/src/app/shared/job-format.spec.ts`) — Jasmine dialect (adapt to Vitest if that's the runner per Step 1):

```typescript
import { archiveBarLayers, restoreBarLayers, phaseSentence } from './job-format';
import { JobSnapshot } from '../core/api/api-models';

function snap(p: Partial<JobSnapshot>): JobSnapshot {
  return {
    jobId: 'j', phase: 'x', totalBytes: 0, totalNewBytes: 0, scannedBytes: 0, hashedBytes: 0,
    uploadedBytes: 0, dedupedBytes: 0, dedupedFiles: 0, etaSeconds: null, throughputBytesPerSec: 0,
    pct: 0, warningCount: 0, stats: {}, restoreTotalFiles: 0, filesRestored: 0, restoreTotalBytes: 0,
    bytesRestored: 0, chunksAvailable: 0, chunksRehydrated: 0, chunksNeedingRehydration: 0,
    chunksPending: 0, chunksTotal: 0, chunkBytesTotal: 0, ...p,
  };
}

describe('archiveBarLayers', () => {
  it('divides all three layers by totalBytes so uploaded never overtakes hashed', () => {
    const l = archiveBarLayers(snap({ totalBytes: 1000, scannedBytes: 1000, hashedBytes: 1000, uploadedBytes: 400, totalNewBytes: 500 }));
    expect(l.scanned).toBe(100);
    expect(l.middle).toBe(100);
    expect(l.top).toBe(40);          // 400/1000, NOT 400/500=80 (the old inconsistent denominator)
    expect(l.top).toBeLessThanOrEqual(l.middle);
  });
});

describe('restoreBarLayers', () => {
  it('uses the authoritative chunksTotal (including needs-rehydration) as the hydration denominator', () => {
    const l = restoreBarLayers(snap({ chunksTotal: 427, chunksAvailable: 145, chunksRehydrated: 0, chunksNeedingRehydration: 282, chunksPending: 0, restoreTotalBytes: 1000, bytesRestored: 250 }));
    expect(Math.round(l.middle)).toBe(34);   // 145/427, NOT 145/145=100 (old subset-sum omitted needs-rehydration)
    expect(l.top).toBe(25);
  });
});

describe('phaseSentence', () => {
  it('says estimating while still hashing', () => {
    expect(phaseSentence(snap({ totalBytes: 1000, hashedBytes: 400 }), 'archive')).toContain('estimating');
  });
  it('says "no new data" for a fully-deduped archive once hashing is done', () => {
    expect(phaseSentence(snap({ totalBytes: 1000, hashedBytes: 1000, totalNewBytes: 0 }), 'archive')).toContain('No new data');
  });
  it('shows the upload sentence when there is new data', () => {
    expect(phaseSentence(snap({ totalBytes: 1000, hashedBytes: 1000, totalNewBytes: 3_110_000_000, uploadedBytes: 1_680_000_000 }), 'archive')).toContain('Uploading');
  });
});
```

- [ ] **Step 6: Run the web tests + build**

Run the web unit runner from Step 1 (e.g. `cd src/Arius.Web && npx ng test --watch=false --browsers=ChromeHeadless` or `npx vitest run`) → the `job-format` specs PASS.
Run: `cd src/Arius.Web && npx ng build --configuration development` → 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/Arius.Web/src/app/core/api/api-models.ts src/Arius.Web/src/app/shared/job-format.ts src/Arius.Web/src/app/shared/layered-bar/layered-bar.component.ts src/Arius.Web/src/app/shared/job-format.spec.ts
git commit -m "fix(web): consistent bar denominators + phase text via tested pure layer functions"
```

---

## Task 6: Client — wire the pure functions into the components (#9, #8-web)

**Files:**
- Modify: `src/Arius.Web/src/app/features/jobs/job-detail.component.ts` (`:313-325`, stage computeds `:396-404`)
- Modify: `src/Arius.Web/src/app/features/jobs/jobs.component.ts` (`activeRows` `:191-206`)

**Interfaces:**
- Consumes: `archiveBarLayers`, `restoreBarLayers` (Task 5).

**Background:** the inconsistent denominators (#9) and the subset-sum chunk total (#8) are currently duplicated inline in BOTH components. Replace both with the shared pure functions so the fix lives in one tested place.

- [ ] **Step 1: Replace `job-detail.component.ts` bar computeds** (`:313-325`)

```typescript
  private layers = computed(() => { const s = this.snap(); if (!s) return { scanned: this.kind() === 'restore' ? 100 : 0, middle: 0, top: 0 };
    return this.kind() === 'restore' ? restoreBarLayers(s) : archiveBarLayers(s); });
  protected readonly scannedPct = computed(() => this.layers().scanned);
  protected readonly middlePct  = computed(() => this.layers().middle);
  protected readonly topPct     = computed(() => this.layers().top);

  // legend / tile derivations — planned uses the authoritative chunk total (includes needs-rehydration)
  protected readonly plannedChunks = computed(() => this.snap()?.chunksTotal ?? 0);
  protected readonly readyChunks   = computed(() => { const s = this.snap(); return s ? s.chunksAvailable + s.chunksRehydrated : 0; });
```

Add the import at the top of the file: `import { archiveBarLayers, restoreBarLayers } from '../../shared/job-format';` (merge with the existing `job-format` import if one is present).

Update the stage computeds that used `totalNewBytes` as an "upload done" gauge to keep working: `uploadDone` (`:375`) stays `s.totalNewBytes > 0 && s.uploadedBytes >= s.totalNewBytes` (correct under the additive model). `rehydrateDone` (`:399`) stays `total > 0 && s.chunksPending === 0` where `total = this.plannedChunks()` — now `chunksTotal` (`:396`). No change needed beyond `plannedChunks` now reading `chunksTotal`.

- [ ] **Step 2: Replace `jobs.component.ts` `activeRows` bar math** (`:194-202`)

```typescript
    const layers = s ? (kind === 'restore' ? restoreBarLayers(s) : archiveBarLayers(s))
                     : { scanned: kind === 'restore' ? 100 : 0, middle: 0, top: 0 };
    const { scanned, middle, top } = layers;
```

Add the import: `import { archiveBarLayers, restoreBarLayers } from '../../shared/job-format';` (merge with the existing `job-format` import).

- [ ] **Step 3: Build**

Run: `cd src/Arius.Web && npx ng build --configuration development` → 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Arius.Web/src/app/features/jobs/job-detail.component.ts src/Arius.Web/src/app/features/jobs/jobs.component.ts
git commit -m "refactor(web): both job views use the shared bar-layer functions (fixes #8/#9 in one place)"
```

---

## Self-review

**Spec coverage (design §3 + finding matrix D):**
- #5 (archive underflow) → Task 1 (additive `TotalNewBytes` via `ChunkUploadingForwarder`; pct/ETA denominator) + Task 4 integration. ✓
- #9 (archive inconsistent denominators) → Task 5 `archiveBarLayers` (all ÷ totalBytes) + Task 6 wiring. ✓
- #8 (restore omits needs-rehydration) → Task 2 (forward `TotalChunks`) + Task 5 `restoreBarLayers` (÷ chunksTotal) + Task 6 + Task 4 integration. ✓
- #6 (quiet-window) → Task 3 (`RehydratedCount`/`firstChunkSeen`). ✓
- #11 (phase text) → Task 5 `phaseSentence` (hashedBytes ≥ totalBytes signal). ✓
- Two new forwarders (both consume already-emitted events) → Tasks 1, 2. ✓
- `readyChunks`/available+rehydrated consolidation: `readyChunks` remains one web computed (Task 5/6); `RehydratedCount` replaces the mislabeled `AvailableOrRehydratedCount` (Task 3). The `CostEstimateDto.ChunksAvailable = available + alreadyRehydrated` display merge (`JobRunner.cs:199`) is left as-is (display-only) — noted as a deferred cosmetic, not chased here (YAGNI).

**Placeholder scan:** no TBD/TODO. The `>` notes are verification/adaptation instructions (verify `ChunkUploadingEvent.Size` units; confirm the restore-scenario persist path; determine the web test runner) — each names the exact command and the decision. Task 5 Step 1 has an explicit DONE_WITH_CONCERNS branch if no web unit runner exists (a real decision point, not a silent skip).

**Type consistency:** `AddQueuedNew(long)`/`_queuedNewBytes`, `SetChunkTotals(int,long)`/`_chunksTotal`/`_chunkBytesTotal`, snapshot `ChunksTotal`/`ChunkBytesTotal`, `RestoreResumeState.RehydratedCount`, and the TS `chunksTotal`/`chunkBytesTotal` + `archiveBarLayers`/`restoreBarLayers`/`phaseSentence` signatures are used identically across the tasks that produce and consume them. `ChunkUploadingEvent(ChunkHash, long Size)` and `ChunkResolutionCompleteEvent(int TotalChunks, int, int, long TotalChunkBytes)` match the real Core records (`Events.cs`).

**Carry-in from Plan 1 deferrals addressed:** none required by Plan 2. Carried forward to Plan 3: `ScriptedRestoreHandler` distinct declined-result shape; `ArchiveHubTests` name; `CanonicalScenarios` trace-vs-aggregates; the `CostEstimateDto` display-merge cosmetic.
