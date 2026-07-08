using System.Runtime.CompilerServices;
using Arius.Api.AppData;
using Arius.Api.Composition;
using Arius.Api.Contracts;
using Arius.Api.Jobs;
using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Storage;
using Mediator;
using Microsoft.AspNetCore.SignalR;

namespace Arius.Api.Hubs;

/// <summary>
/// The single Arius realtime hub: repository entry streaming (file browser + time-travel) and the
/// archive/restore job streams with the inline cost-approval handshake. Container discovery and
/// global search are added in later phases.
/// </summary>
public sealed class JobsHub(
    RepositoryProviderRegistry registry,
    AppDatabase database,
    JobRunner jobRunner,
    RestoreApprovalRegistry approvals,
    JobStateRegistry jobStates,
    SecretProtector secrets,
    IBlobServiceFactory blobServiceFactory) : Hub
{
    /// <summary>
    /// Streams the container names in an account (Add-existing wizard). Pass <paramref name="accountId"/>
    /// &gt; 0 to use a configured account's stored key, or 0 with an explicit name + key for a new account.
    /// </summary>
    public async IAsyncEnumerable<string> StreamContainers(
        long accountId,
        string? accountName,
        string? accountKey,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string name;
        string? key;
        if (accountId > 0)
        {
            var account = database.GetAccount(accountId);
            if (account is null) yield break;
            name = account.Name;
            key = secrets.Unprotect(account.EncryptedAccountKey);
        }
        else
        {
            name = accountName ?? string.Empty;
            key = accountKey;
        }

        var blobService = await blobServiceFactory.CreateAsync(name, key, cancellationToken).ConfigureAwait(false);
        await foreach (var container in blobService.GetContainerNamesAsync(cancellationToken).ConfigureAwait(false))
            yield return container;
    }

    /// <summary>Starts an archive; the caller's connection joins the job group before events flow.</summary>
    public async Task<string> StartArchive(long repositoryId, string tier, bool removeLocal, bool writePointers, bool fastHash)
    {
        if (database.HasActiveJob(repositoryId))
            throw new HubException("A job is already running for this repository.");

        var jobId = Guid.NewGuid().ToString();
        await Groups.AddToGroupAsync(Context.ConnectionId, jobId);
        _ = jobRunner.RunArchiveAsync(repositoryId, jobId, tier, removeLocal, writePointers, fastHash);
        return jobId;
    }

    /// <summary>Starts a restore (empty targetPaths = whole repository).</summary>
    public async Task<string> StartRestore(long repositoryId, string? version, string[]? targetPaths, bool overwrite, bool noPointers)
    {
        if (database.HasActiveJob(repositoryId))
            throw new HubException("A job is already running for this repository.");

        var jobId = Guid.NewGuid().ToString();
        await Groups.AddToGroupAsync(Context.ConnectionId, jobId);
        _ = jobRunner.RunRestoreAsync(repositoryId, jobId, Context.ConnectionId, version, targetPaths ?? [], overwrite, noPointers);
        return jobId;
    }

    /// <summary>Joins the job's SignalR group and returns its current state — live from the registry if the job is
    /// running, else reconstructed from persisted state_json for a parked/finished job. Progress deltas follow.</summary>
    public async Task<JobAttachState?> AttachToJob(string jobId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, jobId);

        var job = database.GetJob(jobId);
        if (job is null) return null;

        // A job blocked in the ConfirmRehydration callback (genuinely parked at awaiting-cost, still within the
        // approval window) still has a LIVE sink here — JobRunner's method has not returned, so nothing has
        // removed it from jobStates yet. JobViewResolver reads the cost/resume the run staged on the sink for
        // exactly this case rather than hardcoding null.
        var view = JobViewResolver.Resolve(jobStates, jobId, job.StateJson);
        return new JobAttachState(job.Status, view.Snapshot ?? EmptySnapshot(jobId), view.Cost, view.WarningCount, view.Resume);
    }

    /// <summary>Leaves the job's SignalR group (the client stopped watching it).</summary>
    public Task DetachFromJob(string jobId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, jobId);

    private static JobSnapshot EmptySnapshot(string jobId) => new()
    {
        JobId = jobId, Phase = "unknown",
        TotalBytes = 0, TotalNewBytes = 0, ScannedBytes = 0, HashedBytes = 0, UploadedBytes = 0,
        DedupedBytes = 0, DedupedFiles = 0, EtaSeconds = null, ThroughputBytesPerSec = 0, Pct = 0,
        Stats = new Dictionary<string, string>(), WarningCount = 0,
        RestoreTotalFiles = 0, FilesRestored = 0, RestoreTotalBytes = 0, BytesRestored = 0,
        ChunksAvailable = 0, ChunksRehydrated = 0, ChunksNeedingRehydration = 0, ChunksPending = 0,
        ChunksTotal = 0,
    };

    /// <summary>Requests cancellation of a job. A job parked at the cost prompt is cancelled by resolving its wait as
    /// a decline (the restore run's decline branch marks it <c>cancelled</c>). A LIVE job with no pending approval is
    /// cancelled cooperatively (its token trips at the next checkpoint). A PARKED job (awaiting-cost / rehydrating, no
    /// live run) releases any waiting approval and is marked terminal so the poller skips it and the single-active-job
    /// guard is freed.</summary>
    public Task CancelJob(string jobId)
    {
        // A job parked at the cost prompt is cancelled by resolving its wait as a decline — the restore run's
        // decline branch then marks it `cancelled`. This MUST precede CancelLive: cancelling the CTS would race
        // the approval wait into a *timeout* (park), not a cancel.
        if (approvals.HasPending(jobId)) { approvals.Resolve(jobId, null); return Task.CompletedTask; }
        if (jobStates.CancelLive(jobId)) return Task.CompletedTask;   // mid-run → cooperative CTS cancel
        approvals.Resolve(jobId, null);                               // parked/not-live safety no-op
        jobRunner.CancelParked(jobId);                               // mark cancelled + broadcast Done
        return Task.CompletedTask;
    }

    /// <summary>
    /// Streams cross-repository search hits: runs a recursive filename filter across every repository
    /// (each failure isolated so one unreachable repo doesn't fail the whole search).
    /// </summary>
    public async IAsyncEnumerable<SearchHitDto> SearchAll(string query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) yield break;

        foreach (var repo in database.ListRepositories())
        {
            var hits = new List<SearchHitDto>();
            try
            {
                var provider = await registry.GetReadProviderAsync(repo.Id, cancellationToken);
                var mediator = provider.GetRequiredService<IMediator>();
                await foreach (var entry in mediator.CreateStream(new ListQuery(new ListQueryOptions { Filter = query, Recursive = true }), cancellationToken))
                    if (entry is RepositoryFileEntry file)
                        hits.Add(new SearchHitDto(repo.Id, repo.Alias, EntryMapping.ToDto(file)));
            }
            catch
            {
                // Skip repositories that can't be opened or listed; keep searching the rest.
            }

            foreach (var hit in hits)
                yield return hit;
        }
    }

    /// <summary>Answers the restore cost modal. An <c>awaiting-cost</c> job is always LIVE within a process
    /// lifetime (its run blocks in the approval callback), so this only ever resolves an in-run wait; a restart
    /// reconciles any parked job to <c>interrupted</c> before it could reach here.</summary>
    public Task ApproveRestore(string jobId, string? priority)
    {
        // ApproveRestore is unambiguously "proceed" — DeclineRestore is the separate decline path. An unrecognized
        // or missing priority therefore defaults to Standard (the cheaper tier), never null: a null here would be
        // read by the run's ConfirmRehydration callback as a decline and silently cancel a restore the user meant
        // to approve.
        var chosen = priority?.ToLowerInvariant() == "high" ? RehydratePriority.High : RehydratePriority.Standard;
        if (approvals.HasPending(jobId)) approvals.Resolve(jobId, chosen);   // in-run
        // else: no live approval wait to answer (job already resumed/terminal) — nothing to do.
        return Task.CompletedTask;
    }

    /// <summary>Declines the restore cost modal (equivalent to answering "cancel"). Resolves a live wait, or
    /// marks a parked job cancelled.</summary>
    public async Task DeclineRestore(string jobId)
    {
        if (approvals.HasPending(jobId)) { approvals.Resolve(jobId, null); return; }
        await DeclineParkedAsync(jobId);
    }

    private Task DeclineParkedAsync(string jobId)
    {
        jobRunner.CancelParked(jobId);   // mark cancelled + broadcast Done
        return Task.CompletedTask;
    }

    /// <summary>Toggles auto-resume for a rehydrating restore. OFF stops the poller re-driving it (status stays
    /// rehydrating; the UI shows "≈ hydrated by" + a manual Restore-now). ON re-drives immediately.</summary>
    public async Task SetAutoResume(string jobId, bool autoResume)
    {
        var job = database.GetJob(jobId);
        if (job?.StateJson is null) return;
        PersistedJobState? persisted;
        try { persisted = System.Text.Json.JsonSerializer.Deserialize<PersistedJobState>(job.StateJson); }
        catch (System.Text.Json.JsonException) { return; }
        if (persisted?.Resume is null) return;
        var updated = persisted with { Resume = persisted.Resume with { AutoResume = autoResume } };
        database.SaveJobState(jobId, System.Text.Json.JsonSerializer.Serialize(updated));
        if (autoResume) _ = jobRunner.ResumeRestoreAsync(jobId);
    }

    /// <summary>Manual "Restore now" for a rehydrating restore whose auto-resume is off.</summary>
    public Task ResumeRestore(string jobId)
    {
        _ = jobRunner.ResumeRestoreAsync(jobId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Streams the immediate children (directories + files) of a folder in a snapshot, server → client.
    /// </summary>
    /// <param name="version">Snapshot version (null/empty = latest).</param>
    /// <param name="prefix">Folder path within the repository (null/empty = root).</param>
    /// <param name="filter">Case-insensitive filename substring filter.</param>
    /// <param name="includeLocal">Overlay the repository's local folder onto the listing.</param>
    public async IAsyncEnumerable<EntryDto> StreamEntries(
        long repositoryId,
        string? version,
        string? prefix,
        string? filter,
        bool includeLocal,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var repository = database.GetRepository(repositoryId);
        var provider   = await registry.GetReadProviderAsync(repositoryId, cancellationToken);
        var mediator   = provider.GetRequiredService<IMediator>();

        var options = new ListQueryOptions
        {
            Version   = string.IsNullOrWhiteSpace(version) ? null : version,
            Prefix    = string.IsNullOrWhiteSpace(prefix) ? null : RelativePath.Parse(prefix),
            Filter    = string.IsNullOrWhiteSpace(filter) ? null : filter,
            Recursive = false,
            LocalPath = includeLocal ? repository?.LocalPath : null,
        };

        await foreach (var entry in mediator.CreateStream(new ListQuery(options), cancellationToken))
            yield return EntryMapping.ToDto(entry);
    }
}
