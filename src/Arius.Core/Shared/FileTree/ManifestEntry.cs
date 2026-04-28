using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.FileTree;

/// <summary>
/// One entry written to the manifest temp file during the archive pipeline.
/// Format (tab-separated, one line each): <c>path\thash\tcreated\tmodified\n</c>
/// <para>
/// <c>path</c> is always forward-slash-normalized and relative to the archive root.
/// <c>hash</c> is the content-hash (hex).
/// Timestamps are ISO-8601 round-trip ("O"), UTC.
/// </para>
/// </summary>
public sealed record ManifestEntry(
    string         Path,
    ContentHash    ContentHash,
    DateTimeOffset Created,
    DateTimeOffset Modified)
{
    private const char Sep = '\t';

    /// <summary>Serializes to a manifest line (no trailing newline).</summary>
    public string Serialize() =>
        $"{Path}{Sep}{ContentHash}{Sep}{Created:O}{Sep}{Modified:O}";

    /// <summary>Parses a manifest line. Throws on invalid input.</summary>
    public static ManifestEntry Parse(string line)
    {
        var parts = line.Split(Sep);
        if (parts.Length != 4)
            throw new FormatException($"Invalid manifest line (expected 4 tab-separated fields): '{line}'");

        return new ManifestEntry(
            Path        : parts[0],
            ContentHash : ContentHash.Parse(parts[1]),
            Created     : DateTimeOffset.Parse(parts[2]),
            Modified    : DateTimeOffset.Parse(parts[3]));
    }
}
