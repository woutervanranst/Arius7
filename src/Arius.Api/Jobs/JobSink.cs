using Arius.Api.Contracts;
using Arius.Api.Hubs;
using Arius.Core.Shared.Hashes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger? _logger;   // optional diagnostic logger (ETA/throughput tracing); null on inert/read sinks

    /// <summary>The SignalR group id (= the job id), or null for an inert (non-job) sink.</summary>
    public string? JobId { get; }

    /// <summary>Per-job cancellation source. <see cref="CancelJob"/> cancels this; <see cref="JobRunner"/>
    /// threads its token into the Core command.</summary>
    public CancellationTokenSource Cts { get; } = new();

    public JobSink() { }                                  // inert sink for read providers
    public JobSink(string jobId, IHubContext<JobsHub> hub, ILogger? logger = null) { JobId = jobId; _hub = hub; _logger = logger; }

    private IClientProxy? Group => JobId is null || _hub is null ? null : _hub.Clients.Group(JobId);

    // ── Messages ────────────────────────────────────────────────────────────
    public void Log(string text, string severity = "meta")
    {
        // Capture warn/error lines for the warnings panel/count.
        if (severity is "warn" or "error")
            CaptureWarning(text);
    }
    /// <summary>The most recent cost estimate pushed via <see cref="Cost"/>, retained so a live reattach while the
    /// run is blocked awaiting cost-approval can render "Review cost ›" without waiting for the persisted snapshot.
    /// Cleared by <see cref="ClearPending"/> once the prompt resolves.</summary>
    public CostEstimateDto? PendingCost { get; private set; }

    /// <summary>The resume defaults (auto-resume + rehydration window) that would apply if this cost prompt times
    /// out — surfaced alongside <see cref="PendingCost"/> for the same live-reattach window.</summary>
    public RestoreResumeState? PendingResume { get; private set; }

    public void Cost(CostEstimateDto estimate) { PendingCost = estimate; Group?.SendAsync("CostEstimate", estimate); }

    public void SetPendingResume(RestoreResumeState resume) => PendingResume = resume;

    /// <summary>Clears the pending cost/resume once the prompt resolves (approved) — a live reattach after this
    /// point is mid-restore, not awaiting a decision, so it must not show a stale modal.</summary>
    public void ClearPending() { PendingCost = null; PendingResume = null; }

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
        _timer = new Timer(_ => { var now = _now(); SampleForEta(now); EmitNow(); LogEtaDiagnostics(now); }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void StopReporting() { _timer?.Dispose(); _timer = null; EmitNow(); }

    /// <summary>Once per reporting tick, logs the EMA throughput state and the derived rate/ETA at Debug under
    /// the "[ETA]" tag, so the numbers driving the header can be traced via ARIUS_LOG_LEVEL=Debug. No-op unless a
    /// logger was supplied and Debug is enabled.</summary>
    private void LogEtaDiagnostics(DateTimeOffset now)
    {
        if (_logger is null || JobId is null || !_logger.IsEnabled(LogLevel.Debug)) return;
        var snap = BuildSnapshot(now);
        _logger.LogDebug(
            "[ETA] job={JobId} phase={Phase} pct={Pct} eta={Eta}s rate={Rate:F0}B/s | archive up={Uploaded} newTotal={TotalNew} hashed={Hashed} scanned={Scanned} total={Total} deduped={Deduped} | restore restored={Restored}/{RestoreTotal}B files={FilesRestored}/{RestoreTotalFiles} chunks total={ChunksTotal} avail={ChunksAvailable} rehyd={ChunksRehydrated} needs={ChunksNeedingRehydration} pending={ChunksPending}",
            JobId, snap.Phase, snap.Pct, snap.EtaSeconds, snap.ThroughputBytesPerSec,
            snap.UploadedBytes, snap.TotalNewBytes, snap.HashedBytes, snap.ScannedBytes, snap.TotalBytes, snap.DedupedBytes,
            snap.BytesRestored, snap.RestoreTotalBytes, snap.FilesRestored, snap.RestoreTotalFiles,
            snap.ChunksTotal, snap.ChunksAvailable, snap.ChunksRehydrated, snap.ChunksNeedingRehydration, snap.ChunksPending);
    }

    /// <summary>Sends the current absolute snapshot immediately (used on transitions and on stop). No-ops once
    /// <see cref="Done"/> has been sent, so the timer's final <see cref="StopReporting"/> emit can never race a
    /// terminal message that already reached the client (a reattaching client would otherwise see a
    /// live-looking Progress after the job ended).</summary>
    public void EmitNow()
    {
        if (_done) return;
        var snapshot = BuildSnapshot(_now());    // built outside the lock (absolute-state; a stale build is fine)
        lock (_emitLock)
        {
            if (_done) return;                   // re-check under the lock: a Done that raced in wins
            Group?.SendAsync("Progress", snapshot);
        }
    }

    // ── Byte-weighted aggregate (archive + restore) ─────────────────────────────
    private long _totalFiles, _totalBytes, _scannedBytes, _scannedFiles, _hashedBytes, _uploadedBytes, _dedupedBytes, _dedupedFiles, _queuedNewBytes;
    private long _restoreTotalFiles, _restoreTotalBytes, _filesRestored, _bytesRestored;
    private volatile int _rehydAvailable, _rehydRehydrated, _rehydNeeds, _rehydPending, _chunksTotal;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ChunkHash, long> _tarUncompressed = new();

    // Forward-only phase ordinal. A job is either archive or restore, and each command drives a disjoint,
    // monotonically-advancing slice of PhaseNames — so the concurrent per-item forwarders that fold into
    // `hash-route`/`upload` (or restore's `download`) can call SetPhase repeatedly and out of order without
    // ever regressing the stepper to an earlier milestone.
    private static readonly string[] PhaseNames =
    {
        "starting",
        "scan", "hash-route", "upload", "snapshot",                     // archive
        "classify", "confirm-cost", "download", "rehydrate", "cleanup", // restore
    };
    private int _phaseOrdinal;   // index into PhaseNames; 0 = "starting"

    private volatile string _status = "running";   // live DB-status mirror; rides along in every snapshot so the client sees status transitions without a reload

    /// <summary>Testable clock seam — tests inject a fixed/stepped clock; production uses real time.</summary>
    internal Func<DateTimeOffset> _now = () => DateTimeOffset.UtcNow;

    public void SetTotals(long files, long bytes) { Interlocked.Exchange(ref _totalFiles, files); Interlocked.Exchange(ref _totalBytes, bytes); }
    /// <summary>Records one scanned file of <paramref name="bytes"/> bytes. The file count rises per call even when
    /// <paramref name="bytes"/> is 0 (pointer-only / thin-archive files have no local binary to read), so the UI can
    /// show "Scanned 0 B, N files" instead of a stuck "0 B".</summary>
    public void AddScanned(long bytes) { Interlocked.Add(ref _scannedBytes, bytes); Interlocked.Increment(ref _scannedFiles); }
    public void AddHashed(long bytes)  => Interlocked.Add(ref _hashedBytes, bytes);
    /// <summary>Reconciles a completed chunk's upload to its full original size. Streaming progress
    /// (<see cref="ReportUploadStreamed"/>) may already have credited most of it; this only tops up any remaining
    /// delta, so the chunk contributes exactly <paramref name="original"/> whether or not streaming was reported
    /// (scripted tests / non-streaming providers still credit the full size here).</summary>
    public void AddUploaded(ChunkHash chunk, long stored, long original) => CreditUpload(chunk, original);
    /// <summary>Accumulates the original (uncompressed) size of each new chunk queued for upload — the
    /// additive "new bytes to upload" total. Independent of the deduped/total subtraction, so it never
    /// underflows for pointer-only-heavy repos. Final once routing completes; 0 until the first queue.</summary>
    public void AddQueuedNew(long originalSize) => Interlocked.Add(ref _queuedNewBytes, originalSize);
    public void AddDeduped(long original) { Interlocked.Add(ref _dedupedBytes, original); Interlocked.Increment(ref _dedupedFiles); }
    public void RememberTar(ChunkHash tarHash, long uncompressed) => _tarUncompressed[tarHash] = uncompressed;
    public void AddUploadedTar(ChunkHash tarHash) { if (_tarUncompressed.TryGetValue(tarHash, out var u)) CreditUpload(tarHash, u); }

    // ── Upload progress crediting (streaming + completion, no double-count) ──────
    private readonly Dictionary<ChunkHash, long> _uploadCredited = new();
    private readonly object _uploadLock = new();

    /// <summary>Streaming upload progress: <paramref name="cumulative"/> is the running total of bytes read from the
    /// source for one chunk/tar (Arius.Core's ProgressStream via <c>ArchiveCommandOptions.CreateUploadProgress</c>).
    /// Crediting only the per-chunk delta makes <see cref="_uploadedBytes"/> rise continuously instead of jumping
    /// when a whole chunk/tar finishes.</summary>
    public void ReportUploadStreamed(ChunkHash chunk, long cumulative) => CreditUpload(chunk, cumulative);

    /// <summary>Advances a chunk's credited-uploaded high-water mark to <paramref name="cumulative"/> and adds the
    /// delta to the aggregate. Monotonic per chunk (a completion reconcile can only top up), so streaming reports and
    /// the terminal completion event compose without double-counting. Serialized because <see cref="Progress{T}"/>
    /// callbacks and the completion forwarder can run on different threadpool threads for the same chunk.</summary>
    private void CreditUpload(ChunkHash chunk, long cumulative)
    {
        lock (_uploadLock)
        {
            var prev = _uploadCredited.TryGetValue(chunk, out var p) ? p : 0L;
            if (cumulative <= prev) return;
            _uploadCredited[chunk] = cumulative;
            Interlocked.Add(ref _uploadedBytes, cumulative - prev);
        }
    }

    public void SetRestoreTotals(long files, long bytes) { Interlocked.Exchange(ref _restoreTotalFiles, files); Interlocked.Exchange(ref _restoreTotalBytes, bytes); }
    /// <summary>Marks one file restored: bumps the file count and reconciles its bytes to <paramref name="size"/>.
    /// Streaming download progress (<see cref="ReportRestoreStreamed"/>) keyed by the same relative path may already
    /// have credited most of it, so this only tops up the remainder — no double-count.</summary>
    public void AddRestored(string key, long size) { Interlocked.Increment(ref _filesRestored); CreditRestore(key, size); }

    // ── Restore(download) progress crediting — mirrors the upload path ───────────
    private readonly Dictionary<string, long> _restoreCredited = new();

    /// <summary>Streaming restore progress: <paramref name="cumulative"/> is the running total of bytes downloaded
    /// for one large file (Arius.Core's <c>RestoreOptions.CreateLargeFileDownloadProgress</c>). Crediting only the
    /// per-file delta makes <see cref="_bytesRestored"/> rise continuously as a large file downloads instead of
    /// jumping only when it finishes.</summary>
    public void ReportRestoreStreamed(string key, long cumulative) => CreditRestore(key, cumulative);

    private void CreditRestore(string key, long cumulative)
    {
        lock (_uploadLock)   // archive & restore never run on the same sink, so the crediting lock is shared
        {
            var prev = _restoreCredited.TryGetValue(key, out var p) ? p : 0L;
            if (cumulative <= prev) return;
            _restoreCredited[key] = cumulative;
            Interlocked.Add(ref _bytesRestored, cumulative - prev);
        }
    }
    public void SetRehydration(int available, int rehydrated, int needs, int pending)
    { _rehydAvailable = available; _rehydRehydrated = rehydrated; _rehydNeeds = needs; _rehydPending = pending; }

    /// <summary>Records the authoritative distinct-chunk total from ChunkResolutionCompleteEvent — the single
    /// denominator for the restore hydration bar.</summary>
    public void SetChunkTotals(int totalChunks) => _chunksTotal = totalChunks;

    /// <summary>Advances the job phase, forward-only: a phase earlier than (or equal to) the current one is
    /// ignored, so the concurrent per-item forwarders that drive `hash-route`/`upload`/`download` can fire in any
    /// order without regressing the stepper. Unknown phase names are ignored — every emitted phase is in
    /// <see cref="PhaseNames"/>.</summary>
    public void SetPhase(string phase)
    {
        var next = Array.IndexOf(PhaseNames, phase);
        if (next < 0) return;
        int cur;
        do
        {
            cur = Volatile.Read(ref _phaseOrdinal);
            if (next <= cur) return;   // never regress; early-out keeps the per-file hot path lock-free
        } while (Interlocked.CompareExchange(ref _phaseOrdinal, next, cur) != cur);
    }
    public void SetStatus(string status) => _status = status;

    // ── Smoothed throughput (EMA over ~1s samples) → a stable ETA ────────────────
    // A job is either archive or restore, never both, so the two progress-byte counters sum into one clock.
    private readonly object _sampleLock = new();
    private double _emaRate;                        // EMA-smoothed bytes/sec
    private DateTimeOffset? _lastSampleAt;
    private long _lastSampleProgress;
    private const double EmaAlpha = 0.2;            // ~5-tick (≈5s) effective smoothing at the 1s cadence

    /// <summary>Fold the latest instantaneous throughput into an EMA. Smoothing stops the ETA swinging on bursty
    /// per-chunk crediting; a no-progress tick HOLDS the rate (rather than decaying it), so a brief between-chunk
    /// gap can't inflate the ETA — only sustained progress moves the estimate.</summary>
    public void SampleForEta(DateTimeOffset now)
    {
        lock (_sampleLock)
        {
            var progress = Interlocked.Read(ref _uploadedBytes) + Interlocked.Read(ref _bytesRestored);
            if (_lastSampleAt is { } last)
            {
                var dt = (now - last).TotalSeconds;
                if (dt > 0)
                {
                    var delta = progress - _lastSampleProgress;
                    if (delta > 0)   // flat tick → hold the rate (a between-chunk gap must not inflate the ETA)
                    {
                        var instant = delta / dt;
                        _emaRate = _emaRate <= 0 ? instant : EmaAlpha * instant + (1 - EmaAlpha) * _emaRate;
                    }
                    _lastSampleAt = now;
                    _lastSampleProgress = progress;
                }
            }
            else
            {
                _lastSampleAt = now;
                _lastSampleProgress = progress;
            }
        }
    }

    private double RateBytesPerSec()
    {
        lock (_sampleLock) return _emaRate;
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
        var rate     = RateBytesPerSec();

        // A job is either archive or restore, never both — pick the active kind's totals/progress
        // so both Pct and EtaSeconds are meaningful whichever kind is running.
        var restoreTotalFiles = Interlocked.Read(ref _restoreTotalFiles);
        var restored          = Interlocked.Read(ref _bytesRestored);
        var restoreTotal      = Interlocked.Read(ref _restoreTotalBytes);
        // Streaming credits stored(compressed) download bytes, which can slightly exceed the original-byte total —
        // cap so the tiles and bar never read over the total (pct is already clamped; this fixes the raw "on disk" value).
        if (restoreTotal > 0 && restored > restoreTotal) restored = restoreTotal;
        var isRestore         = restoreTotal > 0 || restoreTotalFiles > 0;
        var denominator       = isRestore ? restoreTotal : totalNew;
        var progress          = isRestore ? restored     : uploaded;

        // ETA uses a STABLE upload-work estimate so it doesn't read "seconds" early: totalNew (queuedNewBytes) is
        // the exact new-bytes-to-upload but is only discovered incrementally as routing queues chunks, so early on
        // it sits far below the real total (→ absurdly low ETA). max(totalNew, total−deduped) is known from the
        // scan, converges to the truth as routing completes, and the max() avoids the pointer-only underflow that
        // total−deduped alone would hit (deduped can exceed scanned bytes). Restore's total is known up front.
        var etaDenominator = isRestore ? restoreTotal : Math.Max(totalNew, Math.Max(0L, total - deduped));
        long? eta = etaDenominator > 0 && rate > 0 ? (long)Math.Ceiling(Math.Max(0L, etaDenominator - progress) / rate) : null;
        var pct = denominator > 0
            ? (int)Math.Clamp(progress * 100 / denominator, 0, 100)
            : (isRestore && restoreTotalFiles > 0
                ? (int)Math.Clamp(Interlocked.Read(ref _filesRestored) * 100 / restoreTotalFiles, 0, 100)
                // Archive before any upload work is known: reflect scan/hash progress so the ring isn't stuck at 0.
                : (!isRestore && total > 0 ? (int)Math.Clamp(hashed * 100 / total, 0, 100) : 0));

        return new JobSnapshot
        {
            JobId = JobId ?? "",
            Phase = PhaseNames[Volatile.Read(ref _phaseOrdinal)], Status = _status,
            TotalBytes = total, TotalNewBytes = totalNew,
            ScannedBytes = Interlocked.Read(ref _scannedBytes),
            ScannedFiles = Interlocked.Read(ref _scannedFiles),
            HashedBytes  = hashed,
            UploadedBytes = uploaded,
            DedupedBytes = deduped, DedupedFiles = Interlocked.Read(ref _dedupedFiles),
            EtaSeconds = eta, ThroughputBytesPerSec = rate, Pct = pct,
            WarningCount = WarningCount,
            Stats = new Dictionary<string, string>   // legacy stat grid
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
    /// warnings tail, and (restore) the resume params + cost estimate. Archive jobs pass <paramref name="resume"/>
    /// = null and no <paramref name="cost"/>.</summary>
    public PersistedJobState BuildPersistedState(DateTimeOffset now, RestoreResumeState? resume, CostEstimateDto? cost = null) => new()
    {
        Snapshot = BuildSnapshot(now),
        Warnings = Warnings,
        Resume   = resume,
        Cost     = cost,
    };

    /// <summary>Copies the current rehydrated chunk count into <paramref name="resume"/> (used by the poller to
    /// tighten cadence once rehydration has started producing newly-ready chunks). Deliberately excludes
    /// already-available (always-online) chunks — those must not trip the quiet window.</summary>
    public RestoreResumeState WithLiveRehydrationCounts(RestoreResumeState resume) =>
        resume with { RehydratedCount = _rehydRehydrated };
}
