using System.IO.Compression;
using Arius.Api.AppData;
using Arius.Api.Contracts;
using Arius.Api.Downloads;
using Microsoft.Net.Http.Headers;

namespace Arius.Api.Endpoints;

/// <summary>A download job's files and serving them from its staging folder (one file as itself, several as one ZIP).</summary>
internal static class DownloadEndpoints
{
    public static void MapDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/jobs/{id}/download", (string id, AppDatabase db, DownloadStaging staging) =>
        {
            var job = db.GetDownloadJob(id);
            if (job is null) return Results.NotFound();

            var cleanedUp = IsCleanedUp(job, staging);
            var settings  = db.GetDownloadSettings();
            DateTimeOffset? cleanupAt = settings.CleanupEnabled && IsTerminal(job) && !cleanedUp
                ? job.FinishedAt?.AddDays(settings.CleanupDays)
                : null;

            var files = db.ListDownloadFiles(id).Select(f => new DownloadFileDto(f.Path, f.Size, f.Ready, f.Downloaded)).ToList();
            return Results.Ok(new DownloadStateDto(files, cleanedUp, cleanupAt));
        });

        app.MapGet("/jobs/{id}/download/content", async (string id, string? set, HttpContext http, AppDatabase db, DownloadStaging staging) =>
        {
            var job = db.GetDownloadJob(id);
            if (job is null) return Results.NotFound();
            if (set is not ("all" or "remaining")) return Results.BadRequest();
            if (IsCleanedUp(job, staging)) return Results.StatusCode(StatusCodes.Status410Gone);

            var all      = db.ListDownloadFiles(id);
            var selected = all.Where(f => f.Ready && (set == "all" || !f.Downloaded)).ToList();
            if (selected.Count == 0) return Results.NotFound();

            if (selected.Count == 1)
            {
                var file = selected[0];
                var path = staging.FilePathFor(id, file.Path);
                await Results.File(path, "application/octet-stream", Path.GetFileName(file.Path), enableRangeProcessing: true).ExecuteAsync(http);

                // A 416 (range past the end) transfers nothing, so only 200/206 can count.
                if (http.Response.StatusCode is StatusCodes.Status200OK or StatusCodes.Status206PartialContent
                    && CountsAsDownloaded(http.RequestAborted.IsCancellationRequested, http.Request.GetTypedHeaders().Range, new FileInfo(path).Length))
                    db.MarkDownloadFilesDownloaded(id, [file.Path]);
                return Results.Empty;
            }

            var alias = db.GetRepository(job.RepositoryId)?.Alias ?? "download";
            var archiveName = DownloadNaming.ArchiveName(
                alias, job.StartedAt ?? DateTimeOffset.UtcNow, selected.Count, all.Count,
                remaining: set == "remaining" && all.Any(f => f.Downloaded));

            http.Response.ContentType = "application/zip";
            var disposition = new ContentDispositionHeaderValue("attachment");
            disposition.SetHttpFileName(archiveName);
            http.Response.Headers.ContentDisposition = disposition.ToString();

            await using (var zip = await ZipArchive.CreateAsync(http.Response.Body, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, http.RequestAborted))
            {
                foreach (var file in selected)
                {
                    var path  = staging.FilePathFor(id, file.Path);
                    var entry = zip.CreateEntry(file.Path, CompressionLevel.NoCompression);
                    entry.LastWriteTime = ZipTime(File.GetLastWriteTime(path));

                    await using var entryStream = await entry.OpenAsync(http.RequestAborted);
                    await using var source      = File.OpenRead(path);
                    await source.CopyToAsync(entryStream, http.RequestAborted);
                }
            }
            await http.Response.Body.FlushAsync(http.RequestAborted);

            if (!http.RequestAborted.IsCancellationRequested)
                db.MarkDownloadFilesDownloaded(id, selected.Select(f => f.Path).ToList());

            return Results.Empty;

            // ZIP can only store 1980-2107.
            static DateTimeOffset ZipTime(DateTime t) =>
                t < new DateTime(1980, 1, 1) ? new DateTime(1980, 1, 1) : t > new DateTime(2107, 12, 31) ? new DateTime(2107, 12, 31) : t;
        });

        app.MapPost("/jobs/{id}/download/cleanup", (string id, AppDatabase db, DownloadStaging staging) =>
        {
            var job = db.GetDownloadJob(id);
            if (job is null) return Results.NotFound();
            if (!IsTerminal(job)) return Results.Conflict();

            staging.Delete(id);
            return Results.NoContent();
        });

        app.MapGet("/repos/{id:long}/downloads/pending", (long id, AppDatabase db, DownloadStaging staging) =>
        {
            foreach (var job in db.ListDownloadJobs(id))
            {
                if (IsCleanedUp(job, staging) || !IsTerminal(job)) continue;

                var files     = db.ListDownloadFiles(job.Id);
                var remaining = files.Where(f => f.Ready && !f.Downloaded).ToList();
                if (remaining.Count == 0) continue;

                return Results.Ok(new PendingDownloadDto(job.Id, files.Count(f => f.Ready), files.Count, remaining.Count, remaining.Sum(f => f.Size)));
            }
            return Results.NoContent();
        });
    }

    /// <summary>Whether a finished transfer counts as the file being downloaded: never when aborted; a ranged request only
    /// when some range reaches the last byte.</summary>
    internal static bool CountsAsDownloaded(bool aborted, RangeHeaderValue? range, long length) =>
        !aborted && (range is null || range.Ranges.Any(r => r.To is null || r.To >= length - 1));

    private static bool IsTerminal(JobRecord job) => JobStatuses.Terminal.Contains(job.Status);

    private static bool IsCleanedUp(JobRecord job, DownloadStaging staging) => IsTerminal(job) && !staging.Exists(job.Id);

    /// <summary>The job, only when it exists and is a download — the staging helper trusts its job id, so every route resolves it here first.</summary>
    private static JobRecord? GetDownloadJob(this AppDatabase db, string id) =>
        db.GetJob(id) is { Kind: "download" } job ? job : null;
}
