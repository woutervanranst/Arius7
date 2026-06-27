using Arius.Api.Contracts;

namespace Arius.Api.Endpoints;

/// <summary>
/// Server-side directory browsing for the local-path picker. Lists directories <b>as the Arius.Api
/// host/container sees them</b> — under Docker these are the mounted volumes; in dev they are real
/// folders on the machine running the API. The stored local path must resolve here (not on the
/// browser's machine), which is why the picker is server-driven.
/// </summary>
internal static class FilesystemEndpoints
{
    public static void MapFilesystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/fs/list", (string? path) =>
        {
            // Default to the filesystem root the API can see; the client navigates from there.
            var target = string.IsNullOrWhiteSpace(path) ? DefaultRoot() : path.Trim();

            DirectoryInfo directory;
            try
            {
                directory = new DirectoryInfo(target);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
            {
                return Results.BadRequest($"Invalid path: {ex.Message}");
            }

            if (!directory.Exists)
                return Results.NotFound($"Directory not found: {target}");

            List<FsEntryDto> entries;
            try
            {
                entries = directory.EnumerateDirectories()
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(d => new FsEntryDto(d.Name, d.FullName))
                    .ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return Results.Problem($"Cannot read directory: {ex.Message}", statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Ok(new FsListDto(directory.FullName, directory.Parent?.FullName, entries));
        });
    }

    private static string DefaultRoot()
        => Path.GetPathRoot(Environment.CurrentDirectory) is { Length: > 0 } root ? root : "/";
}
