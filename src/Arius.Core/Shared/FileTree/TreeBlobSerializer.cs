using Arius.Core.Shared.Encryption;
using System.IO.Compression;
using System.Text;

namespace Arius.Core.Shared.FileTree;

/// <summary>
/// Serializes and deserializes <see cref="TreeBlob"/> instances to/from a compact text format.
///
/// Line format:
/// <list type="bullet">
///   <item>File entry: <c>&lt;hash&gt; F &lt;created&gt; &lt;modified&gt; &lt;name&gt;</c></item>
///   <item>Directory entry: <c>&lt;hash&gt; D &lt;name&gt;</c></item>
/// </list>
///
/// Rules:
/// <list type="bullet">
///   <item>Entries are sorted by <see cref="TreeEntry.Name"/> (ordinal, case-sensitive).</item>
///   <item>Timestamps use ISO-8601 round-trip format ("O"), UTC only — no spaces, unambiguous as a field.</item>
///   <item>Name is always the last field; it may contain spaces (no quoting or escaping needed).</item>
///   <item>Lines terminated by <c>\n</c>. No header, no blank lines.</item>
/// </list>
///
/// Tree hash = SHA256 of the canonical UTF-8 text bytes, optionally passphrase-seeded
/// via <see cref="IEncryptionService.ComputeHash(byte[])"/>.
/// </summary>
public static class TreeBlobSerializer
{
    private static readonly Encoding s_utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // ── Storage serialization (gzip + optional encryption) ────────────────────

    /// <summary>
    /// Serializes a <see cref="TreeBlob"/> to a gzip-compressed (and optionally encrypted) byte array
    /// for upload to blob storage. Mirrors <c>ShardSerializer.SerializeAsync</c>.
    /// </summary>
    public static async Task<byte[]> SerializeForStorageAsync(
        TreeBlob           tree,
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
                if (entry.Type == TreeEntryType.File)
                    await writer.WriteLineAsync($"{entry.Hash} F {entry.Created!.Value:O} {entry.Modified!.Value:O} {entry.Name}");
                else
                    await writer.WriteLineAsync($"{entry.Hash} D {entry.Name}");
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Deserializes a <see cref="TreeBlob"/> from a gzip-compressed (and optionally encrypted) stream
    /// downloaded from blob storage. Mirrors <c>ShardSerializer.DeserializeFromStream</c>.
    /// </summary>
    public static async Task<TreeBlob> DeserializeFromStorageAsync(
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

    // ── Serialize ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes a <see cref="TreeBlob"/> to canonical UTF-8 text bytes.
    /// Entries are sorted by name (ordinal) before serialization.
    /// </summary>
    public static byte[] Serialize(TreeBlob tree)
    {
        var sb = new StringBuilder();

        foreach (var entry in tree.Entries.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (entry.Type == TreeEntryType.File)
            {
                // <hash> F <created> <modified> <name>
                sb.Append(entry.Hash);
                sb.Append(" F ");
                sb.Append(entry.Created!.Value.ToString("O"));
                sb.Append(' ');
                sb.Append(entry.Modified!.Value.ToString("O"));
                sb.Append(' ');
                sb.AppendLine(entry.Name);
            }
            else
            {
                // <hash> D <name>
                sb.Append(entry.Hash);
                sb.Append(" D ");
                sb.AppendLine(entry.Name);
            }
        }

        return s_utf8.GetBytes(sb.ToString());
    }

    private static TreeBlob ParseLines(string[] lines)
    {
        var entries = new List<TreeEntry>(lines.Length);

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrEmpty(line))
                continue;

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

                entries.Add(new TreeEntry
                {
                    Hash     = hash,
                    Type     = TreeEntryType.File,
                    Created  = created,
                    Modified = modified,
                    Name     = name
                });
            }
            else if (typeMarker == 'D')
            {
                entries.Add(new TreeEntry
                {
                    Hash = hash,
                    Type = TreeEntryType.Dir,
                    Name = afterType
                });
            }
            else
            {
                throw new FormatException($"Invalid tree entry type marker '{typeMarker}': '{line}'");
            }
        }

        return new TreeBlob { Entries = entries };
    }

    // ── Hash computation ──────────────────────────────────────────────────────

    /// <summary>
    /// Computes the tree hash: <see cref="IEncryptionService.ComputeHash(byte[])"/> applied to
    /// the canonical text bytes. With passphrase: SHA256(passphrase + text); without: SHA256(text).
    /// Returns lowercase hex.
    /// </summary>
    public static string ComputeHash(TreeBlob tree, IEncryptionService encryption)
    {
        var text = Serialize(tree);
        var hash = encryption.ComputeHash(text);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
