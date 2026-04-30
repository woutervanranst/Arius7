using System.Globalization;
using System.IO.Compression;
using System.Text;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.FileTree;

/// <summary>
/// Serializes and deserializes persisted filetree entries to and from the canonical text format.
/// </summary>
public static class FileTreeSerializer
{
    private static readonly Encoding s_utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static async Task<byte[]> SerializeForStorageAsync(
        IReadOnlyList<FileTreeEntry> entries,
        IEncryptionService encryption,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(encryption);

        var ms = new MemoryStream();

        await using (var encStream = encryption.WrapForEncryption(ms))
        await using (var gzipStream = new GZipStream(encStream, CompressionLevel.Optimal, leaveOpen: true))
        await using (var writer = new StreamWriter(gzipStream, s_utf8, leaveOpen: true))
        {
            foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (entry is FileEntry fileEntry)
                    await writer.WriteLineAsync(SerializeFileEntryLine(fileEntry));
                else if (entry is DirectoryEntry directoryEntry)
                    await writer.WriteLineAsync($"{directoryEntry.FileTreeHash} D {directoryEntry.Name}");
                else
                    throw new InvalidOperationException($"Unsupported file tree entry type: {entry.GetType().Name}");
            }
        }

        return ms.ToArray();
    }

    public static async Task<IReadOnlyList<FileTreeEntry>> DeserializeFromStorageAsync(
        Stream source,
        IEncryptionService encryption,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(encryption);

        await using var decStream = encryption.WrapForDecryption(source);
        await using var gzipStream = new GZipStream(decStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzipStream, s_utf8, leaveOpen: true);
        var content = await reader.ReadToEndAsync(cancellationToken);
        return ParsePersistedLines(content.Split('\n'));
    }

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

        foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (entry is FileEntry fileEntry)
            {
                sb.AppendLine(SerializeFileEntryLine(fileEntry));
            }
            else if (entry is DirectoryEntry directoryEntry)
            {
                sb.Append(directoryEntry.FileTreeHash);
                sb.Append(" D ");
                sb.AppendLine(directoryEntry.Name);
            }
            else
            {
                throw new InvalidOperationException($"Unsupported file tree entry type: {entry.GetType().Name}");
            }
        }

        return s_utf8.GetBytes(sb.ToString());
    }

    public static string SerializeFileEntryLine(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return $"{entry.ContentHash} F {entry.Created:O} {entry.Modified:O} {entry.Name}";
    }

    public static FileEntry ParseFileEntryLine(string line)
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

    public static FileTreeEntry ParsePersistedEntryLine(string line)
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
                Name = afterType
            };
        }

        throw new FormatException($"Invalid tree entry type marker '{typeMarker}': '{line}'");
    }

    public static FileTreeEntry ParseStagedEntryLine(string line)
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
            'F' => ParseFileEntryLine(line),
            'D' => StagedDirectoryEntry.Parse(line),
            _ => throw new FormatException($"Invalid tree entry type marker '{afterHash[0]}': '{line}'")
        };
    }

    public static FileTreeHash ComputeHash(IReadOnlyList<FileTreeEntry> entries, IEncryptionService encryption)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(encryption);

        var text = Serialize(entries);
        return FileTreeHash.Parse(encryption.ComputeHash(text));
    }

    private static IReadOnlyList<FileTreeEntry> ParsePersistedLines(IEnumerable<string> lines)
    {
        var entries = new List<FileTreeEntry>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrEmpty(line))
                continue;

            entries.Add(ParsePersistedEntryLine(line));
        }

        return entries;
    }

    private static FileEntry ParseFileEntry(string hash, string afterType, string line)
    {
        var s1 = afterType.IndexOf(' ');
        if (s1 < 0)
            throw new FormatException($"Invalid file entry (missing created): '{line}'");

        var created = DateTimeOffset.Parse(afterType[..s1], null, DateTimeStyles.RoundtripKind);

        var afterCreated = afterType[(s1 + 1)..];
        var s2 = afterCreated.IndexOf(' ');
        if (s2 < 0)
            throw new FormatException($"Invalid file entry (missing modified): '{line}'");

        var modified = DateTimeOffset.Parse(afterCreated[..s2], null, DateTimeStyles.RoundtripKind);
        var name = afterCreated[(s2 + 1)..];

        if (string.IsNullOrWhiteSpace(name))
            throw new FormatException($"Invalid file entry (empty name): '{line}'");

        if (!ContentHash.TryParse(hash, out var contentHash))
            throw new FormatException($"Invalid file entry (invalid content hash): '{line}'");

        return new FileEntry
        {
            ContentHash = contentHash,
            Created = created,
            Modified = modified,
            Name = name
        };
    }
}
