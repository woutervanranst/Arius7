using Arius.Core.Shared.FileSystem;

namespace Arius.Core.Shared.LocalFile;

/// <summary>
/// Detects archive input paths that collide under ordinal case-insensitive comparison.
/// It exists to prevent Arius from publishing snapshots that cannot be restored reliably on case-insensitive filesystems,
/// with responsibility for rejecting conflicting <see cref="RelativePath"/> values before snapshot publication.
/// </summary>
internal static class ArchivePathCollisionValidator
{
    /// <summary>
    /// Validates a sequence of file pairs for case-insensitive relative-path collisions.
    /// </summary>
    public static void Validate(IEnumerable<FilePair> pairs)
    {
        var firstByKey = new Dictionary<string, RelativePath>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in pairs)
        {
            Observe(firstByKey, pair.Path);
        }
    }

    /// <summary>
    /// Records one path and throws if it conflicts with a previously observed path under case-insensitive comparison.
    /// </summary>
    public static void Observe(IDictionary<string, RelativePath> firstByKey, RelativePath path)
    {
        var key = path.ToString();
        if (firstByKey.TryGetValue(key, out var existing) && existing != path)
        {
            throw new InvalidOperationException(
                $"Archive input contains case-insensitive path collisions: '{existing}' and '{path}'.");
        }

        firstByKey[key] = path;
    }
}
