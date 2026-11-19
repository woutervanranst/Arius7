using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Filesystem;

/// <summary>
/// Lists subdirectories for the local-path picker as the Api host sees them (under Docker, the mounted volumes).
/// The picker is server-driven because a repository's local path must resolve on the host, not on the browser's
/// machine.
/// </summary>
internal sealed class ListFilesystem : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/fs/list", (string? path, ListFilesystem endpoint) => endpoint.Handle(path));

    private IResult Handle(string? path)
    {
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
    }

    private static string DefaultRoot()
        => Path.GetPathRoot(Environment.CurrentDirectory) is { Length: > 0 } root ? root : "/";
}
