using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.ChunkIndex;

/// <summary>
/// One line in a chunk index shard file.
/// Large-file format (content-hash == chunk-hash): <c>&lt;content-hash&gt; &lt;original-size&gt; &lt;compressed-size&gt;\n</c>
/// Small-file format (content-hash != chunk-hash): <c>&lt;content-hash&gt; &lt;chunk-hash&gt; &lt;original-size&gt; &lt;compressed-size&gt;\n</c>
/// All hashes are lowercase hex strings (SHA256 = 64 chars).
/// Field count is the discriminator: 3 fields = large file, 4 fields = small file.
/// </summary>
public sealed record ShardEntry(ContentHash ContentHash, ChunkHash ChunkHash, long OriginalSize, long CompressedSize)
{
    public ShardEntry(string contentHash, string chunkHash, long originalSize, long compressedSize)
        : this(ContentHash.Parse(contentHash), ChunkHash.Parse(chunkHash), originalSize, compressedSize)
    {
    }

    // ── Serialization ──────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes this entry to the shard line format (no trailing newline).
    /// Emits 3 fields when <see cref="ContentHash"/> == <see cref="ChunkHash"/> (large file),
    /// 4 fields otherwise (small/tar-bundled file).
    /// </summary>
    public string Serialize() =>
        ContentHash.ToString() == ChunkHash.ToString()
            ? $"{ContentHash} {OriginalSize} {CompressedSize}"
            : $"{ContentHash} {ChunkHash} {OriginalSize} {CompressedSize}";

    /// <summary>
    /// Parses a single shard line. Returns <c>null</c> on blank or comment lines.
    /// 3 fields = large file (chunk-hash reconstructed as content-hash).
    /// 4 fields = small file (explicit chunk-hash).
    /// </summary>
    public static ShardEntry? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            return null;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            3 => new ShardEntry(
                ContentHash:    ContentHash.Parse(parts[0]),
                ChunkHash:      ChunkHash.Parse(parts[0]),              // large file: chunk-hash == content-hash
                OriginalSize:   long.Parse(parts[1]),
                CompressedSize: long.Parse(parts[2])),
            4 => new ShardEntry(
                ContentHash:    ContentHash.Parse(parts[0]),
                ChunkHash:      ChunkHash.Parse(parts[1]),
                OriginalSize:   long.Parse(parts[2]),
                CompressedSize: long.Parse(parts[3])),
            _ => throw new FormatException($"Invalid shard entry (expected 3 or 4 fields): '{line}'")
        };
    }
}

/// <summary>
/// An in-memory shard: a collection of <see cref="ShardEntry"/> keyed by content-hash.
/// </summary>
public sealed class Shard
{
    private readonly Dictionary<ContentHash, ShardEntry> _entries;

    public Shard() => _entries = [];

    private Shard(Dictionary<ContentHash, ShardEntry> entries) => _entries = entries;

    public int Count => _entries.Count;

    // ── Lookup ─────────────────────────────────────────────────────────────────

    public bool TryLookup(ContentHash contentHash, out ShardEntry? entry) => _entries.TryGetValue(contentHash, out entry);

    // ── Merge ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a new shard that contains all entries from this shard plus
    /// the provided <paramref name="newEntries"/>. Existing entries are kept
    /// (last-writer-wins if duplicate content-hash is present in newEntries).
    /// </summary>
    public Shard Merge(IEnumerable<ShardEntry> newEntries)
    {
        var combined = new Dictionary<ContentHash, ShardEntry>(_entries);

        foreach (var e in newEntries)
            combined[e.ContentHash] = e;
        
        return new Shard(combined);
    }

    // ── Serialize ──────────────────────────────────────────────────────────────

    /// <summary>Serializes all entries to a text stream (one line per entry, sorted by content-hash).</summary>
    public void WriteTo(TextWriter writer)
    {
        foreach (var entry in _entries.Values.OrderBy(e => e.ContentHash.ToString(), StringComparer.Ordinal))
            writer.WriteLine(entry.Serialize());
    }

    // ── Deserialize ───────────────────────────────────────────────────────────

    /// <summary>Parses a shard from a text reader.</summary>
    public static Shard ReadFrom(TextReader reader)
    {
        var entries = new Dictionary<ContentHash, ShardEntry>();
        while (reader.ReadLine() is { } line)
        {
            var entry = ShardEntry.TryParse(line);
            if (entry is not null)
                entries[entry.ContentHash] = entry;
        }

        return new Shard(entries);
    }

    // ── Prefix calculation ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the 2-character (1-byte / 4-bit + 4-bit) shard prefix for a content-hash.
    /// With 65,536 shards this is the first 4 hex chars (2 bytes) of the hash.
    /// </summary>
    public static string PrefixOf(ContentHash contentHash) => contentHash.Prefix4;

    public static string PrefixOf(string contentHash) => PrefixOf(ContentHash.Parse(contentHash));
}
