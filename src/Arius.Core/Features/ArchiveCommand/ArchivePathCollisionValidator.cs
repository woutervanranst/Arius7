using Arius.Core.Shared.FileSystem;

namespace Arius.Core.Features.ArchiveCommand;

/// <summary>
/// Detects archive input paths that collide under ordinal case-insensitive comparison.
/// It exists to prevent Arius from publishing snapshots that cannot be restored reliably on case-insensitive filesystems,
/// with responsibility for rejecting conflicting <see cref="RelativePath"/> values before snapshot publication.
/// </summary>
internal static class ArchivePathCollisionValidator
{
    internal static IEqualityComparer<RelativePath> OrdinalIgnoreCaseComparer { get; } = RelativePathOrdinalIgnoreCaseComparer.Instance;

    /// <summary>
    /// Validates a sequence of file pairs for case-insensitive relative-path collisions.
    /// </summary>
    public static void Validate(IEnumerable<FilePair> pairs)
    {
        var firstByKey = new Dictionary<RelativePath, RelativePath>(OrdinalIgnoreCaseComparer);

        foreach (var pair in pairs)
        {
            Observe(firstByKey, pair.RelativePath);
        }
    }

    /// <summary>
    /// Records one path and throws if it conflicts with a previously observed path under case-insensitive comparison.
    /// </summary>
    public static void Observe(IDictionary<RelativePath, RelativePath> firstByKey, RelativePath path)
    {
        if (firstByKey.TryGetValue(path, out var existing) && existing != path)
        {
            throw new InvalidOperationException(
                $"Archive input contains case-insensitive path collisions: '{existing}' and '{path}'.");
        }

        firstByKey[path] = path;
    }

    private sealed class RelativePathOrdinalIgnoreCaseComparer : IEqualityComparer<RelativePath>
    {
        public static RelativePathOrdinalIgnoreCaseComparer Instance { get; } = new();

        public bool Equals(RelativePath x, RelativePath y) =>
            x.Equals(y, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(RelativePath obj) =>
            obj.GetHashCode(StringComparer.OrdinalIgnoreCase);
    }
}
