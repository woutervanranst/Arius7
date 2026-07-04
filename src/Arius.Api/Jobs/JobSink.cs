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

    // ── Byte-weighted aggregate (archive + restore) ─────────────────────────────
    private long _totalFiles, _totalBytes, _scannedBytes, _hashedBytes, _uploadedBytes, _dedupedBytes, _dedupedFiles;
    private long _restoreTotalFiles, _restoreTotalBytes, _filesRestored, _bytesRestored;
    private int  _rehydAvailable, _rehydRehydrated, _rehydNeeds, _rehydPending;
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

    // ── Sliding-window ETA (archive + restore share the same uploaded-bytes clock) ──────────────
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

    // ── Snapshot builder ─────────────────────────────────────────────────────
    public JobSnapshot BuildSnapshot(DateTimeOffset now)
    {
        var total    = Interlocked.Read(ref _totalBytes);
        var deduped  = Interlocked.Read(ref _dedupedBytes);
        var uploaded = Interlocked.Read(ref _uploadedBytes);
        var totalNew = total > 0 ? Math.Max(0, total - deduped) : 0;
        var rate     = RateBytesPerSec(now);
        long? eta    = totalNew > 0 && rate > 0 ? (long)Math.Ceiling(Math.Max(0L, totalNew - uploaded) / rate) : null;
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
}
