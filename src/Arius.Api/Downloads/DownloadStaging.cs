namespace Arius.Api.Downloads;

/// <summary>
/// The per-job staging folders (<c>{root}/{jobId}</c>) that a download job restores into and the browser then
/// downloads from.
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

    /// <summary>Names of the job staging folders under the root; only GUID-named folders count, so a foreign folder
    /// (e.g. a user's <c>Downloads</c> when the data directory is misplaced) is never listed, counted or cleaned up.</summary>
    public IReadOnlyList<string> ListJobIds() =>
        Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Select(d => Path.GetFileName(d)).Where(name => Guid.TryParse(name, out _)).ToList()
            : [];

    /// <summary>Total size of the job folder's files; files (or the folder) deleted meanwhile by a concurrent clean-up count as 0.</summary>
    public long SizeOf(string jobId)
    {
        var folder = PathFor(jobId);
        if (!Directory.Exists(folder))
            return 0;

        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch (FileNotFoundException) { }
            }
        }
        catch (DirectoryNotFoundException) { }
        return total;
    }
}
