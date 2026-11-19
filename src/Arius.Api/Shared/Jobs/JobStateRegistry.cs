using System.Collections.Concurrent;

namespace Arius.Api.Shared.Jobs;

/// <summary>The live jobs, keyed by job id. Lets code outside a job's own service provider (attach, REST reads, the
/// rehydration poller) reach a running job's <see cref="JobSink"/>. A job is present only while its run executes;
/// the database row is the durable record.</summary>
public sealed class JobStateRegistry
{
    private readonly ConcurrentDictionary<string, JobSink> _sinks = new();

    public void Register(string jobId, JobSink sink) => _sinks[jobId] = sink;
    public bool TryGet(string jobId, out JobSink sink) => _sinks.TryGetValue(jobId, out sink!);
    public void Remove(string jobId) => _sinks.TryRemove(jobId, out _);
    public IReadOnlyCollection<string> ActiveJobIds => _sinks.Keys.ToArray();

    /// <summary>Cancels a live job's token. Returns <c>false</c> when no live job was found, so the caller can cancel
    /// it as a parked job instead.</summary>
    public bool CancelLive(string jobId)
    {
        if (_sinks.TryGetValue(jobId, out var sink))
        {
            try { sink.Cts.Cancel(); return true; }
            catch (ObjectDisposedException) { return false; }   // the job finished and disposed its CTS between lookup and cancel
        }
        return false;
    }
}
