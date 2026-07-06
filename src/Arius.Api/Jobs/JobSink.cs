using Arius.Api.Hubs;
using Arius.Core.Shared.Hashes;
using Microsoft.AspNetCore.SignalR;

namespace Arius.Api.Jobs;

/// <summary>
/// Per-job channel to the client. Registered as a singleton inside a job's own service provider, so
/// the INotification forwarders (and the command's IProgress closures) resolve exactly this job's
/// sink — events are isolated by provider, not by a correlation id. Read providers get an inert sink
/// (no <see cref="JobId"/>), so the same forwarders are harmless there. Also holds the per-job
/// aggregate counters that feed the drawer's stat grid.
/// </summary>
public sealed class JobSink
{
    private readonly IHubContext<JobsHub>? _hub;

    /// <summary>The SignalR group id (= the job id), or null for an inert (non-job) sink.</summary>
    public string? JobId { get; }

    /// <summary>Per-job cancellation source. <see cref="CancelJob"/> cancels this; <see cref="JobRunner"/>
    /// threads its token into the Core command. A fresh source per sink (including inert sinks, where it is
    /// simply never observed).</summary>
    public CancellationTokenSource Cts { get; } = new();

    public JobSink() { }                                  // inert sink for read providers
    public JobSink(string jobId, IHubContext<JobsHub> hub) { JobId = jobId; _hub = hub; }

    private IClientProxy? Group => JobId is null || _hub is null ? null : _hub.Clients.Group(JobId);

    // ── Messages ────────────────────────────────────────────────────────────
    public void Log(string text, string severity = "meta")
    {
        if (severity is "warn" or "error")
            CaptureWarning(text);
        Group?.SendAsync("Log", new { text, severity });
    }
    public void Cost(object estimate) => Group?.SendAsync("CostEstimate", estimate);

    private volatile bool _done;
    /// <summary>Whether a terminal <see cref="Done"/> has been sent — suppresses any late progress emit.</summary>
    public bool IsDone => _done;

    /// <summary>Serializes the terminal <see cref="Done"/> send against <see cref="EmitNow"/> so the two can never
    /// interleave: without this, the 1s timer's <c>EmitNow</c> could read <c>_done == false</c>, get preempted, and
    /// send a stale "Progress" after "Done" already reached the client. Never held across <see cref="BuildSnapshot"/>,
    /// so it cannot nest under <see cref="_sampleLock"/>/<see cref="_warnLock"/>.</summary>
    private readonly object _emitLock = new();

    public void Done(string status, string summary, string? outcomeJson = null)
    {
        lock (_emitLock)
        {
            _done = true;
            Group?.SendAsync("Done", new { jobId = JobId, status, summary, outcome = outcomeJson });
        }
    }

    // ── Warnings capture (verbatim warn/error lines; count survives ring trimming) ──
    private const int WarningRingCap = 200;
    private readonly LinkedList<string> _warnings = new();
    private readonly object _warnLock = new();
    private int _warningCount;

    private void CaptureWarning(string text)
    {
        lock (_warnLock)
        {
            _warningCount++;
            _warnings.AddLast(text);
            if (_warnings.Count > WarningRingCap) _warnings.RemoveFirst();
        }
    }

    /// <summary>Total warn/error lines seen (accurate even after the ring trims older lines).</summary>
    public int WarningCount { get { lock (_warnLock) return _warningCount; } }

    /// <summary>The last ≤200 warn/error lines, oldest→newest.</summary>
    public IReadOnlyList<string> Warnings { get { lock (_warnLock) return _warnings.ToArray(); } }

    // ── Coalesced progress reporting ─────────────────────────────────────────
    private Timer? _timer;

    /// <summary>Begins coalesced progress emission (~1s) plus ETA sampling. No-op for an inert (no-hub) sink.</summary>
    public void StartReporting()
    {
        if (JobId is null) return;
        _timer = new Timer(_ => { SampleForEta(_now()); EmitNow(); }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void StopReporting() { _timer?.Dispose(); _timer = null; EmitNow(); }

    /// <summary>Sends the current absolute snapshot immediately (used on transitions and on stop). No-ops once
    /// <see cref="Done"/> has been sent, so the timer's final <see cref="StopReporting"/> emit can never race a
    /// terminal message that already reached the client (a reattaching client would otherwise see a
    /// live-looking Progress after the job ended).</summary>
    public void EmitNow()
    {
        if (_done) return;                       // cheap early-out
        var snapshot = BuildSnapshot(_now());    // built outside the lock (absolute-state; a stale build is fine)
        lock (_emitLock)
        {
            if (_done) return;                   // re-check under the lock: a Done that raced in wins
            Group?.SendAsync("Progress", snapshot);
        }
    }

    // ── Byte-weighted aggregate (archive + restore) ─────────────────────────────
    private long _totalFiles, _totalBytes, _scannedBytes, _hashedBytes, _uploadedBytes, _dedupedBytes, _dedupedFiles, _queuedNewBytes;
    private long _restoreTotalFiles, _restoreTotalBytes, _filesRestored, _bytesRestored, _chunkBytesTotal;
    private volatile int _rehydAvailable, _rehydRehydrated, _rehydNeeds, _rehydPending, _chunksTotal;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ChunkHash, long> _tarUncompressed = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Started, DateTimeOffset? Done)> _stages = new();
    private volatile string _phase = "starting";

    /// <summary>Testable clock seam — tests inject a fixed/stepped clock; production uses real time.</summary>
    internal Func<DateTimeOffset> _now = () => DateTimeOffset.UtcNow;

    public void SetTotals(long files, long bytes) { Interlocked.Exchange(ref _totalFiles, files); Interlocked.Exchange(ref _totalBytes, bytes); }
    public void AddScanned(long bytes) => Interlocked.Add(ref _scannedBytes, bytes);
    public void AddHashed(long bytes)  => Interlocked.Add(ref _hashedBytes, bytes);
    public void AddUploaded(long stored, long original) => Interlocked.Add(ref _uploadedBytes, original);
    /// <summary>Accumulates the original (uncompressed) size of each new chunk queued for upload — the
    /// additive "new bytes to upload" total. Independent of the deduped/total subtraction, so it never
    /// underflows for pointer-only-heavy repos. Final once routing completes; 0 until the first queue.</summary>
    public void AddQueuedNew(long originalSize) => Interlocked.Add(ref _queuedNewBytes, originalSize);
    public void AddDeduped(long original) { Interlocked.Add(ref _dedupedBytes, original); Interlocked.Increment(ref _dedupedFiles); }
    public void RememberTar(ChunkHash tarHash, long uncompressed) => _tarUncompressed[tarHash] = uncompressed;
    public void AddUploadedTar(ChunkHash tarHash) { if (_tarUncompressed.TryGetValue(tarHash, out var u)) Interlocked.Add(ref _uploadedBytes, u); }

    public void SetRestoreTotals(long files, long bytes) { Interlocked.Exchange(ref _restoreTotalFiles, files); Interlocked.Exchange(ref _restoreTotalBytes, bytes); }
    public void AddRestored(long size) { Interlocked.Increment(ref _filesRestored); Interlocked.Add(ref _bytesRestored, size); }
    public void SetRehydration(int available, int rehydrated, int needs, int pending)
    { _rehydAvailable = available; _rehydRehydrated = rehydrated; _rehydNeeds = needs; _rehydPending = pending; }

    /// <summary>Records the authoritative distinct-chunk total (and their byte total) from
    /// ChunkResolutionCompleteEvent — the single denominator for the restore hydration bar, so it no longer
    /// has to be (wrongly) reconstructed from a subset of the rehydration buckets.</summary>
    public void SetChunkTotals(int totalChunks, long totalChunkBytes)
    { _chunksTotal = totalChunks; Interlocked.Exchange(ref _chunkBytesTotal, totalChunkBytes); }

    public void SetPhase(string phase) => _phase = phase;
    public void StageStarted(string stage) => _stages[stage] = (_now(), null);
    public void StageDone(string stage) { if (_stages.TryGetValue(stage, out var e)) _stages[stage] = (e.Started, _now()); else _stages[stage] = (_now(), _now()); }

    // ── Sliding-window ETA (a job is either archive or restore, never both, so the two
    // progress-byte counters are summed into one unified clock) ────────────────────────
    private readonly LinkedList<(DateTimeOffset At, long Progress)> _samples = new();
    private readonly object _sampleLock = new();
    private static readonly TimeSpan EtaWindow = TimeSpan.FromSeconds(60);

    /// <summary>Record an (instant, cumulative-progress-bytes) point and drop points older than the window.</summary>
    public void SampleForEta(DateTimeOffset now)
    {
        lock (_sampleLock)
        {
            var progress = Interlocked.Read(ref _uploadedBytes) + Interlocked.Read(ref _bytesRestored);
            _samples.AddLast((now, progress));
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
            var db = last.Value.Progress - first.Value.Progress;
            return db > 0 ? db / dt : 0;
        }
    }

    // ── Snapshot builder ─────────────────────────────────────────────────────
    public JobSnapshot BuildSnapshot(DateTimeOffset now)
    {
        var total    = Interlocked.Read(ref _totalBytes);
        var deduped  = Interlocked.Read(ref _dedupedBytes);
        var uploaded = Interlocked.Read(ref _uploadedBytes);
        var hashed   = Interlocked.Read(ref _hashedBytes);
        // "New bytes to upload" is the sum of queued new-chunk sizes (additive) — NOT total - deduped,
        // which underflows when pointer-only files (scanned as 0 bytes) are deduped at full size.
        var totalNew = Interlocked.Read(ref _queuedNewBytes);
        var rate     = RateBytesPerSec(now);

        // A job is either archive or restore, never both — pick the active kind's totals/progress
        // so both Pct and EtaSeconds are meaningful whichever kind is running.
        var restoreTotalFiles = Interlocked.Read(ref _restoreTotalFiles);
        var restored          = Interlocked.Read(ref _bytesRestored);
        var restoreTotal      = Interlocked.Read(ref _restoreTotalBytes);
        var isRestore         = restoreTotal > 0 || restoreTotalFiles > 0;
        var denominator       = isRestore ? restoreTotal : totalNew;
        var progress          = isRestore ? restored     : uploaded;

        long? eta = denominator > 0 && rate > 0 ? (long)Math.Ceiling(Math.Max(0L, denominator - progress) / rate) : null;
        var pct = denominator > 0
            ? (int)Math.Clamp(progress * 100 / denominator, 0, 100)
            : (isRestore && restoreTotalFiles > 0
                ? (int)Math.Clamp(Interlocked.Read(ref _filesRestored) * 100 / restoreTotalFiles, 0, 100)
                // Archive before any upload work is known: reflect scan/hash progress so the ring isn't stuck at 0.
                : (!isRestore && total > 0 ? (int)Math.Clamp(hashed * 100 / total, 0, 100) : 0));

        return new JobSnapshot
        {
            JobId = JobId ?? "",
            Phase = _phase,
            TotalBytes = total, TotalNewBytes = totalNew,
            ScannedBytes = Interlocked.Read(ref _scannedBytes),
            HashedBytes  = hashed,
            UploadedBytes = uploaded,
            DedupedBytes = deduped, DedupedFiles = Interlocked.Read(ref _dedupedFiles),
            EtaSeconds = eta, ThroughputBytesPerSec = rate, Pct = pct,
            WarningCount = WarningCount,
            Stats = new Dictionary<string, string>   // legacy grid — drops in Plan 3
            {
                ["Uploaded"] = JobFormat.Bytes(uploaded),
                ["Deduped"]  = Interlocked.Read(ref _dedupedFiles).ToString(),
            },
            RestoreTotalFiles = restoreTotalFiles,
            FilesRestored = Interlocked.Read(ref _filesRestored),
            RestoreTotalBytes = restoreTotal,
            BytesRestored = restored,
            ChunksAvailable = _rehydAvailable,
            ChunksRehydrated = _rehydRehydrated,
            ChunksNeedingRehydration = _rehydNeeds,
            ChunksPending = _rehydPending,
            ChunksTotal = _chunksTotal,
            ChunkBytesTotal = Interlocked.Read(ref _chunkBytesTotal),
        };
    }

    /// <summary>Builds the compact, terminal outcome summary persisted to the jobs `outcome` column on completion.
    /// Archive fields come from the archive counters, restore fields from the restore counters — a job is
    /// either archive or restore, never both, so the unused side's fields are naturally null/zero.</summary>
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

    /// <summary>Assembles the <see cref="PersistedJobState"/> written to <c>state_json</c> — current snapshot,
    /// warnings tail, and (restore) the resume params. Archive jobs pass <paramref name="resume"/> = null.</summary>
    public PersistedJobState BuildPersistedState(DateTimeOffset now, RestoreResumeState? resume) => new()
    {
        Snapshot = BuildSnapshot(now),
        Warnings = Warnings,
        Resume   = resume,
    };

    /// <summary>Copies the current available+rehydrated chunk count into <paramref name="resume"/> (used by the
    /// poller to tighten cadence once rehydration has started producing ready chunks). Returns the same instance
    /// with the count applied.</summary>
    public RestoreResumeState WithLiveRehydrationCounts(RestoreResumeState resume) =>
        resume with { AvailableOrRehydratedCount = _rehydAvailable + _rehydRehydrated };
}
