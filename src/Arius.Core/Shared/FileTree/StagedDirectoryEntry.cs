using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.FileTree;

internal sealed record StagedDirectoryEntry : FileTreeEntry
{
    public required string DirectoryId { get; init; }

    public static StagedDirectoryEntry Parse(string line)
    {
        ArgumentException.ThrowIfNullOrEmpty(line);

        line = line.TrimEnd('\r');

        var firstSpace = line.IndexOf(' ');
        if (firstSpace < 0)
            throw new FormatException($"Invalid staged directory entry (no spaces): '{line}'");

        var directoryId = line[..firstSpace];
        try
        {
            directoryId = HashCodec.NormalizeHex(directoryId);
        }
        catch (FormatException ex)
        {
            throw new FormatException($"Invalid staged directory entry (invalid directory id): '{line}'", ex);
        }

        var afterId = line[(firstSpace + 1)..];
        if (afterId.Length < 2 || afterId[0] != 'D' || afterId[1] != ' ')
            throw new FormatException($"Invalid staged directory entry (missing type marker): '{line}'");

        var name = afterId[2..];
        if (string.IsNullOrWhiteSpace(name))
            throw new FormatException($"Invalid staged directory entry (empty name): '{line}'");

        if (!name.EndsWith("/", StringComparison.Ordinal)
            || name.Length == 1
            || name.Contains('\\')
            || name[..^1].Contains('/')
            || name is "./" or "../"
            || string.IsNullOrWhiteSpace(name[..^1]))
        {
            throw new FormatException($"Invalid staged directory entry (non-canonical name): '{line}'");
        }

        return new StagedDirectoryEntry
        {
            DirectoryId = directoryId,
            Name        = name
        };
    }
}
