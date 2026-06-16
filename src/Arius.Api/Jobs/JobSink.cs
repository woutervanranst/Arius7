using Arius.Api.Hubs;
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

    // ── Aggregate counters (archive + restore) ──────────────────────────────
    private long _totalFiles, _filesHashed, _chunksUploaded, _bytesUploaded, _filesDeduped;
    private long _totalRestore, _filesRestored, _bytesRestored, _chunksToRehydrate;

    public void SetTotalFiles(long n) => Interlocked.Exchange(ref _totalFiles, n);
    public void IncHashed() => Interlocked.Increment(ref _filesHashed);
    public void IncDeduped() => Interlocked.Increment(ref _filesDeduped);
    public void IncUploaded(long stored) { Interlocked.Increment(ref _chunksUploaded); Interlocked.Add(ref _bytesUploaded, stored); }
    public void SetTotalRestore(long n) => Interlocked.Exchange(ref _totalRestore, n);
    public void IncRestored(long size) { Interlocked.Increment(ref _filesRestored); Interlocked.Add(ref _bytesRestored, size); }
    public void SetRehydrating(int n) => Interlocked.Exchange(ref _chunksToRehydrate, n);

    /// <summary>Sends archive progress (pct from hashed/total) + the Files/Uploaded/Deduped/Throughput grid.</summary>
    public void ReportArchive(int? pctOverride = null)
    {
        var total = Math.Max(1, Interlocked.Read(ref _totalFiles));
        var hashed = Interlocked.Read(ref _filesHashed);
        var pct = pctOverride ?? (int)Math.Min(95, hashed * 90 / total);
        Group?.SendAsync("Progress", new
        {
            pct,
            stats = new Dictionary<string, string>
            {
                ["Files"] = $"{hashed}/{Interlocked.Read(ref _totalFiles)}",
                ["Uploaded"] = FormatBytes(Interlocked.Read(ref _bytesUploaded)),
                ["Deduped"] = Interlocked.Read(ref _filesDeduped).ToString(),
                ["Chunks"] = Interlocked.Read(ref _chunksUploaded).ToString(),
            },
        });
    }

    /// <summary>Sends restore progress (pct from restored/total) + the restore stat grid.</summary>
    public void ReportRestore(int? pctOverride = null)
    {
        var total = Math.Max(1, Interlocked.Read(ref _totalRestore));
        var restored = Interlocked.Read(ref _filesRestored);
        var pct = pctOverride ?? (int)Math.Min(95, restored * 90 / total);
        Group?.SendAsync("Progress", new
        {
            pct,
            stats = new Dictionary<string, string>
            {
                ["Restored"] = $"{restored}/{Interlocked.Read(ref _totalRestore)}",
                ["Downloaded"] = FormatBytes(Interlocked.Read(ref _bytesRestored)),
                ["Rehydrating"] = Interlocked.Read(ref _chunksToRehydrate).ToString(),
            },
        });
    }

    private static string FormatBytes(long b)
        => b >= 1_000_000_000 ? $"{b / 1e9:0.00} GB" : b >= 1_000_000 ? $"{b / 1e6:0.0} MB" : b >= 1000 ? $"{b / 1e3:0} KB" : $"{b} B";
}
