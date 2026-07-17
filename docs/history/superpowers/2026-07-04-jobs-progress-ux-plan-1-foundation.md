# Jobs/Progress UX — Plan 1: Core events + Api job-state foundation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the byte-honest, persisted, guarded job-state engine that the rest of the jobs/progress UX rework sits on — with zero UI dependencies.

**Architecture:** Two additive Core events give byte accuracy. A per-job `JobSink` (already injected per job-provider) becomes the live `JobState` — byte-weighted layered aggregates, per-stage timings, and a sliding-window ETA. A `JobStateRegistry` singleton maps `jobId → JobSink` for later hub/endpoint/poller access. Progress is emitted **coalesced (~1 s, absolute-state)** instead of per-event. State is persisted to a new `StateJson`/`Outcome` column pair, and a **single-active-job-per-repository** invariant is enforced by a partial unique index.

**Tech Stack:** .NET / C#, Mediator (INotification), SignalR (`IHubContext<JobsHub>`), Microsoft.Data.Sqlite, TUnit.

**Design spec:** `docs/history/superpowers/2026-07-04-jobs-progress-ux-design.md` (§4, §6, §10, §11, §12, and the aggregate/ETA parts of §10 progress).

## Global Constraints

- **Host→features boundary:** Api code must not resolve or call Core `Shared` services (`IChunkStorageService`, `ISnapshotService`, …); go through `IMediator`. (AGENTS.md, Architecture.)
- **Net Core footprint for the whole feature is exactly two additive events:** `ChunkUploadedEvent.OriginalSize` and `FileDedupedEvent`. Do not add other Core changes.
- **New-event docstrings match the `FileSkippedEvent`/`EntryExcludedEvent` house style** (rich `<summary>`, `<see cref>` cross-refs, pipeline position + consumer usage).
- **Progress is absolute-state (latest-wins).** During Plan 1 the payload keeps the legacy `pct` + `stats` fields (a superset) so the existing drawer/console keep working; Plan 3 switches the UI to the byte fields and drops the legacy ones. **Do not remove the console or `Log`/`Cost`/`Done` in Plan 1.**
- **Non-terminal statuses are exactly** `running`, `awaiting-cost`, `rehydrating`. Terminal: `completed`, `failed`, `cancelled`, `interrupted`. `queued` and `scheduled`-as-a-job-status are removed. (`awaiting-cost`/`rehydrating` are produced in Plan 2, but the guard/index enumerate them now.)
- **Tests:** TUnit. Run a class with `dotnet test --project <csproj> --treenode-filter "/*/*/<ClassName>/*"`. Use `FakeLogger<T>` (not `NullLogger<T>`). Non-test classes `internal`; one top-level class per file; mirror source structure; reusable doubles in `Fakes/`.
- **Domain vocabulary:** chunk, content hash, chunk hash, dedup, tar bundle, snapshot, rehydration, byte-weighted.

---

## File structure

**Core (modify):**
- `src/Arius.Core/Features/ArchiveCommand/Events.cs` — extend `ChunkUploadedEvent`; add `FileDedupedEvent`.
- `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs` — populate `OriginalSize` (line 565); publish `FileDedupedEvent` at the two dedup-hit branches (lines 481–483, 490–493).

**Api (create):**
- `src/Arius.Api/Jobs/JobStateRegistry.cs` — singleton `jobId → JobSink` map.
- `src/Arius.Api/Jobs/JobSnapshot.cs` — the absolute-state payload record + `JobOutcome` record + `EtaWindow`.

**Api (modify):**
- `src/Arius.Api/Jobs/JobSink.cs` — becomes the live `JobState` (byte aggregates, stage timings, ETA window, coalesced emit, snapshot/outcome builders).
- `src/Arius.Api/Hubs/ArchiveForwarders.cs`, `RestoreForwarders.cs` — mutate byte aggregates; new `FileScanned`/`FileHashing`/`FileDeduped` forwarders.
- `src/Arius.Api/Jobs/JobRunner.cs` — register/unregister with the registry; start/stop emit; guard pre-check; branch nothing new yet (rehydration branch is Plan 2).
- `src/Arius.Api/Jobs/SchedulerService.cs` — skip when the repo has an active job.
- `src/Arius.Api/Hubs/JobsHub.cs` — reject `StartArchive`/`StartRestore` when the repo is busy.
- `src/Arius.Api/AppData/AppDatabase.cs` — `state_json`/`outcome` columns + migration + partial unique index; `HasActiveJob`, `SaveJobState`, `SetJobOutcome`, `ReconcileInterruptedJobs`; extend `InsertJob`/`ListJobs`/`ReadJob`.
- `src/Arius.Api/AppData/Records.cs` — `JobRecord` gains `StateJson`/`Outcome`.
- `src/Arius.Api/Contracts/Dtos.cs` — `JobDto` gains `Outcome`.
- `src/Arius.Api/Program.cs` — register `JobStateRegistry`; call `ReconcileInterruptedJobs()` at startup.

**Tests (create):**
- `src/Arius.Integration.Tests/Pipeline/ArchiveEventsTests.cs`
- `src/Arius.Api.Tests/Jobs/JobSinkAggregateTests.cs`, `JobSinkEtaTests.cs`, `JobStateRegistryTests.cs`
- `src/Arius.Api.Tests/AppData/JobPersistenceTests.cs`, `JobGuardTests.cs`

---

## Task 1: Core — two additive events + publish sites

**Files:**
- Modify: `src/Arius.Core/Features/ArchiveCommand/Events.cs`
- Modify: `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs:565`, `:481-483`, `:490-493`
- Test: `src/Arius.Integration.Tests/Pipeline/ArchiveEventsTests.cs`

**Interfaces:**
- Produces: `ChunkUploadedEvent(ChunkHash ChunkHash, long StoredSize, long OriginalSize)`; `FileDedupedEvent(ContentHash ContentHash, long OriginalSize)`.

- [ ] **Step 1: Write the failing test**

Follow the existing `Arius.Integration.Tests/Pipeline/` fixtures (`PipelineFixture`, and the collecting patterns in `RestoreCostModelTests`/`RoundtripTests`). Archive a dataset containing a duplicate file (two files with identical content) while collecting published notifications.

```csharp
// src/Arius.Integration.Tests/Pipeline/ArchiveEventsTests.cs
using Arius.Core.Features.ArchiveCommand;

namespace Arius.Integration.Tests.Pipeline;

public class ArchiveEventsTests
{
    [Test]
    public async Task ChunkUploaded_carries_original_size_and_dedup_fires_for_duplicate_content()
    {
        await using var fixture = await PipelineFixture.CreateAsync();

        // Two files, identical content of a known size → one upload, one dedup.
        var content = fixture.RandomBytes(4096);
        fixture.WriteFile("a.bin", content);
        fixture.WriteFile("b.bin", content);

        var events = fixture.CollectNotifications();   // see fixture: subscribes a collecting INotificationHandler
        await fixture.ArchiveAsync();

        var uploaded = events.OfType<ChunkUploadedEvent>().Single();
        await Assert.That(uploaded.OriginalSize).IsEqualTo(4096L);

        var deduped = events.OfType<FileDedupedEvent>().Single();
        await Assert.That(deduped.OriginalSize).IsEqualTo(4096L);
    }
}
```

If `PipelineFixture` has no notification-collection helper, add one (a test `INotificationHandler<INotification>` registered into the fixture's provider that appends to a thread-safe list) in `Arius.Integration.Tests/Pipeline/Fakes/`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project src/Arius.Integration.Tests --treenode-filter "/*/*/ArchiveEventsTests/*"`
Expected: FAIL — `ChunkUploadedEvent` has no `OriginalSize`; `FileDedupedEvent` does not exist.

- [ ] **Step 3: Add/extend the events**

In `Events.cs`, replace the `ChunkUploadedEvent` record and add `FileDedupedEvent` right after it:

```csharp
/// <summary>A chunk upload completed.</summary>
/// <param name="ChunkHash">Content hash of the uploaded chunk.</param>
/// <param name="StoredSize">Bytes written to storage (compressed + encrypted); the upload denominator.</param>
/// <param name="OriginalSize">
/// Uncompressed size in bytes of the chunk's source content. Lets a byte-progress consumer express the
/// uploaded layer in the same original-dataset units as the scanned/hashed layers (stored size would
/// otherwise understate progress because of compression). For a large chunk this is the file's original
/// size; tar bundles carry their own uncompressed total on <see cref="TarBundleSealingEvent"/>.
/// </param>
public sealed record ChunkUploadedEvent(ChunkHash ChunkHash, long StoredSize, long OriginalSize) : INotification;

/// <summary>
/// A hashed file's content was found already stored — a hit in the chunk index or the in-run in-flight-hashes
/// set at the Dedup + Router stage — so it is <i>not</i> re-uploaded and contributes only a filetree entry.
/// It had a prior <see cref="FileScannedEvent"/> and <see cref="FileHashedEvent"/>; consumers tally it as
/// deduplicated (bytes not re-uploaded). Contrast <see cref="ChunkUploadedEvent"/>, which fires for content
/// that <i>is</i> uploaded. The handler folds these into <c>ArchiveResult.FilesDeduped</c>.
/// </summary>
/// <param name="ContentHash">Content hash of the deduplicated file (already present in the repository).</param>
/// <param name="OriginalSize">Uncompressed size in bytes of the file whose content was not re-uploaded.</param>
public sealed record FileDedupedEvent(ContentHash ContentHash, long OriginalSize) : INotification;
```

- [ ] **Step 4: Populate `OriginalSize` at the large-chunk upload site**

`ArchiveCommandHandler.cs:565` — `originalSize` is already in scope (line 552):

```csharp
await _mediator.Publish(new ChunkUploadedEvent(largeChunkHash, storedSize, originalSize), ct);
```

- [ ] **Step 5: Publish `FileDedupedEvent` at the two dedup-hit branches**

Pointer-only hit (`ArchiveCommandHandler.cs`, after `Interlocked.Add(ref originalSize, pointerSize);` ~line 483):

```csharp
await _mediator.Publish(new FileDedupedEvent(hashed.ContentHash, pointerSize), cancellationToken);
```

Regular hit (after `Interlocked.Add(ref originalSize, fs.GetFileSize(hashed.FilePair.RelativePath));` ~line 493) — capture the size once to avoid a second `GetFileSize`:

```csharp
var dedupedSize = fs.GetFileSize(hashed.FilePair.RelativePath);
Interlocked.Add(ref originalSize, dedupedSize);
await _mediator.Publish(new FileDedupedEvent(hashed.ContentHash, dedupedSize), cancellationToken);
```

- [ ] **Step 6: Run the test — expect PASS**

Run: `dotnet test --project src/Arius.Integration.Tests --treenode-filter "/*/*/ArchiveEventsTests/*"`
Expected: PASS. Then run the Core suite to confirm no forwarder/consumer broke: `dotnet test --project src/Arius.Core.Tests`.

- [ ] **Step 7: Commit**

```bash
git add src/Arius.Core/Features/ArchiveCommand/Events.cs src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs src/Arius.Integration.Tests/Pipeline/
git commit -m "feat(core): add FileDedupedEvent and ChunkUploadedEvent.OriginalSize for byte-accurate progress"
```

---

## Task 2: `JobSink` → live `JobState` (byte aggregates + ETA window)

**Files:**
- Modify: `src/Arius.Api/Jobs/JobSink.cs`
- Create: `src/Arius.Api/Jobs/JobSnapshot.cs`
- Test: `src/Arius.Api.Tests/Jobs/JobSinkAggregateTests.cs`, `src/Arius.Api.Tests/Jobs/JobSinkEtaTests.cs`

**Interfaces:**
- Produces (mutators): `AddScanned(long)`, `AddHashed(long)`, `AddUploaded(long stored, long original)`, `AddDeduped(long original)`, `RememberTar(ChunkHash, long uncompressed)`, `AddUploadedTar(ChunkHash)`, `SetTotals(long files, long bytes)`, `AddRestored(long)`, `SetRestoreTotals(long files, long bytes)`, `SetRehydration(int available, int rehydrated, int needsRehydration, int pending)`, `StageStarted(string)`, `StageDone(string)`, `SampleForEta(DateTimeOffset now)`.
- Produces (readers): `JobSnapshot BuildSnapshot(DateTimeOffset now)`, `JobOutcome BuildOutcome()`.
- `BuildSnapshot` returns absolute state; `etaSeconds` is null until `totalNewBytes` is known.

- [ ] **Step 1: Write the failing aggregate test**

```csharp
// src/Arius.Api.Tests/Jobs/JobSinkAggregateTests.cs
using Arius.Api.Jobs;

namespace Arius.Api.Tests.Jobs;

public class JobSinkAggregateTests
{
    private static JobSink NewArchiveSink() => new();   // inert (no hub) — aggregation is hub-independent

    [Test]
    public async Task Byte_layers_and_dedup_accumulate_as_original_bytes()
    {
        var s = NewArchiveSink();
        s.SetTotals(files: 3, bytes: 3000);
        s.AddScanned(3000);
        s.AddHashed(3000);
        s.AddUploaded(stored: 400, original: 2000);
        s.AddDeduped(original: 1000);

        var snap = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
        await Assert.That(snap.TotalBytes).IsEqualTo(3000L);
        await Assert.That(snap.ScannedBytes).IsEqualTo(3000L);
        await Assert.That(snap.HashedBytes).IsEqualTo(3000L);
        await Assert.That(snap.UploadedBytes).IsEqualTo(2000L);   // original units, not stored
        await Assert.That(snap.DedupedBytes).IsEqualTo(1000L);
        await Assert.That(snap.TotalNewBytes).IsEqualTo(2000L);   // total - deduped
    }

    [Test]
    public async Task Tar_uploaded_bytes_use_remembered_uncompressed_size()
    {
        var s = NewArchiveSink();
        var tar = ChunkHash.Parse(new string('a', 64));
        s.RememberTar(tar, uncompressed: 5000);
        s.AddUploadedTar(tar);

        var snap = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
        await Assert.That(snap.UploadedBytes).IsEqualTo(5000L);
    }
}
```

- [ ] **Step 2: Run — expect FAIL** (`JobSink` has none of these members)

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobSinkAggregateTests/*"`
Expected: FAIL (does not compile / members missing).

- [ ] **Step 3: Create the snapshot/outcome records**

```csharp
// src/Arius.Api/Jobs/JobSnapshot.cs
namespace Arius.Api.Jobs;

/// <summary>Absolute-state progress snapshot for a job — the payload for the coalesced Progress emit and for
/// snapshot-on-attach (Plan 2). Everything is a total, so a consumer applies latest-wins with no accumulation.</summary>
public sealed record JobSnapshot
{
    public required string  JobId          { get; init; }
    public required string  Phase          { get; init; }
    public required long    TotalBytes     { get; init; }
    public required long    TotalNewBytes  { get; init; }   // TotalBytes - DedupedBytes (0 until known)
    public required long    ScannedBytes   { get; init; }
    public required long    HashedBytes    { get; init; }
    public required long    UploadedBytes  { get; init; }   // original units
    public required long    DedupedBytes   { get; init; }
    public required long    DedupedFiles   { get; init; }
    public required long?   EtaSeconds     { get; init; }   // null until TotalNewBytes known
    public required double  ThroughputBytesPerSec { get; init; }
    public required int     Pct            { get; init; }   // byte-weighted; legacy consumers read this
    public required IReadOnlyDictionary<string, string> Stats { get; init; }   // legacy drawer stat grid
}

/// <summary>Compact, terminal, list-friendly completion summary (persisted to the jobs `outcome` column).</summary>
public sealed record JobOutcome
{
    public long?   FileCount         { get; init; }
    public long?   UploadedBytes     { get; init; }
    public long?   DedupedBytes      { get; init; }
    public long?   FilesRestored     { get; init; }
    public long?   DownloadedBytes   { get; init; }
    public string? SnapshotTimestamp { get; init; }
    public long?   DurationSeconds   { get; init; }
}
```

- [ ] **Step 4: Enrich `JobSink` with byte aggregates + builders**

Replace the count-based aggregate section of `JobSink.cs` (the fields and `SetTotalFiles`/`IncHashed`/`IncDeduped`/`IncUploaded`/`ReportArchive`/`ReportRestore` block, lines 30–77) with byte-based state. Keep the `Log`/`Cost`/`Done` messaging methods and the constructors unchanged.

```csharp
// ── Byte-weighted aggregate (archive + restore) ─────────────────────────────
private long _totalFiles, _totalBytes, _scannedBytes, _hashedBytes, _uploadedBytes, _dedupedBytes, _dedupedFiles;
private long _restoreTotalFiles, _restoreTotalBytes, _filesRestored, _bytesRestored;
private int  _rehydAvailable, _rehydRehydrated, _rehydNeeds, _rehydPending;
private readonly System.Collections.Concurrent.ConcurrentDictionary<ChunkHash, long> _tarUncompressed = new();
private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Started, DateTimeOffset? Done)> _stages = new();
private volatile string _phase = "starting";

public void SetTotals(long files, long bytes) { Interlocked.Exchange(ref _totalFiles, files); Interlocked.Exchange(ref _totalBytes, bytes); }
public void AddScanned(long bytes) => Interlocked.Add(ref _scannedBytes, bytes);
public void AddHashed(long bytes)  => Interlocked.Add(ref _hashedBytes, bytes);
public void AddUploaded(long stored, long original) => Interlocked.Add(ref _uploadedBytes, original);
public void AddDeduped(long original) { Interlocked.Add(ref _dedupedBytes, original); Interlocked.Increment(ref _dedupedFiles); }
public void RememberTar(ChunkHash tarHash, long uncompressed) => _tarUncompressed[tarHash] = uncompressed;
public void AddUploadedTar(ChunkHash tarHash) { if (_tarUncompressed.TryGetValue(tarHash, out var u)) Interlocked.Add(ref _uploadedBytes, u); }

public void SetRestoreTotals(long files, long bytes) { Interlocked.Exchange(ref _restoreTotalFiles, files); Interlocked.Exchange(ref _restoreTotalBytes, bytes); }
public void AddRestored(long size) { Interlocked.Increment(ref _filesRestored); Interlocked.Add(ref _bytesRestored, size); }
public void SetRehydration(int available, int rehydrated, int needs, int pending)
{ _rehydAvailable = available; _rehydRehydrated = rehydrated; _rehydNeeds = needs; _rehydPending = pending; }

public void SetPhase(string phase) => _phase = phase;
public void StageStarted(string stage) => _stages[stage] = (_now(), null);
public void StageDone(string stage) { if (_stages.TryGetValue(stage, out var e)) _stages[stage] = (e.Started, _now()); else _stages[stage] = (_now(), _now()); }
```

Add a testable clock seam (default real time; tests inject a fixed clock via a constructor overload or a settable field):

```csharp
internal Func<DateTimeOffset> _now = () => DateTimeOffset.UtcNow;
```

Then the builders:

```csharp
public JobSnapshot BuildSnapshot(DateTimeOffset now)
{
    var total    = Interlocked.Read(ref _totalBytes);
    var deduped  = Interlocked.Read(ref _dedupedBytes);
    var uploaded = Interlocked.Read(ref _uploadedBytes);
    var totalNew = total > 0 ? Math.Max(0, total - deduped) : 0;
    var rate     = RateBytesPerSec(now);
    long? eta    = totalNew > 0 && rate > 0 ? (long)Math.Ceiling((totalNew - uploaded) / rate) : null;
    var pct      = totalNew > 0 ? (int)Math.Clamp(uploaded * 100 / totalNew, 0, 100) : 0;

    return new JobSnapshot
    {
        JobId = JobId ?? "",
        Phase = _phase,
        TotalBytes = total, TotalNewBytes = totalNew,
        ScannedBytes = Interlocked.Read(ref _scannedBytes),
        HashedBytes  = Interlocked.Read(ref _hashedBytes),
        UploadedBytes = uploaded,
        DedupedBytes = deduped, DedupedFiles = Interlocked.Read(ref _dedupedFiles),
        EtaSeconds = eta, ThroughputBytesPerSec = rate, Pct = pct,
        Stats = new Dictionary<string, string>   // legacy grid — drops in Plan 3
        {
            ["Uploaded"] = JobFormat.Bytes(uploaded),
            ["Deduped"]  = Interlocked.Read(ref _dedupedFiles).ToString(),
        },
    };
}
```

(`BuildOutcome` and the ETA window are added in Steps 5–7.)

- [ ] **Step 5: Run the aggregate test — expect PASS**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobSinkAggregateTests/*"`
Expected: PASS.

- [ ] **Step 6: Write the failing ETA test**

```csharp
// src/Arius.Api.Tests/Jobs/JobSinkEtaTests.cs
using Arius.Api.Jobs;

namespace Arius.Api.Tests.Jobs;

public class JobSinkEtaTests
{
    [Test]
    public async Task Eta_is_null_until_total_new_bytes_known_then_uses_windowed_rate()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var s  = new JobSink();

        // No totals yet → estimating (null).
        s.AddUploaded(0, 1_000);
        s.SampleForEta(t0);
        await Assert.That(s.BuildSnapshot(t0).EtaSeconds).IsNull();

        // Totals known; 1 MB uploaded over 1 s = 1 MB/s; 9 MB remaining → 9 s.
        s.SetTotals(files: 10, bytes: 10_000_000);
        s.SampleForEta(t0);                    // 1_000 @ t0  (warm start)
        s.AddUploaded(0, 1_000_000);           // now 1_001_000 uploaded
        s.SampleForEta(t0.AddSeconds(1));
        var snap = s.BuildSnapshot(t0.AddSeconds(1));
        await Assert.That(snap.EtaSeconds).IsNotNull();
        await Assert.That(snap.EtaSeconds!.Value).IsBetween(8, 10);
    }
}
```

- [ ] **Step 7: Implement the ETA sliding window**

Add to `JobSink`:

```csharp
private readonly LinkedList<(DateTimeOffset At, long Uploaded)> _samples = new();
private readonly object _sampleLock = new();
private static readonly TimeSpan EtaWindow = TimeSpan.FromSeconds(60);

/// <summary>Record an (instant, cumulative-uploaded-bytes) point and drop points older than the window.</summary>
public void SampleForEta(DateTimeOffset now)
{
    lock (_sampleLock)
    {
        _samples.AddLast((now, Interlocked.Read(ref _uploadedBytes)));
        while (_samples.First is { } first && now - first.Value.At > EtaWindow)
            _samples.RemoveFirst();
    }
}

private double RateBytesPerSec(DateTimeOffset now)
{
    lock (_sampleLock)
    {
        if (_samples.First is not { } first || _samples.Last is not { } last) return 0;
        var dt = (last.Value.At - first.Value.At).TotalSeconds;
        if (dt <= 0) return 0;
        var db = last.Value.Uploaded - first.Value.Uploaded;
        return db > 0 ? db / dt : 0;
    }
}
```

- [ ] **Step 8: Run both JobSink test classes — expect PASS**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobSinkEtaTests/*"` then `.../JobSinkAggregateTests/*`.
Expected: PASS. The project will not compile yet where old `ReportArchive`/`IncHashed` callers exist (forwarders) — that is fixed in Task 4; if the test project builds independently this is fine, otherwise proceed to Task 4 before running the full solution build.

- [ ] **Step 9: Commit**

```bash
git add src/Arius.Api/Jobs/JobSink.cs src/Arius.Api/Jobs/JobSnapshot.cs src/Arius.Api.Tests/Jobs/JobSinkAggregateTests.cs src/Arius.Api.Tests/Jobs/JobSinkEtaTests.cs
git commit -m "feat(api): byte-weighted JobSink aggregate + sliding-window ETA"
```

---

## Task 3: `JobStateRegistry` singleton

**Files:**
- Create: `src/Arius.Api/Jobs/JobStateRegistry.cs`
- Test: `src/Arius.Api.Tests/Jobs/JobStateRegistryTests.cs`

**Interfaces:**
- Consumes: `JobSink` (Task 2).
- Produces: `void Register(string jobId, JobSink sink)`, `bool TryGet(string jobId, out JobSink sink)`, `void Remove(string jobId)`, `IReadOnlyCollection<string> ActiveJobIds`.

- [ ] **Step 1: Write the failing test**

```csharp
// src/Arius.Api.Tests/Jobs/JobStateRegistryTests.cs
using Arius.Api.Jobs;

namespace Arius.Api.Tests.Jobs;

public class JobStateRegistryTests
{
    [Test]
    public async Task Register_then_TryGet_then_Remove()
    {
        var reg  = new JobStateRegistry();
        var sink = new JobSink();
        reg.Register("job-1", sink);

        await Assert.That(reg.TryGet("job-1", out var got)).IsTrue();
        await Assert.That(got).IsSameReferenceAs(sink);
        await Assert.That(reg.ActiveJobIds).Contains("job-1");

        reg.Remove("job-1");
        await Assert.That(reg.TryGet("job-1", out _)).IsFalse();
    }
}
```

- [ ] **Step 2: Run — expect FAIL** (no `JobStateRegistry`)

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobStateRegistryTests/*"`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// src/Arius.Api/Jobs/JobStateRegistry.cs
using System.Collections.Concurrent;

namespace Arius.Api.Jobs;

/// <summary>Singleton map of live jobs: <c>jobId → JobSink</c>. Lets code outside a job's own service provider
/// (the hub's snapshot-on-attach, REST reads, the rehydration poller) reach a running job's live state. A job
/// is present only while its run is executing; it is removed on completion. Not persisted (the DB row is the
/// durable anchor).</summary>
public sealed class JobStateRegistry
{
    private readonly ConcurrentDictionary<string, JobSink> _sinks = new();

    public void Register(string jobId, JobSink sink) => _sinks[jobId] = sink;
    public bool TryGet(string jobId, out JobSink sink) => _sinks.TryGetValue(jobId, out sink!);
    public void Remove(string jobId) => _sinks.TryRemove(jobId, out _);
    public IReadOnlyCollection<string> ActiveJobIds => _sinks.Keys.ToArray();
}
```

- [ ] **Step 4: Register it as a singleton**

In `src/Arius.Api/Program.cs`, next to the other job service registrations (search for `AddSingleton<JobRunner>` / `RestoreApprovalRegistry`):

```csharp
builder.Services.AddSingleton<JobStateRegistry>();
```

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobStateRegistryTests/*"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Arius.Api/Jobs/JobStateRegistry.cs src/Arius.Api/Program.cs src/Arius.Api.Tests/Jobs/JobStateRegistryTests.cs
git commit -m "feat(api): add JobStateRegistry for live per-job state lookup"
```

---

## Task 4: Wire forwarders to the byte aggregate

**Files:**
- Modify: `src/Arius.Api/Hubs/ArchiveForwarders.cs`, `src/Arius.Api/Hubs/RestoreForwarders.cs`

**Interfaces:**
- Consumes: `JobSink` byte mutators (Task 2); `FileScannedEvent`, `FileHashingEvent`, `FileDedupedEvent`, `ChunkUploadedEvent`, `TarBundleSealingEvent`, `TarBundleUploadedEvent`, `ScanCompleteEvent`, `SnapshotCreatedEvent` (Core); restore events.
- The forwarders **no longer call `ReportArchive`/`ReportRestore`** (removed in Task 2). They mutate the sink; the coalesced emit (Task 5) sends. Keep the `sink.Log(...)` lines (console still lives until Plan 3).

- [ ] **Step 1: Write the failing test**

```csharp
// append to src/Arius.Api.Tests/Jobs/JobSinkAggregateTests.cs
[Test]
public async Task Archive_forwarders_populate_byte_layers()
{
    var s = new JobSink();
    await new ScanCompleteForwarder(s).Handle(new ScanCompleteEvent(2, 3000), default);
    await new FileScannedForwarder(s).Handle(new FileScannedEvent(RelativePath.Parse("a"), 2000), default);
    await new FileHashingForwarder(s).Handle(new FileHashingEvent(RelativePath.Parse("a"), 2000), default);
    await new FileDedupedForwarder(s).Handle(new FileDedupedEvent(ContentHash.Parse(new string('b',64)), 1000), default);
    await new ChunkUploadedForwarder(s).Handle(new ChunkUploadedEvent(ChunkHash.Parse(new string('c',64)), 300, 2000), default);

    var snap = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
    await Assert.That(snap.TotalBytes).IsEqualTo(3000L);
    await Assert.That(snap.ScannedBytes).IsEqualTo(2000L);
    await Assert.That(snap.UploadedBytes).IsEqualTo(2000L);
    await Assert.That(snap.DedupedBytes).IsEqualTo(1000L);
}
```

- [ ] **Step 2: Run — expect FAIL** (new forwarders don't exist; signatures changed)

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobSinkAggregateTests/*"`
Expected: FAIL.

- [ ] **Step 3: Rewrite `ArchiveForwarders.cs`**

```csharp
using Arius.Api.Jobs;
using Arius.Core.Features.ArchiveCommand;
using Mediator;

namespace Arius.Api.Hubs;

// Each forwarder folds one Arius.Core archive event into the job's JobSink byte aggregate.
// They no longer send Progress directly — the coalesced ~1s emit (JobRunner) does. Log lines stay
// until the console is removed in Plan 3.

public sealed class ScanCompleteForwarder(JobSink sink) : INotificationHandler<ScanCompleteEvent>
{
    public ValueTask Handle(ScanCompleteEvent n, CancellationToken ct)
    {
        sink.SetTotals(n.TotalFiles, n.TotalBytes);
        sink.StageDone("scan");
        sink.Log($"Indexed {n.TotalFiles} entries · {JobFormat.Bytes(n.TotalBytes)}", "info");
        return ValueTask.CompletedTask;
    }
}

public sealed class FileScannedForwarder(JobSink sink) : INotificationHandler<FileScannedEvent>
{
    public ValueTask Handle(FileScannedEvent n, CancellationToken ct) { sink.AddScanned(n.FileSize); return ValueTask.CompletedTask; }
}

public sealed class FileHashingForwarder(JobSink sink) : INotificationHandler<FileHashingEvent>
{
    public ValueTask Handle(FileHashingEvent n, CancellationToken ct) { sink.AddHashed(n.FileSize); return ValueTask.CompletedTask; }
}

public sealed class FileDedupedForwarder(JobSink sink) : INotificationHandler<FileDedupedEvent>
{
    public ValueTask Handle(FileDedupedEvent n, CancellationToken ct) { sink.AddDeduped(n.OriginalSize); return ValueTask.CompletedTask; }
}

public sealed class TarBundleSealingForwarder(JobSink sink) : INotificationHandler<TarBundleSealingEvent>
{
    public ValueTask Handle(TarBundleSealingEvent n, CancellationToken ct)
    {
        sink.RememberTar(n.TarHash, n.UncompressedSize);
        sink.Log($"  sealing tar bundle · {n.EntryCount} files · {JobFormat.Bytes(n.TarByteSize)}", "meta");
        return ValueTask.CompletedTask;
    }
}

public sealed class TarBundleUploadedForwarder(JobSink sink) : INotificationHandler<TarBundleUploadedEvent>
{
    public ValueTask Handle(TarBundleUploadedEvent n, CancellationToken ct)
    {
        sink.AddUploadedTar(n.TarHash);
        sink.Log($"  ✓ tar bundle uploaded · {n.EntryCount} files → {JobFormat.Bytes(n.StoredSize)}", "ok");
        return ValueTask.CompletedTask;
    }
}

public sealed class ChunkUploadedForwarder(JobSink sink) : INotificationHandler<ChunkUploadedEvent>
{
    public ValueTask Handle(ChunkUploadedEvent n, CancellationToken ct)
    {
        sink.AddUploaded(n.StoredSize, n.OriginalSize);
        sink.Log($"  ✓ {n.ChunkHash.Short8} → {JobFormat.Bytes(n.StoredSize)}", "ok");
        return ValueTask.CompletedTask;
    }
}

public sealed class SnapshotCreatedForwarder(JobSink sink) : INotificationHandler<SnapshotCreatedEvent>
{
    public ValueTask Handle(SnapshotCreatedEvent n, CancellationToken ct)
    {
        sink.StageDone("snapshot");
        sink.Log($"Writing manifest · snapshot {n.FileCount} files", "info");
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 4: Update `RestoreForwarders.cs`**

Change the aggregate calls to the byte model; keep the Log lines. The relevant edits:
- `TreeTraversalCompleteForwarder`: `sink.SetRestoreTotals(n.FileCount, n.TotalOriginalSize);` (was `SetTotalRestore` + `ReportRestore(10)`).
- `RehydrationStatusForwarder`: `sink.SetRehydration(n.Available, n.Rehydrated, n.NeedsRehydration, n.Pending);` (plus its existing Log).
- `FileRestoredForwarder`: `sink.AddRestored(n.FileSize);` (was `IncRestored` + `ReportRestore()`).
- `RehydrationStartedForwarder`, `ChunkDownloadStartedForwarder`, `SnapshotResolvedForwarder`: keep as-is except remove any `ReportRestore(...)` calls; keep Log.

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobSinkAggregateTests/*"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Arius.Api/Hubs/ArchiveForwarders.cs src/Arius.Api/Hubs/RestoreForwarders.cs src/Arius.Api.Tests/Jobs/JobSinkAggregateTests.cs
git commit -m "feat(api): forward Core events into byte-weighted JobSink aggregate"
```

---

## Task 5: Coalesced ~1 s emit + Progress payload

**Files:**
- Modify: `src/Arius.Api/Jobs/JobSink.cs` (emit + timer), `src/Arius.Api/Jobs/JobRunner.cs` (start/stop/register)
- Test: `src/Arius.Api.Tests/Jobs/JobSinkEtaTests.cs` (add an emit-payload assertion via a fake hub)

**Interfaces:**
- Consumes: `JobStateRegistry` (Task 3), `JobSnapshot` (Task 2).
- Produces on `JobSink`: `void StartReporting()`, `void StopReporting()`, `void EmitNow()`. The SignalR message name stays `"Progress"`; the payload is `JobSnapshot` (superset — legacy `Pct`/`Stats` fields present).

- [ ] **Step 1: Add emit + timer to `JobSink`**

The `Group` proxy and constructors already exist. Add:

```csharp
private Timer? _timer;

/// <summary>Begins coalesced progress emission (~1s) plus ETA sampling. No-op for an inert (no-hub) sink.</summary>
public void StartReporting()
{
    if (JobId is null) return;
    _timer = new Timer(_ => { SampleForEta(_now()); EmitNow(); }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
}

public void StopReporting() { _timer?.Dispose(); _timer = null; EmitNow(); }

/// <summary>Sends the current absolute snapshot immediately (used on transitions and on stop).</summary>
public void EmitNow() => Group?.SendAsync("Progress", BuildSnapshot(_now()));
```

Keep `Done`/`Cost`/`Log`. Remove the now-unused `ReportArchive`/`ReportRestore` if any references remain.

- [ ] **Step 2: Wire lifecycle + registry into `JobRunner`**

Inject `JobStateRegistry registry` into `JobRunner`'s constructor (add the parameter and a field). In **both** `RunArchiveAsync` and `RunRestoreAsync`, immediately after `var sink = new JobSink(jobId, hub);`:

```csharp
registry.Register(jobId, sink);
sink.StartReporting();
```

And in **both** `finally` blocks, before/around the existing cleanup:

```csharp
sink.StopReporting();
registry.Remove(jobId);
```

(The `provider.DisposeAsync()` / `gate.Release()` lines stay.)

- [ ] **Step 3: Write the failing emit test**

Add a fake hub double under `src/Arius.Api.Tests/Fakes/` capturing `SendAsync` payloads, or assert via `BuildSnapshot` directly if wiring a fake `IHubContext` is heavy. Minimal behavioral assertion using `BuildSnapshot` (already covered) plus a compile-time check that `StartReporting`/`StopReporting` exist:

```csharp
// src/Arius.Api.Tests/Jobs/JobSinkEtaTests.cs  (add)
[Test]
public async Task Inert_sink_reporting_is_noop()
{
    var s = new JobSink();          // no hub, no jobId
    s.StartReporting();             // must not throw / must not start a timer
    s.StopReporting();
    await Assert.That(s.BuildSnapshot(DateTimeOffset.UnixEpoch).Pct).IsEqualTo(0);
}
```

- [ ] **Step 4: Run — expect PASS; build the solution**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobSinkEtaTests/*"` then `dotnet build src/Arius.Api`.
Expected: PASS + clean build (all old `ReportArchive`/`IncHashed` references now gone).

- [ ] **Step 5: Commit**

```bash
git add src/Arius.Api/Jobs/JobSink.cs src/Arius.Api/Jobs/JobRunner.cs src/Arius.Api.Tests/Jobs/JobSinkEtaTests.cs
git commit -m "feat(api): coalesced ~1s absolute-state Progress emit + registry lifecycle"
```

---

## Task 6: Persistence — `state_json` / `outcome` + records

**Files:**
- Modify: `src/Arius.Api/AppData/AppDatabase.cs`, `src/Arius.Api/AppData/Records.cs`, `src/Arius.Api/Contracts/Dtos.cs`, `src/Arius.Api/Endpoints/JobEndpoints.cs`
- Test: `src/Arius.Api.Tests/AppData/JobPersistenceTests.cs`

**Interfaces:**
- Produces on `AppDatabase`: `void SaveJobState(string id, string stateJson)`, `void SetJobOutcome(string id, string outcomeJson)`; `CompleteJob` unchanged; `ListJobs`/`ReadJob`/`JobRecord` gain `StateJson`, `Outcome`.

- [ ] **Step 1: Write the failing persistence test**

```csharp
// src/Arius.Api.Tests/AppData/JobPersistenceTests.cs
using Arius.Api.AppData;

namespace Arius.Api.Tests.AppData;

public class JobPersistenceTests
{
    // Follow StatisticsCacheTests for the temp-file AppDatabase setup pattern.
    [Test]
    public async Task State_and_outcome_persist_and_read_back()
    {
        using var db = TestDb.Create();                 // helper mirroring StatisticsCacheTests
        var repoId = db.InsertRepositoryForTest();      // helper: minimal account+repo
        db.InsertJob("j1", repoId, "archive", "one-off", "running");

        db.SaveJobState("j1", "{\"phase\":\"upload\"}");
        db.SetJobOutcome("j1", "{\"fileCount\":3}");

        var job = db.ListJobs().Single(j => j.Id == "j1");
        await Assert.That(job.StateJson).IsEqualTo("{\"phase\":\"upload\"}");
        await Assert.That(job.Outcome).IsEqualTo("{\"fileCount\":3}");
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobPersistenceTests/*"`
Expected: FAIL (columns/methods/fields missing).

- [ ] **Step 3: Add the columns via the existing migration pattern**

In `AppDatabase.cs`, after the `EnsureColumn(connection, table: "repositories", column: "region_hint", type: "TEXT");` line (~line 100):

```csharp
EnsureColumn(connection, table: "jobs", column: "state_json", type: "TEXT");
EnsureColumn(connection, table: "jobs", column: "outcome",    type: "TEXT");
```

- [ ] **Step 4: Add the save methods**

In the `// ── Jobs ──` region of `AppDatabase.cs`:

```csharp
public void SaveJobState(string id, string stateJson)
{
    using var connection = OpenConnection();
    using var command = connection.CreateCommand();
    command.CommandText = "UPDATE jobs SET state_json = $s WHERE id = $id;";
    command.Parameters.AddWithValue("$s", stateJson);
    command.Parameters.AddWithValue("$id", id);
    command.ExecuteNonQuery();
}

public void SetJobOutcome(string id, string outcomeJson)
{
    using var connection = OpenConnection();
    using var command = connection.CreateCommand();
    command.CommandText = "UPDATE jobs SET outcome = $o WHERE id = $id;";
    command.Parameters.AddWithValue("$o", outcomeJson);
    command.Parameters.AddWithValue("$id", id);
    command.ExecuteNonQuery();
}
```

- [ ] **Step 5: Extend `ListJobs`/`ReadJob` and `JobRecord`**

`AppDatabase.ListJobs` SELECT (line 381) — add the two columns:

```csharp
command.CommandText = "SELECT id, repo_id, kind, trigger, status, pct, detail, started_at, finished_at, state_json, outcome FROM jobs ORDER BY COALESCE(started_at, '') DESC LIMIT $limit;";
```

`ReadJob` (line 512) — append two reads:

```csharp
private static JobRecord ReadJob(SqliteDataReader reader) => new(
    reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
    reader.GetString(4), reader.GetDouble(5),
    reader.IsDBNull(6) ? null : reader.GetString(6),
    reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
    reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
    reader.IsDBNull(9) ? null : reader.GetString(9),
    reader.IsDBNull(10) ? null : reader.GetString(10));
```

`Records.cs` — extend `JobRecord`:

```csharp
public sealed record JobRecord(
    string Id, long RepositoryId, string Kind, string Trigger, string Status,
    double Pct, string? Detail, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    string? StateJson = null, string? Outcome = null);
```

- [ ] **Step 6: Surface `Outcome` on the list DTO**

In `Contracts/Dtos.cs`, add `string? Outcome` to `JobDto` (as the last, nullable parameter). In `JobEndpoints.cs` `GET /jobs`, pass `j.Outcome` into the `JobDto` construction.

- [ ] **Step 7: Run — expect PASS**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobPersistenceTests/*"`
Expected: PASS.

- [ ] **Step 8: Persist from `JobSink`/`JobRunner`**

Add `BuildOutcome()` to `JobSink` (archive branch shown; restore fills the restore fields):

```csharp
public JobOutcome BuildOutcome(DateTimeOffset startedAt, DateTimeOffset now, string? snapshotTimestamp) => new()
{
    FileCount = Interlocked.Read(ref _totalFiles) is var f and > 0 ? f : null,
    UploadedBytes = Interlocked.Read(ref _uploadedBytes),
    DedupedBytes  = Interlocked.Read(ref _dedupedBytes),
    FilesRestored = Interlocked.Read(ref _filesRestored) is var r and > 0 ? r : null,
    DownloadedBytes = Interlocked.Read(ref _bytesRestored),
    SnapshotTimestamp = snapshotTimestamp,
    DurationSeconds = (long)(now - startedAt).TotalSeconds,
};
```

In `JobRunner`, on successful completion (both archive and restore, before `sink.Done(...)`), persist state + outcome using `System.Text.Json`:

```csharp
database.SaveJobState(jobId, JsonSerializer.Serialize(sink.BuildSnapshot(DateTimeOffset.UtcNow)));
database.SetJobOutcome(jobId, JsonSerializer.Serialize(sink.BuildOutcome(startedAt, DateTimeOffset.UtcNow, snapshotTimestamp)));
```

(Capture `startedAt = DateTimeOffset.UtcNow` at the top of each run; `snapshotTimestamp` = the archive's `SnapshotTime` when available, else null. The ~5–10 s periodic `SaveJobState` flush is added in Plan 2 alongside `AttachToJob`; Plan 1 persists at completion, which is enough for history + the restart reconciliation of Task 7.)

- [ ] **Step 9: Build + commit**

Run: `dotnet build src/Arius.Api` (expect clean).

```bash
git add src/Arius.Api/AppData/ src/Arius.Api/Contracts/Dtos.cs src/Arius.Api/Endpoints/JobEndpoints.cs src/Arius.Api/Jobs/ src/Arius.Api.Tests/AppData/JobPersistenceTests.cs
git commit -m "feat(api): persist job state_json + outcome; extend JobRecord/JobDto"
```

---

## Task 7: Single-active-job guard + status vocab + restart reconciliation

**Files:**
- Modify: `src/Arius.Api/AppData/AppDatabase.cs`, `src/Arius.Api/Hubs/JobsHub.cs`, `src/Arius.Api/Jobs/SchedulerService.cs`, `src/Arius.Api/Jobs/JobRunner.cs`, `src/Arius.Api/Program.cs`
- Test: `src/Arius.Api.Tests/AppData/JobGuardTests.cs`

**Interfaces:**
- Produces on `AppDatabase`: `bool HasActiveJob(long repositoryId)`, `int ReconcileInterruptedJobs()`. The partial unique index `ux_jobs_one_active_per_repo` is the race-proof backstop.

- [ ] **Step 1: Write the failing guard test**

```csharp
// src/Arius.Api.Tests/AppData/JobGuardTests.cs
using Arius.Api.AppData;
using Microsoft.Data.Sqlite;

namespace Arius.Api.Tests.AppData;

public class JobGuardTests
{
    [Test]
    public async Task HasActiveJob_true_while_running_false_when_terminal()
    {
        using var db = TestDb.Create();
        var repoId = db.InsertRepositoryForTest();
        db.InsertJob("j1", repoId, "archive", "one-off", "running");
        await Assert.That(db.HasActiveJob(repoId)).IsTrue();

        db.CompleteJob("j1", "completed", 100, null);
        await Assert.That(db.HasActiveJob(repoId)).IsFalse();
    }

    [Test]
    public async Task Second_active_job_for_same_repo_is_rejected_by_the_index()
    {
        using var db = TestDb.Create();
        var repoId = db.InsertRepositoryForTest();
        db.InsertJob("j1", repoId, "archive", "one-off", "running");
        await Assert.That(() => db.InsertJob("j2", repoId, "restore", "one-off", "running"))
                    .Throws<SqliteException>();
    }

    [Test]
    public async Task ReconcileInterruptedJobs_marks_orphaned_running_as_interrupted()
    {
        using var db = TestDb.Create();
        var repoId = db.InsertRepositoryForTest();
        db.InsertJob("j1", repoId, "archive", "one-off", "running");
        var n = db.ReconcileInterruptedJobs();
        await Assert.That(n).IsEqualTo(1);
        await Assert.That(db.ListJobs().Single(j => j.Id == "j1").Status).IsEqualTo("interrupted");
    }
}
```

- [ ] **Step 2: Run — expect FAIL**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobGuardTests/*"`
Expected: FAIL.

- [ ] **Step 3: Add the partial unique index to schema init**

In `AppDatabase.cs`, append to the `CREATE TABLE ... schedules/statistics_cache` DDL string (before the closing `""";`), or as a follow-up `ExecuteNonQuery`:

```sql
CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_one_active_per_repo
    ON jobs(repo_id)
    WHERE status IN ('running','awaiting-cost','rehydrating');
```

- [ ] **Step 4: Add `HasActiveJob` + `ReconcileInterruptedJobs`**

```csharp
public bool HasActiveJob(long repositoryId)
{
    using var connection = OpenConnection();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT 1 FROM jobs WHERE repo_id = $r AND status IN ('running','awaiting-cost','rehydrating') LIMIT 1;";
    command.Parameters.AddWithValue("$r", repositoryId);
    return command.ExecuteScalar() is not null;
}

/// <summary>On Api startup, any job left <c>running</c> by a crash/restart is dead (its in-process run is gone).
/// Mark it <c>interrupted</c>. (Plan 2 extends this to revert a resumable rehydration job to <c>rehydrating</c>.)</summary>
public int ReconcileInterruptedJobs()
{
    using var connection = OpenConnection();
    using var command = connection.CreateCommand();
    command.CommandText = "UPDATE jobs SET status = 'interrupted', finished_at = $t WHERE status = 'running';";
    command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
    return command.ExecuteNonQuery();
}
```

- [ ] **Step 5: Run — expect PASS**

Run: `dotnet test --project src/Arius.Api.Tests --treenode-filter "/*/*/JobGuardTests/*"`
Expected: PASS.

- [ ] **Step 6: Enforce the guard at the start paths**

`JobsHub.StartArchive`/`StartRestore` — before creating the job id:

```csharp
if (database.HasActiveJob(repositoryId))
    throw new HubException("A job is already running for this repository.");
```

(`JobsHub` already injects `AppDatabase database`.)

`SchedulerService.Tick` — inside the `repo is not null` block, guard the fire:

```csharp
if (repo is not null && !database.HasActiveJob(schedule.RepositoryId))
{
    // ... existing fire (RunArchiveAsync) ...
}
else if (repo is not null)
{
    logger.LogWarning("Skipped scheduled archive for repository {RepositoryId} — a job is already in progress", schedule.RepositoryId);
}
```

`JobRunner.RunArchiveAsync`/`RunRestoreAsync` — the `InsertJob` call is the race-proof backstop; wrap it so a losing racer reports busy instead of crashing:

```csharp
try { database.InsertJob(jobId, repositoryId, "archive", trigger, "running"); }
catch (SqliteException) { sink.Done("failed", "A job is already running for this repository."); return; }
```

- [ ] **Step 7: Call reconciliation at startup**

In `Program.cs`, after the app is built and the `AppDatabase` is available (search where `AppDatabase` is constructed / migrations run), before `app.Run()`:

```csharp
app.Services.GetRequiredService<AppDatabase>().ReconcileInterruptedJobs();
```

- [ ] **Step 8: Build + run the Api test suite**

Run: `dotnet build src/Arius.Api` then `dotnet test --project src/Arius.Api.Tests`.
Expected: clean build; all Api tests PASS.

- [ ] **Step 9: Commit**

```bash
git add src/Arius.Api/AppData/ src/Arius.Api/Hubs/JobsHub.cs src/Arius.Api/Jobs/ src/Arius.Api/Program.cs src/Arius.Api.Tests/AppData/JobGuardTests.cs
git commit -m "feat(api): single-active-job-per-repo guard + interrupted-job restart reconciliation"
```

---

## Self-review checklist (completed)

**Spec coverage:** §4 job-state model → Tasks 2, 6 (StateJson persistence; the ~5–10 s live flush + full snapshot-on-attach are explicitly deferred to Plan 2). §6 guard → Task 7. §10 progress/ETA → Tasks 2, 4, 5. §11 events → Task 1. §12 status vocab + reconciliation → Task 7. §9 outcomes → Task 6. Reattach/cancel/cost/rehydration/warnings/pill are Plans 2–3 by design.

**Placeholder scan:** no TBD/TODO; every code step shows real code. The one "follow the existing fixture" note (Task 1 Step 1, Task 6 Step 1) points at named existing files (`PipelineFixture`, `StatisticsCacheTests`) rather than leaving logic unwritten — the test *bodies* are complete.

**Type consistency:** `JobSnapshot`/`JobOutcome` (Task 2) are consumed unchanged in Tasks 5–6; `JobSink` mutators named identically across Tasks 2, 4, 6; `HasActiveJob`/`ReconcileInterruptedJobs` signatures match between Task 7 definition and callers; `JobRecord`'s two new trailing params match the `ReadJob` order.

**Known cross-task assumption:** Tasks 2/5 change `JobSink`'s public surface, so the solution only builds green after Task 4 removes the old `ReportArchive`/`Inc*` callers. Steps note this and gate the full-solution build to Task 5 Step 4 onward.
