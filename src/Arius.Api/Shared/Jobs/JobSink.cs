using Arius.Api.Features.Jobs;
using Arius.Api.Shared.Hubs;
using Arius.Core.Shared.Hashes;
using Microsoft.AspNetCore.SignalR;

namespace Arius.Api.Shared.Jobs;

/// <summary>
/// A job's channel to its clients and its progress counters. Registered as a singleton in the job's own service
/// provider, so the notification forwarders and progress callbacks resolve this job's sink: events are isolated
/// by provider, not by a correlation id. Read providers get an inert sink without a <see cref="JobId"/>, on which
/// the forwarders have no effect.
/// </summary>
public sealed class JobSink
{
    private readonly IHubContext<JobsHub>? _hub;
    // ETA diagnostics logger; null on an inert sink and until attached.
    // Volatile: attached on the job-start thread, read on the reporting timer's thread.
    private volatile ILogger? _logger;

    /// <summary>The job id, which is also its SignalR group; null for an inert sink.</summary>
    public string? JobId { get; }

    /// <summary>Cancelled by <see cref="CancelJob"/>; <see cref="JobRunner"/> passes its token to the Core command.</summary>
    public CancellationTokenSource Cts { get; } = new();

    public JobSink() { }                                  // inert sink for read providers
    public JobSink(string jobId, IHubContext<JobsHub>? hub, ILogger? logger = null) { JobId = jobId; _hub = hub; _logger = logger; }

    /// <summary>Attaches the repository's logger so the <c>[ETA]</c> trace lands in the repository log next to
    /// Core's events. Set after construction because the sink exists before the repository's provider and its
    /// logger factory.</summary>
    public void AttachDiagnosticsLogger(ILogger logger) => _logger = logger;

    private IClientProxy? Group => JobId is null || _hub is null ? null : _hub.Clients.Group(JobId);

    public void Log(string text, string severity = "meta")
    {
        if (severity is "warn" or "error")
            CaptureWarning(text);
    }
    /// <summary>The last cost estimate sent by <see cref="Cost"/>, kept so a client reattaching while the run awaits
    /// approval can show the cost prompt. Cleared by <see cref="ClearPending"/>.</summary>
    public CostEstimateDto? PendingCost { get; private set; }

    /// <summary>The resume settings (auto-resume and rehydration window) shown with <see cref="PendingCost"/> to a
    /// reattaching client.</summary>
    public RestoreResumeState? PendingResume { get; private set; }

    public void Cost(CostEstimateDto estimate) { PendingCost = estimate; Group?.SendAsync("CostEstimate", estimate); }

    public void SetPendingResume(RestoreResumeState resume) => PendingResume = resume;

    /// <summary>Clears the pending cost and resume settings once the prompt is approved, so a client reattaching
    /// mid-restore does not see a stale cost prompt.</summary>
    public void ClearPending() { PendingCost = null; PendingResume = null; }

    private volatile bool _done;
    /// <summary>Whether <see cref="Done"/> has been sent; no progress is sent after it.</summary>
    public bool IsDone => _done;

    /// <summary>Serializes <see cref="Done"/> against progress sends, so a stale <c>Progress</c> never follows
    /// <c>Done</c> to the client. Never held across <see cref="BuildSnapshot"/>, so it cannot nest with
    /// <see cref="_sampleLock"/> or <see cref="_warnLock"/>.</summary>
    private readonly object _emitLock = new();

    public void Done(string status, string summary, string? outcomeJson = null)
    {
        lock (_emitLock)
        {
            _done = true;
            Group?.SendAsync("Done", new { jobId = JobId, status, summary, outcome = outcomeJson });
        }
    }

    // Warn/error lines, kept verbatim in a bounded ring.
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

    /// <summary>Total warn/error lines, including those trimmed from <see cref="Warnings"/>.</summary>
    public int WarningCount { get { lock (_warnLock) return _warningCount; } }

    /// <summary>The last 200 warn/error lines at most, oldest first.</summary>
    public IReadOnlyList<string> Warnings { get { lock (_warnLock) return _warnings.ToArray(); } }

    private Timer? _timer;

    /// <summary>Starts sending progress and sampling throughput once per second. No-op for an inert sink.</summary>
    public void StartReporting()
    {
        if (JobId is null) return;
        // One snapshot per tick feeds both the client and the log, so the logged line matches what the client received.
        _timer = new Timer(_ =>
        {
            var now = _now();
            SampleForEta(now);
            var snapshot = BuildSnapshot(now);
            Emit(snapshot);
            LogEtaDiagnostics(snapshot);
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void StopReporting() { _timer?.Dispose(); _timer = null; EmitNow(); }

    /// <summary>Logs the snapshot sent to the client at Debug under the <c>[ETA]</c> tag, so what the web UI rendered
    /// can be reproduced from the log.</summary>
    internal void LogEtaDiagnostics(JobSnapshot snap)
    {
        if (_logger is null || JobId is null || !_logger.IsEnabled(LogLevel.Debug)) return;
        _logger.LogDebug(
            "[ETA] job={JobId} phase={Phase} status={Status} pct={Pct} eta={EtaSeconds}s provisional={EtaIsProvisional} tp={ThroughputBytesPerSec:F0}B/s hashTp={HashThroughputBytesPerSec:F0}B/s upTp={UploadThroughputBytesPerSec:F0}B/s warnings={WarningCount}"
            + " | archive total={TotalBytes} totalNew={TotalNewBytes} scanned={ScannedBytes}/{ScannedFiles}f hashed={HashedBytes} uploaded={UploadedBytes} deduped={DedupedBytes}/{DedupedFiles}f"
            + " | restore restoreTotal={RestoreTotalBytes}/{RestoreTotalFiles}f restored={BytesRestored}/{FilesRestored}f chunksTotal={ChunksTotal} avail={ChunksAvailable} rehyd={ChunksRehydrated} needs={ChunksNeedingRehydration} pending={ChunksPending}",
            snap.JobId, snap.Phase, snap.Status, snap.Pct, snap.EtaSeconds, snap.EtaIsProvisional, snap.ThroughputBytesPerSec, snap.HashThroughputBytesPerSec, snap.UploadThroughputBytesPerSec, snap.WarningCount,
            snap.TotalBytes, snap.TotalNewBytes, snap.ScannedBytes, snap.ScannedFiles, snap.HashedBytes, snap.UploadedBytes, snap.DedupedBytes, snap.DedupedFiles,
            snap.RestoreTotalBytes, snap.RestoreTotalFiles, snap.BytesRestored, snap.FilesRestored,
            snap.ChunksTotal, snap.ChunksAvailable, snap.ChunksRehydrated, snap.ChunksNeedingRehydration, snap.ChunksPending);
    }

    /// <summary>Sends the current snapshot immediately, e.g. on a status transition. Does nothing after
    /// <see cref="Done"/>, so a client never receives progress for a finished job.</summary>
    public void EmitNow()
    {
        if (_done) return;
        Emit(BuildSnapshot(_now()));             // built outside the lock: a slightly stale snapshot is harmless
    }

    private void Emit(JobSnapshot snapshot)
    {
        lock (_emitLock)
        {
            if (_done) return;                   // re-check under the lock: a concurrent Done wins
            Group?.SendAsync("Progress", snapshot);
        }
    }

    private long _totalFiles, _totalBytes, _scannedBytes, _scannedFiles, _hashedBytes, _uploadedBytes, _dedupedBytes, _dedupedFiles, _queuedNewBytes, _newByteTotal;
    private volatile bool _newByteTotalFinal;   // set once RoutingCompleteEvent has fixed the exact new-byte total
    private long _restoreTotalFiles, _restoreTotalBytes, _filesRestored, _bytesRestored;
    private volatile int _rehydAvailable, _rehydRehydrated, _rehydNeeds, _rehydPending, _chunksTotal;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ChunkHash, long> _tarUncompressed = new();

    // The phases in order; archive and restore each advance through their own slice.
    private static readonly string[] PhaseNames =
    {
        "starting",
        "scan", "hash-route", "upload", "snapshot",                     // archive
        "classify", "confirm-cost", "download", "rehydrate", "cleanup", // restore
    };
    private int _phaseOrdinal;

    private volatile string _status = "running";   // mirrors the job's database status

    /// <summary>Clock seam for tests.</summary>
    internal Func<DateTimeOffset> _now = () => DateTimeOffset.UtcNow;

    public void SetTotals(long files, long bytes) { Interlocked.Exchange(ref _totalFiles, files); Interlocked.Exchange(ref _totalBytes, bytes); }
    /// <summary>Records one scanned file. The file count rises even when <paramref name="bytes"/> is 0 (a pointer-only
    /// file has no local binary), so scanning progress stays visible.</summary>
    public void AddScanned(long bytes) { Interlocked.Add(ref _scannedBytes, bytes); Interlocked.Increment(ref _scannedFiles); }
    public void AddHashed(long bytes)  => Interlocked.Add(ref _hashedBytes, bytes);
    /// <summary>Credits a completed chunk at its full <paramref name="original"/> size, topping up what
    /// <see cref="ReportUploadStreamed"/> already credited, so the chunk counts once whether or not streaming
    /// progress was reported.</summary>
    public void AddUploaded(ChunkHash chunk, long stored, long original) => CreditUpload(chunk, original);
    /// <summary>Adds the original size of a new chunk queued for upload; the running sum is the provisional
    /// new-byte total.</summary>
    public void AddQueuedNew(long originalSize) => Interlocked.Add(ref _queuedNewBytes, originalSize);
    /// <summary>Sets the exact new-byte total from <c>RoutingCompleteEvent</c>. Until then the upload ETA uses the
    /// growing <see cref="_queuedNewBytes"/>.</summary>
    public void SetNewByteTotal(long newByteTotal) { Interlocked.Exchange(ref _newByteTotal, newByteTotal); _newByteTotalFinal = true; }
    public void AddDeduped(long original) { Interlocked.Add(ref _dedupedBytes, original); Interlocked.Increment(ref _dedupedFiles); }
    public void RememberTar(ChunkHash tarHash, long uncompressed) => _tarUncompressed[tarHash] = uncompressed;
    public void AddUploadedTar(ChunkHash tarHash) { if (_tarUncompressed.TryGetValue(tarHash, out var u)) CreditUpload(tarHash, u); }

    private readonly Dictionary<ChunkHash, long> _uploadCredited = new();
    private readonly object _uploadLock = new();

    /// <summary>Credits streaming upload progress: <paramref name="cumulative"/> is the bytes read so far for one
    /// chunk or tar, so uploaded bytes rise continuously instead of once per finished chunk.</summary>
    public void ReportUploadStreamed(ChunkHash chunk, long cumulative) => CreditUpload(chunk, cumulative);

    /// <summary>Raises the chunk's credited high-water mark to <paramref name="cumulative"/> and adds the difference to
    /// the total, so streaming reports and the completion event never double-count. Locked because
    /// <see cref="Progress{T}"/> callbacks and the completion forwarder can run concurrently for the same chunk.</summary>
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
    /// <summary>Marks one file restored and credits it at <paramref name="size"/>, topping up what
    /// <see cref="ReportRestoreStreamed"/> already credited for the same path.</summary>
    public void AddRestored(string key, long size) { Interlocked.Increment(ref _filesRestored); CreditRestore(key, size); FileRestored?.Invoke(key); }

    /// <summary>Raised with the relative path of each restored file; a download job uses it to mark the file ready.</summary>
    public Action<string>? FileRestored { get; set; }

    private readonly Dictionary<string, long> _restoreCredited = new();

    /// <summary>Credits streaming download progress: <paramref name="cumulative"/> is the bytes downloaded so far for
    /// one large file, so restored bytes rise continuously while it downloads.</summary>
    public void ReportRestoreStreamed(string key, long cumulative) => CreditRestore(key, cumulative);

    private void CreditRestore(string key, long cumulative)
    {
        lock (_uploadLock)   // archive and restore never share a sink, so one lock serves both
        {
            var prev = _restoreCredited.TryGetValue(key, out var p) ? p : 0L;
            if (cumulative <= prev) return;
            _restoreCredited[key] = cumulative;
            Interlocked.Add(ref _bytesRestored, cumulative - prev);
        }
    }
    public void SetRehydration(int available, int rehydrated, int needs, int pending)
    { _rehydAvailable = available; _rehydRehydrated = rehydrated; _rehydNeeds = needs; _rehydPending = pending; }

    /// <summary>Sets the distinct-chunk total from <c>ChunkResolutionCompleteEvent</c>, the denominator of the restore
    /// hydration bar.</summary>
    public void SetChunkTotals(int totalChunks) => _chunksTotal = totalChunks;

    /// <summary>Advances the job phase. An earlier or unknown phase is ignored, so concurrent per-item forwarders can
    /// report phases in any order without moving the stepper back.</summary>
    public void SetPhase(string phase)
    {
        var next = Array.IndexOf(PhaseNames, phase);
        if (next < 0) return;
        int cur;
        do
        {
            cur = Volatile.Read(ref _phaseOrdinal);
            if (next <= cur) return;   // early return keeps the per-file hot path lock-free
        } while (Interlocked.CompareExchange(ref _phaseOrdinal, next, cur) != cur);
    }
    public void SetStatus(string status) => _status = status;

    // Throughput is smoothed with two exponential moving averages: local hashing, and transfer (upload for
    // archive, download for restore). Their time constant is a tenth of the elapsed time, clamped to
    // [3 s, 10 min], so a short job stays responsive while a long job settles.
    private readonly object _sampleLock = new();
    private double _transferRate;                   // bytes/s uploaded or downloaded
    private double _hashRate;                        // bytes/s hashed
    private DateTimeOffset? _startedAt;              // time of the first sample
    private DateTimeOffset? _lastSampleAt;
    private long _lastTransferProgress;
    private long _lastHashProgress;
    private const double EmaTauElapsedFraction = 0.1;
    private const double EmaTauMinSeconds      = 3.0;
    private const double EmaTauMaxSeconds      = 600.0;

    /// <summary>Folds the progress since the last sample into both rates. A tick without progress keeps the rate
    /// instead of decaying it, so a gap between chunks does not inflate the ETA.</summary>
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

    /// <summary>The first positive sample seeds the rate directly, so it is not damped from zero.</summary>
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

    public JobSnapshot BuildSnapshot(DateTimeOffset now)
    {
        var total    = Interlocked.Read(ref _totalBytes);
        var deduped  = Interlocked.Read(ref _dedupedBytes);
        var uploaded = Interlocked.Read(ref _uploadedBytes);
        var hashed   = Interlocked.Read(ref _hashedBytes);
        // New bytes to upload are the sum of queued new-chunk sizes, not total minus deduped, which underflows when
        // pointer-only files (scanned as 0 bytes) are deduplicated at full size.
        var totalNew = Interlocked.Read(ref _queuedNewBytes);
        var (transferRate, hashRate) = Rates();

        // A job is either archive or restore; Pct and EtaSeconds use the active kind's totals.
        var restoreTotalFiles = Interlocked.Read(ref _restoreTotalFiles);
        var restored          = Interlocked.Read(ref _bytesRestored);
        var restoreTotal      = Interlocked.Read(ref _restoreTotalBytes);
        // Streaming credits stored (compressed) bytes, which can slightly exceed the original-byte total; cap so the
        // UI never shows more than the total.
        if (restoreTotal > 0 && restored > restoreTotal) restored = restoreTotal;
        var isRestore         = restoreTotal > 0 || restoreTotalFiles > 0;

        // The ETA is the slower of two concurrent constraints:
        //  - upload: remaining new bytes / transfer rate. Until RoutingCompleteEvent fixes the exact total, the
        //    queued-new total serves as a lower bound.
        //  - hashing (archive only): remaining bytes to hash / hash rate, so a fully deduplicated archive reports
        //    its real, hash-bound time.
        // Restore has only the download term. An archive has no ETA until the scan completes (total == 0), because
        // a partial new-byte total would underestimate.
        var newByteTotalFinal = _newByteTotalFinal;
        var uploadDenom       = newByteTotalFinal ? Interlocked.Read(ref _newByteTotal) : totalNew;

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
            eta = null;                        // still scanning
        }
        else
        {
            long? uploadEta = transferRate > 0 ? (long)Math.Ceiling(Math.Max(0L, uploadDenom - uploaded) / transferRate) : null;
            long? hashEta   = hashRate     > 0 ? (long)Math.Ceiling(Math.Max(0L, total - hashed)         / hashRate)     : null;
            // The slower constraint wins; ties go to upload, so a finished job does not report a stale hash term.
            eta = (hashEta is { } h && (uploadEta is not { } u || h > u)) ? hashEta : uploadEta;
            etaIsProvisional = eta is not null && !newByteTotalFinal;
        }

        // Each reported rate is its smoothed rate while that stream is active and 0 otherwise, so a finished stream
        // never shows a stale rate. Hashing counts as active during the scan.
        double hashTp, uploadTp;
        if (isRestore)
        {
            hashTp   = 0;                                                                 // restore never hashes
            uploadTp = restoreTotal > 0 && restored < restoreTotal ? transferRate : 0;   // the download uses the transfer rate
        }
        else
        {
            var hashLive   = total == 0 || hashed < total;
            var uploadLive = uploadDenom > 0 && uploaded < uploadDenom;
            hashTp   = hashLive   ? hashRate     : 0;
            uploadTp = uploadLive ? transferRate : 0;
        }
        var dominantTp = Math.Max(hashTp, uploadTp);
        // Archive Pct is (uploaded + deduplicated) / scanned total, matching the job page's progress bar. The clamp
        // covers pointer-only files, whose deduplicated size can exceed the total.
        var pct = isRestore
            ? (restoreTotal > 0
                ? (int)Math.Clamp(restored * 100 / restoreTotal, 0, 100)
                : restoreTotalFiles > 0
                    ? (int)Math.Clamp(Interlocked.Read(ref _filesRestored) * 100 / restoreTotalFiles, 0, 100)
                    : 0)
            : total > 0 ? (int)Math.Clamp((uploaded + deduped) * 100 / total, 0, 100) : 0;

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
            EtaSeconds = eta, ThroughputBytesPerSec = dominantTp,
            HashThroughputBytesPerSec = hashTp, UploadThroughputBytesPerSec = uploadTp,
            Pct = pct, EtaIsProvisional = etaIsProvisional,
            WarningCount = WarningCount,
            Stats = new Dictionary<string, string>   // job drawer stat grid
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

    /// <summary>Builds the summary persisted to <c>jobs.outcome</c> when the job ends. The fields of the other job
    /// kind are null or zero.</summary>
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

    public PersistedJobState BuildPersistedState(DateTimeOffset now, RestoreResumeState? resume, CostEstimateDto? cost = null) => new()
    {
        Snapshot = BuildSnapshot(now),
        Warnings = Warnings,
        Resume   = resume,
        Cost     = cost,
    };

    /// <summary>Copies the live <see cref="RestoreResumeState.RehydratedCount"/> into <paramref name="resume"/>.</summary>
    public RestoreResumeState WithLiveRehydrationCounts(RestoreResumeState resume) =>
        resume with { RehydratedCount = _rehydRehydrated };
}
