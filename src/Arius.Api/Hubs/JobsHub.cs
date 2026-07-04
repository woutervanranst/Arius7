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

    /// <summary>Requests cancellation of a live job (cooperative — takes effect at the next checkpoint). The
    /// parked-job path (mark cancelled, disarm poller/approval) is handled by the richer wiring added alongside
    /// approve/decline and auto-resume; for a live job this cancels its token.</summary>
    public Task CancelJob(string jobId)
    {
        jobStates.CancelLive(jobId);
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

    /// <summary>Answers the restore cost modal for a LIVE, in-run approval wait: "standard"/"high" to proceed,
    /// anything else to decline. The parked/restart fallback (re-trigger a fresh run) is <see cref="ApproveRestore"/>.</summary>
    public void ApproveRestore(string jobId, string? priority)
    {
        RehydratePriority? chosen = priority?.ToLowerInvariant() switch
        {
            "standard" => RehydratePriority.Standard,
            "high"     => RehydratePriority.High,
            _          => null,
        };

        if (approvals.HasPending(jobId))
        {
            approvals.Resolve(jobId, chosen);   // in-run: feeds back into the same RestoreCommand
            return;
        }
        // Parked (timed out / restarted): re-trigger handled in Task 6 (ResumeRestoreAsync). Until then this is
        // a no-op for a non-live job; Task 6 replaces this branch with the re-trigger call.
    }

    /// <summary>Declines the restore cost modal (equivalent to answering "cancel").</summary>
    public void DeclineRestore(string jobId)
    {
        if (approvals.HasPending(jobId)) { approvals.Resolve(jobId, null); return; }
        // Parked decline is completed in Task 6 (mark cancelled + disarm).
    }

    /// <summary>Back-compat alias for the current Angular drawer; delegates to <see cref="ApproveRestore"/>.
    /// Removed when the drawer is reworked in Plan 3.</summary>
    public void Approve(string jobId, string? priority) => ApproveRestore(jobId, priority);

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
