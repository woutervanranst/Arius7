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

    /// <summary>Cancels a live job's token if it is currently registered. Returns whether a live job was found —
    /// a caller uses <c>false</c> to fall back to the parked-job path (mark cancelled in the DB, disarm the poller).</summary>
    public bool CancelLive(string jobId)
    {
        if (_sinks.TryGetValue(jobId, out var sink))
        {
            try { sink.Cts.Cancel(); return true; }
            catch (ObjectDisposedException) { return false; }   // job finished (its finally disposed the CTS) between lookup and cancel
        }
        return false;
    }
}
