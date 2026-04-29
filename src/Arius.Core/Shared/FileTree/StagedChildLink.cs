using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.FileTree;

internal readonly record struct StagedChildLink(string DirectoryId, string Name)
{
    public static StagedChildLink Parse(string line)
    {
        ArgumentException.ThrowIfNullOrEmpty(line);

        line = line.TrimEnd('\r');

        var firstSpace = line.IndexOf(' ');
        if (firstSpace < 0)
            throw new FormatException($"Invalid child link (no spaces): '{line}'");

        var directoryId = line[..firstSpace];
        try
        {
            directoryId = HashCodec.NormalizeHex(directoryId);
        }
        catch (FormatException ex)
        {
            throw new FormatException($"Invalid child link (invalid directory id): '{line}'", ex);
        }

        var afterId = line[(firstSpace + 1)..];
        if (afterId.Length < 2 || afterId[0] != 'D' || afterId[1] != ' ')
            throw new FormatException($"Invalid child link (missing type marker): '{line}'");

        var name = afterId[2..];
        if (string.IsNullOrWhiteSpace(name))
            throw new FormatException($"Invalid child link (empty name): '{line}'");

        if (!name.EndsWith("/", StringComparison.Ordinal)
            || name.Length == 1
            || name.Contains('\\')
            || name[..^1].Contains('/')
            || name is "./" or "../"
            || string.IsNullOrWhiteSpace(name[..^1]))
        {
            throw new FormatException($"Invalid child link (non-canonical name): '{line}'");
        }

        return new StagedChildLink(directoryId, name);
    }
}
