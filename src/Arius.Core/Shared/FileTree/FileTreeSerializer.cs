using System.Globalization;
using System.Text;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.FileTree;

/// <summary>
/// Serializes and deserializes canonical filetree node content.
/// Persisted nodes and staged nodes use different line formats, so parsing APIs are named by format.
/// </summary>
public static class FileTreeSerializer
{
    private static readonly Encoding s_utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static IReadOnlyList<FileTreeEntry> Deserialize(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var text = s_utf8.GetString(bytes);
        return ParsePersistedLines(text.Split('\n'));
    }

    public static byte[] Serialize(IReadOnlyList<FileTreeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var sb = new StringBuilder();

        foreach (var entry in entries.OrderBy(e => e.Name.ToString(), StringComparer.Ordinal))
        {
            if (entry is FileEntry fileEntry)
                sb.AppendLine(SerializePersistedFileEntryLine(fileEntry));
            else if (entry is DirectoryEntry directoryEntry)
                sb.AppendLine(SerializePersistedDirectoryEntryLine(directoryEntry));
            else
                throw new InvalidOperationException($"Unsupported file tree entry type: {entry.GetType().Name}");
        }

        return s_utf8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Serializes one file entry in the persisted filetree node format.
    /// </summary>
    public static string SerializePersistedFileEntryLine(FileEntry entry) => $"{entry.ContentHash} F {entry.Created:O} {entry.Modified:O} {entry.Name}";

    /// <summary>
    /// Serializes one directory entry in the persisted filetree node format.
    /// </summary>
    public static string SerializePersistedDirectoryEntryLine(DirectoryEntry entry) => SerializePersistedDirectoryEntryLine(entry.FileTreeHash, entry.Name);
    public static string SerializePersistedDirectoryEntryLine(FileTreeHash hash, PathSegment name) => $"{hash} D {name}";
    public static string SerializePersistedDirectoryEntryLine(string hash, PathSegment name)       => $"{hash} D {name}";

    /// <summary>
    /// Parses a persisted file entry line of the form '{content-hash} F {created} {modified} {name}'.
    /// </summary>
    public static FileEntry ParsePersistedFileEntryLine(string line)
    {
        ArgumentException.ThrowIfNullOrEmpty(line);

        line = line.TrimEnd('\r');

        var firstSpace = line.IndexOf(' ');
        if (firstSpace < 0)
            throw new FormatException($"Invalid tree entry (no spaces): '{line}'");

        var hash = line[..firstSpace];
        var afterHash = line[(firstSpace + 1)..];

        if (afterHash.Length < 2 || afterHash[0] != 'F' || afterHash[1] != ' ')
            throw new FormatException($"Invalid file entry (missing type marker): '{line}'");

        return ParseFileEntry(hash, afterHash[2..], line);
    }

    /// <summary>
    /// Parses one persisted filetree node line into either a <see cref="FileEntry"/> or <see cref="DirectoryEntry"/>.
    /// </summary>
    public static FileTreeEntry ParsePersistedNodeEntryLine(string line)
    {
        ArgumentException.ThrowIfNullOrEmpty(line);

        line = line.TrimEnd('\r');

        var firstSpace = line.IndexOf(' ');
        if (firstSpace < 0)
            throw new FormatException($"Invalid tree entry (no spaces): '{line}'");

        var hash = line[..firstSpace];
        var afterHash = line[(firstSpace + 1)..];

        if (afterHash.Length < 2 || afterHash[1] != ' ')
            throw new FormatException($"Invalid tree entry (missing type marker): '{line}'");

        var typeMarker = afterHash[0];
        var afterType = afterHash[2..];

        if (typeMarker == 'F')
            return ParseFileEntry(hash, afterType, line);

        if (typeMarker == 'D')
        {
            if (string.IsNullOrWhiteSpace(afterType))
                throw new FormatException($"Invalid directory entry (empty name): '{line}'");

            if (!FileTreeHash.TryParse(hash, out var fileTreeHash))
                throw new FormatException($"Invalid directory entry (invalid tree hash): '{line}'");

            return new DirectoryEntry
            {
                FileTreeHash = fileTreeHash,
                Name         = ParseDirectoryEntryName(afterType, line, "Invalid directory entry")
            };
        }

        throw new FormatException($"Invalid tree entry type marker '{typeMarker}': '{line}'");
    }

    /// <summary>
    /// Parses one staged node line into either a <see cref="FileEntry"/> or <see cref="StagedDirectoryEntry"/>.
    /// Staged directory lines use a directory id instead of a child filetree hash.
    /// </summary>
    public static FileTreeEntry ParseStagedNodeEntryLine(string line)
    {
        ArgumentException.ThrowIfNullOrEmpty(line);

        line = line.TrimEnd('\r');

        var firstSpace = line.IndexOf(' ');
        if (firstSpace < 0)
            throw new FormatException($"Invalid tree entry (no spaces): '{line}'");

        var afterHash = line[(firstSpace + 1)..];

        if (afterHash.Length < 2 || afterHash[1] != ' ')
            throw new FormatException($"Invalid tree entry (missing type marker): '{line}'");

        return afterHash[0] switch
        {
            'F' => ParsePersistedFileEntryLine(line),
            'D' => ParseStagedDirectoryEntryLine(line),
            _   => throw new FormatException($"Invalid tree entry type marker '{afterHash[0]}': '{line}'")
        };
    }

    private static IReadOnlyList<FileTreeEntry> ParsePersistedLines(IEnumerable<string> lines)
    {
        var entries = new List<FileTreeEntry>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrEmpty(line))
                continue;

            entries.Add(ParsePersistedNodeEntryLine(line));
        }

        return entries;
    }

    private static StagedDirectoryEntry ParseStagedDirectoryEntryLine(string line)
    {
        var firstSpace = line.IndexOf(' ');
        if (firstSpace < 0)
            throw new FormatException($"Invalid staged directory entry (no spaces): '{line}'");

        var normalizedDirectoryNameHash = line[..firstSpace];
        try
        {
            normalizedDirectoryNameHash = HashCodec.NormalizeHex(normalizedDirectoryNameHash);
        }
        catch (FormatException ex)
        {
            throw new FormatException($"Invalid staged directory entry (invalid directory id): '{line}'", ex);
        }

        var afterId = line[(firstSpace + 1)..];
        if (afterId.Length < 2 || afterId[0] != 'D' || afterId[1] != ' ')
            throw new FormatException($"Invalid staged directory entry (missing type marker): '{line}'");

        var name = afterId[2..];

        return new StagedDirectoryEntry
        {
            DirectoryNameHash = normalizedDirectoryNameHash,
            Name              = ParseDirectoryEntryName(name, line, "Invalid staged directory entry")
        };
    }

    private static PathSegment ParseDirectoryEntryName(string name, string line, string errorPrefix)
    {
        try
        {
            return PathSegment.Parse(name);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"{errorPrefix} (non-canonical name): '{line}'", ex);
        }
    }

    private static FileEntry ParseFileEntry(string hash, string afterType, string line)
    {
        var s1 = afterType.IndexOf(' ');
        if (s1 < 0)
            throw new FormatException($"Invalid file entry (missing created): '{line}'");

        var created = DateTimeOffset.Parse(afterType[..s1], null, DateTimeStyles.RoundtripKind);

        var afterCreated = afterType[(s1 + 1)..];
        var s2           = afterCreated.IndexOf(' ');
        if (s2 < 0)
            throw new FormatException($"Invalid file entry (missing modified): '{line}'");

        var modified = DateTimeOffset.Parse(afterCreated[..s2], null, DateTimeStyles.RoundtripKind);
        var name     = afterCreated[(s2 + 1)..];

        if (string.IsNullOrWhiteSpace(name))
            throw new FormatException($"Invalid file entry (empty name): '{line}'");

        if (!ContentHash.TryParse(hash, out var contentHash))
            throw new FormatException($"Invalid file entry (invalid content hash): '{line}'");

        return new FileEntry
        {
            ContentHash = contentHash,
            Created     = created,
            Modified    = modified,
            Name        = ParseFileEntryName(name, line)
        };
    }

    private static PathSegment ParseFileEntryName(string name, string line)
    {
        try
        {
            return PathSegment.Parse(name);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"Invalid file entry (non-canonical name): '{line}'", ex);
        }
    }
}
