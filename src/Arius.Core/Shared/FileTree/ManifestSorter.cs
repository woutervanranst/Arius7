namespace Arius.Core.Shared.FileTree;

/// <summary>
/// External sort of the manifest file: reads all entries, sorts by path, rewrites.
/// At 500M × ~80 bytes this is ~40 GB; a chunked merge sort would be needed at that
/// scale. For the initial implementation we use an in-process LINQ sort on the file
/// (sufficient for development/testing; the hook is isolated so it can be swapped
/// for a true external sort later).
/// </summary>
public static class ManifestSorter
{
    /// <summary>
    /// Sorts <paramref name="manifestPath"/> in place by path (ordinal ascending).
    /// Returns the same path for convenience.
    /// </summary>
    public static async Task<string> SortAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        // Read all lines
        var lines = await File.ReadAllLinesAsync(manifestPath, cancellationToken);

        // Parse → sort → serialize
        var sorted = lines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(ManifestEntry.Parse)
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToList();

        // Rewrite in place
        await using var writer = new StreamWriter(
            new FileStream(manifestPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true));

        foreach (var entry in sorted)
            await writer.WriteLineAsync(entry.Serialize().AsMemory(), cancellationToken);

        return manifestPath;
    }
}