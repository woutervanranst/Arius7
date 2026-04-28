using System.IO.Compression;
using System.Text;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.FileTree;

/// <summary>
/// Serializes and deserializes <see cref="FileTreeBlob"/> instances to/from a compact text format.
///
/// Line format:
/// <list type="bullet">
///   <item>File entry: <c>&lt;hash&gt; F &lt;created&gt; &lt;modified&gt; &lt;name&gt;</c></item>
///   <item>Directory entry: <c>&lt;hash&gt; D &lt;name&gt;</c></item>
/// </list>
///
/// Rules:
/// <list type="bullet">
///   <item>Entries are sorted by <see cref="FileTreeEntry.Name"/> (ordinal, case-sensitive).</item>
///   <item>Timestamps use ISO-8601 round-trip format ("O"), UTC only — no spaces, unambiguous as a field.</item>
///   <item>Name is always the last field; it may contain spaces (no quoting or escaping needed).</item>
///   <item>Lines terminated by <c>\n</c>. No header, no blank lines.</item>
/// </list>
///
/// Tree hash = SHA256 of the canonical UTF-8 text bytes, optionally passphrase-seeded
/// via <see cref="IEncryptionService.ComputeHash(byte[])"/>.
/// </summary>
public static class FileTreeBlobSerializer
{
    private static readonly Encoding s_utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // ── Storage serialization (gzip + optional encryption) ────────────────────

    /// <summary>
    /// Serializes a <see cref="FileTreeBlob"/> to a gzip-compressed (and optionally encrypted) byte array
    /// for upload to blob storage. Mirrors <c>ShardSerializer.SerializeAsync</c>.
    /// </summary>
    public static async Task<byte[]> SerializeForStorageAsync(
        FileTreeBlob           tree,
        IEncryptionService encryption,
        CancellationToken  cancellationToken = default)
    {
        var ms = new MemoryStream();

        await using (var encStream  = encryption.WrapForEncryption(ms))
        await using (var gzipStream = new GZipStream(encStream, CompressionLevel.Optimal, leaveOpen: true))
        await using (var writer     = new StreamWriter(gzipStream, s_utf8, leaveOpen: true))
        {
            foreach (var entry in tree.Entries.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                switch (entry)
                {
                    case FileEntry fileEntry:
                        await writer.WriteLineAsync($"{fileEntry.ContentHash} F {fileEntry.Created:O} {fileEntry.Modified:O} {fileEntry.Name}");
                        break;
                    case DirectoryEntry directoryEntry:
                        await writer.WriteLineAsync($"{directoryEntry.FileTreeHash} D {directoryEntry.Name}");
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported file tree entry type: {entry.GetType().Name}");
                }
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Deserializes a <see cref="FileTreeBlob"/> from a gzip-compressed (and optionally encrypted) stream
    /// downloaded from blob storage. Mirrors <c>ShardSerializer.DeserializeFromStream</c>.
    /// </summary>
    public static async Task<FileTreeBlob> DeserializeFromStorageAsync(
        Stream             source,
        IEncryptionService encryption,
        CancellationToken  cancellationToken = default)
    {
        await using var decStream  = encryption.WrapForDecryption(source);
        await using var gzipStream = new GZipStream(decStream, CompressionMode.Decompress);
        using var reader  = new StreamReader(gzipStream, s_utf8, leaveOpen: true);
        var content = await reader.ReadToEndAsync(cancellationToken);
        return ParseLines(content.Split('\n'));
    }

    // ── Serialize / Deserialize (plaintext disk cache) ────────────────────────

    /// <summary>
    /// Deserializes a <see cref="FileTreeBlob"/> from canonical UTF-8 text bytes
    /// (the inverse of <see cref="Serialize"/>). Used when reading plaintext files
    /// from the local disk cache.
    /// </summary>
    public static FileTreeBlob Deserialize(byte[] bytes)
    {
        var text  = s_utf8.GetString(bytes);
        return ParseLines(text.Split('\n'));
    }

    /// <summary>
    /// Serializes a <see cref="FileTreeBlob"/> to canonical UTF-8 text bytes.
    /// Entries are sorted by name (ordinal) before serialization.
    /// </summary>
    public static byte[] Serialize(FileTreeBlob tree)
    {
        var sb = new StringBuilder();

        foreach (var entry in tree.Entries.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (entry is FileEntry fileEntry)
            {
                // <hash> F <created> <modified> <name>
                sb.Append(fileEntry.ContentHash);
                sb.Append(" F ");
                sb.Append(fileEntry.Created.ToString("O"));
                sb.Append(' ');
                sb.Append(fileEntry.Modified.ToString("O"));
                sb.Append(' ');
                sb.AppendLine(fileEntry.Name);
            }
            else if (entry is DirectoryEntry directoryEntry)
            {
                // <hash> D <name>
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

    private static FileTreeBlob ParseLines(string[] lines)
    {
        var entries = new List<FileTreeEntry>(lines.Length);

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrEmpty(line))
                continue;

            try
            {
                // All lines: first field = hash, second field = type marker (F or D)
                // F line: <hash> F <created> <modified> <name...>
                //         split on first 4 spaces → [hash, F, created, modified, name...]
                // D line: <hash> D <name...>
                //         split on first 2 spaces → [hash, D, name...]

                var firstSpace = line.IndexOf(' ');
                if (firstSpace < 0) throw new FormatException($"Invalid tree entry (no spaces): '{line}'");

                var hash       = line[..firstSpace];
                var afterHash  = line[(firstSpace + 1)..];

                if (afterHash.Length < 2 || afterHash[1] != ' ')
                    throw new FormatException($"Invalid tree entry (missing type marker): '{line}'");

                var typeMarker = afterHash[0];
                var afterType  = afterHash[2..];

                if (typeMarker == 'F')
                {
                    // <created> <modified> <name...>
                    var s1 = afterType.IndexOf(' ');
                    if (s1 < 0) throw new FormatException($"Invalid file entry (missing created): '{line}'");
                    var created = DateTimeOffset.Parse(afterType[..s1], null, System.Globalization.DateTimeStyles.RoundtripKind);

                    var afterCreated = afterType[(s1 + 1)..];
                    var s2           = afterCreated.IndexOf(' ');
                    if (s2 < 0) throw new FormatException($"Invalid file entry (missing modified): '{line}'");
                    var modified = DateTimeOffset.Parse(afterCreated[..s2], null, System.Globalization.DateTimeStyles.RoundtripKind);

                    var name = afterCreated[(s2 + 1)..];

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    if (!ContentHash.TryParse(hash, out var contentHash))
                    {
                        continue;
                    }

                    entries.Add(new FileEntry
                    {
                        ContentHash = contentHash,
                        Created  = created,
                        Modified = modified,
                        Name     = name
                    });
                }
                else if (typeMarker == 'D')
                {
                    if (string.IsNullOrWhiteSpace(afterType) || !FileTreeHash.TryParse(hash, out var fileTreeHash))
                    {
                        continue;
                    }

                    entries.Add(new DirectoryEntry
                    {
                        FileTreeHash = fileTreeHash,
                        Name         = afterType
                    });
                }
                else
                {
                    throw new FormatException($"Invalid tree entry type marker '{typeMarker}': '{line}'");
                }
            }
            catch (FormatException)
            {
                continue;
            }
        }

        return new FileTreeBlob { Entries = entries };
    }

    // ── Hash computation ──────────────────────────────────────────────────────

    /// <summary>
    /// Computes the tree hash: <see cref="IEncryptionService.ComputeHash(byte[])"/> applied to
    /// the canonical text bytes. With passphrase: SHA256(passphrase + text); without: SHA256(text).
    /// Returns lowercase hex.
    /// </summary>
    public static FileTreeHash ComputeHash(FileTreeBlob tree, IEncryptionService encryption)
    {
        var text = Serialize(tree);
        return FileTreeHash.Parse(encryption.ComputeHash(text));
    }
}
