namespace Arius.Core.Shared.FileTree;

internal sealed record StagedChildLink(string DirectoryId, string Name)
{
    public static StagedChildLink Parse(string line)
    {
        ArgumentException.ThrowIfNullOrEmpty(line);

        line = line.TrimEnd('\r');

        var firstSpace = line.IndexOf(' ');
        if (firstSpace < 0)
            throw new FormatException($"Invalid child link (no spaces): '{line}'");

        var directoryId = line[..firstSpace];
        var afterId = line[(firstSpace + 1)..];
        if (afterId.Length < 2 || afterId[0] != 'D' || afterId[1] != ' ')
            throw new FormatException($"Invalid child link (missing type marker): '{line}'");

        var name = afterId[2..];
        if (string.IsNullOrWhiteSpace(name))
            throw new FormatException($"Invalid child link (empty name): '{line}'");

        return new StagedChildLink(directoryId, name);
    }
}
