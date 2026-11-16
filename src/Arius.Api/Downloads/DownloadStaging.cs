namespace Arius.Api.Downloads;

/// <summary>
/// The per-job staging folders (<c>{root}/{jobId}</c>) that a download job restores into and the browser then
/// downloads from. Public because <c>JobRunner</c>'s public constructor takes it.
/// </summary>
public sealed class DownloadStaging(string root)
{
    public string PathFor(string jobId) => Path.Combine(root, jobId);

    /// <summary>Full path of a staged file; throws when <paramref name="relativePath"/> resolves outside the job folder.</summary>
    public string FilePathFor(string jobId, string relativePath)
    {
        var folder = Path.GetFullPath(PathFor(jobId));
        var full   = Path.GetFullPath(Path.Combine(folder, relativePath));

        if (!full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"Path '{relativePath}' is outside the download folder.", nameof(relativePath));

        return full;
    }

    public bool Exists(string jobId) => Directory.Exists(PathFor(jobId));

    /// <summary>Deletes every file under the job folder whose <c>/</c>-separated relative path is not in <paramref name="readyPaths"/>.</summary>
    public void PruneUnready(string jobId, IReadOnlySet<string> readyPaths)
    {
        var folder = PathFor(jobId);
        if (!Directory.Exists(folder))
            return;

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (!readyPaths.Contains(Path.GetRelativePath(folder, file).Replace('\\', '/')))
                File.Delete(file);
        }
    }

    public void Delete(string jobId)
    {
        var folder = PathFor(jobId);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    public IReadOnlyList<string> ListJobIds() =>
        Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Select(d => Path.GetFileName(d)).ToList()
            : [];

    public long SizeOf(string jobId)
    {
        var folder = PathFor(jobId);
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : 0;
    }
}
