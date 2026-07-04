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

    public JobSink() { }                                  // inert sink for read providers
    public JobSink(string jobId, IHubContext<JobsHub> hub) { JobId = jobId; _hub = hub; }

    private IClientProxy? Group => JobId is null || _hub is null ? null : _hub.Clients.Group(JobId);

    // ── Messages ────────────────────────────────────────────────────────────
    public void Log(string text, string severity = "meta") => Group?.SendAsync("Log", new { text, severity });
    public void Cost(object estimate) => Group?.SendAsync("CostEstimate", estimate);
    public void Done(string status, string summary) => Group?.SendAsync("Done", new { status, summary });

    // ── Coalesced progress reporting ─────────────────────────────────────────
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

    // ── Byte-weighted aggregate (archive + restore) ─────────────────────────────
    private long _totalFiles, _totalBytes, _scannedBytes, _hashedBytes, _uploadedBytes, _dedupedBytes, _dedupedFiles;
    private long _restoreTotalFiles, _restoreTotalBytes, _filesRestored, _bytesRestored;
    private volatile int _rehydAvailable, _rehydRehydrated, _rehydNeeds, _rehydPending;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ChunkHash, long> _tarUncompressed = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Started, DateTimeOffset? Done)> _stages = new();
    private volatile string _phase = "starting";

    /// <summary>Testable clock seam — tests inject a fixed/stepped clock; production uses real time.</summary>
    internal Func<DateTimeOffset> _now = () => DateTimeOffset.UtcNow;

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
        var totalNew = total > 0 ? Math.Max(0, total - deduped) : 0;
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
                : 0);

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
            RestoreTotalFiles = restoreTotalFiles,
            FilesRestored = Interlocked.Read(ref _filesRestored),
            RestoreTotalBytes = restoreTotal,
            BytesRestored = restored,
            ChunksAvailable = _rehydAvailable,
            ChunksRehydrated = _rehydRehydrated,
            ChunksNeedingRehydration = _rehydNeeds,
            ChunksPending = _rehydPending,
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
}
