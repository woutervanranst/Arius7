using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;
using Arius.Api.Shared.Hubs;
using Arius.Api.Shared.Jobs;
using Arius.Core.Shared.FileSystem;
using Microsoft.AspNetCore.SignalR;

namespace Arius.Api.Features.Downloads;

/// <summary>Hub <c>StartDownload</c> — starts a download of the collected files from the pinned snapshot version; the
/// files are restored into the job's staging folder for the browser to fetch.</summary>
public sealed class StartDownload(IHubContext<JobsHub> hub, AppDatabase database, JobRunner jobRunner) : IHubHandler
{
    public async Task<string> HandleAsync(string connectionId, long repositoryId, string? version, DownloadFileRequest[]? files)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new HubException("Choose a snapshot to download from.");
        if (files is null || files.Length == 0)
            throw new HubException("Collect at least one file to download.");

        var collected = new List<DownloadFile>();
        var seen = new HashSet<string>();
        foreach (var file in files)
        {
            // The empty path parses as the repository root, which would turn the download into a whole-repository restore.
            if (string.IsNullOrEmpty(file.Path) || !RelativePath.TryParse(file.Path, out var parsed))
                throw new HubException($"'{file.Path}' is not a valid repository path.");

            var path = parsed.ToString();
            if (seen.Add(path))
                collected.Add(new DownloadFile(path, file.Size));
        }

        if (database.HasActiveJob(repositoryId))
            throw new HubException("A job is already running for this repository.");

        var jobId = Guid.NewGuid().ToString();
        await hub.Groups.AddToGroupAsync(connectionId, jobId);
        _ = jobRunner.RunDownloadAsync(repositoryId, jobId, version, collected);
        return jobId;
    }
}
