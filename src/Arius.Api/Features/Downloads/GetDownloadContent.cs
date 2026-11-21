using System.ComponentModel;
using System.IO.Compression;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Downloads;
using Arius.Api.Shared.Hosting;
using Microsoft.Net.Http.Headers;

namespace Arius.Api.Features.Downloads;

/// <summary>Serves the staged files: a single file as itself, with range requests, or several files as one streamed ZIP.</summary>
internal sealed class GetDownloadContent(AppDatabase db, DownloadStaging staging) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/jobs/{id}/download/content", (
                string id,
                [Description("`all` for every staged file, or `remaining` for the staged files not fetched yet.")] string? set,
                HttpContext http,
                GetDownloadContent endpoint) => endpoint.HandleAsync(id, set, http))
            .WithSummary("Download staged files")
            .WithDescription("Streams the selected staged files of a download job. A single file is served as itself and supports range requests; several files are streamed as one ZIP archive. Files that are transferred completely are marked as fetched. Returns 400 Bad Request for an invalid `set`, 404 Not Found if the job does not exist, is not a download job or has no matching staged files, and 410 Gone after the staging folder has been cleaned up.");

    private async Task<IResult> HandleAsync(string id, string? set, HttpContext http)
    {
        var job = db.GetDownloadJob(id);
        if (job is null) return Results.NotFound();
        if (set is not ("all" or "remaining")) return Results.BadRequest();
        if (DownloadEndpoints.IsCleanedUp(job, staging)) return Results.StatusCode(StatusCodes.Status410Gone);

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
                && DownloadEndpoints.CountsAsDownloaded(http.RequestAborted.IsCancellationRequested, http.Response.GetTypedHeaders().ContentRange, new FileInfo(path).Length))
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

        // ZIP entries can only store dates from 1980 to 2107.
        static DateTimeOffset ZipTime(DateTime t) =>
            t < new DateTime(1980, 1, 1) ? new DateTime(1980, 1, 1) : t > new DateTime(2107, 12, 31) ? new DateTime(2107, 12, 31) : t;
    }
}
