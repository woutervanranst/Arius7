using Arius.Core.Shared.FileSystem;

namespace Arius.Core.Shared.LocalFile;

internal static class ArchivePathCollisionValidator
{
    public static void Validate(IEnumerable<FilePair> pairs)
    {
        var firstByKey = new Dictionary<string, RelativePath>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in pairs)
        {
            Observe(firstByKey, pair.Path);
        }
    }

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
