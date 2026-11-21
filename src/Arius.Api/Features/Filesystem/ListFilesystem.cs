using System.ComponentModel;
using Arius.Api.Shared.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Arius.Api.Features.Filesystem;

/// <summary>
/// <c>GET /fs/list</c>: lists subdirectories for the local-path picker as the Api host sees them (under Docker, the
/// mounted volumes). The picker is server-driven because a repository's local path must resolve on the host, not on
/// the browser's machine.
/// </summary>
internal sealed class ListFilesystem : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/fs/list", (
                [Description("Directory on the server. Defaults to the file system root.")] string? path,
                ListFilesystem endpoint) => endpoint.Handle(path))
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithSummary("List server directories")
            .WithDescription("Lists the subdirectories of a directory on the server, for choosing a repository's local path. Paths are as the server sees them; under Docker, these are the mounted volumes. Returns 400 Bad Request for an invalid path, 404 Not Found if the directory does not exist, and 403 Forbidden if it cannot be read.");

    private Results<Ok<FsListDto>, BadRequest<string>, NotFound<string>, ProblemHttpResult> Handle(string? path)
    {
        var target = string.IsNullOrWhiteSpace(path) ? DefaultRoot() : path.Trim();

        DirectoryInfo directory;
        try
        {
            directory = new DirectoryInfo(target);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return TypedResults.BadRequest($"Invalid path: {ex.Message}");
        }

        if (!directory.Exists)
            return TypedResults.NotFound($"Directory not found: {target}");

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
            return TypedResults.Problem($"Cannot read directory: {ex.Message}", statusCode: StatusCodes.Status403Forbidden);
        }

        return TypedResults.Ok(new FsListDto(directory.FullName, directory.Parent?.FullName, entries));
    }

    private static string DefaultRoot()
        => Path.GetPathRoot(Environment.CurrentDirectory) is { Length: > 0 } root ? root : "/";
}
