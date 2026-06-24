using System.Diagnostics;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Shared.ChunkIndex;

/// <summary>
/// One entry in a chunk-index shard file.
/// Large-file chunk format (content-hash == chunk-hash): <c>content-hash            original-size chunk-size tier-hint</c>
/// Small-file chunk format (content-hash != chunk-hash): <c>content-hash chunk-hash original-size chunk-size tier-hint</c>
/// All hashes are lowercase hex strings (SHA256 = 64 chars).
/// Field count is the discriminator: 4 fields = large file, 5 fields = small file.
/// 
/// <c>chunk-size</c> is the byte count of the stored chunk blob:
///   for large chunks, that is the large chunk blob itself;
///   for tar-bundled files, that is the parent tar chunk blob.
/// The tier-hint is the storage tier of the chunk blob at archive time (wire values: hot=1, cool=2, cold=3, archive=4);
/// it is a hint — a lifecycle policy or rehydration may change the actual tier out of our control.
/// </summary>
[SharedWithinAssembly]
[DebuggerDisplay("{ContentHash} {ChunkHash} {OriginalSize} {ChunkSize} {StorageTierHint}")]
internal sealed record ShardEntry(ContentHash ContentHash, ChunkHash ChunkHash, long OriginalSize, long ChunkSize, BlobTier StorageTierHint)
{
    public bool IsLargeChunk => ChunkHash.Parse(ContentHash) == ChunkHash;

    // ── Serialization ──────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes this entry to the shard line format (no trailing newline).
    /// Emits 4 fields when <see cref="ContentHash"/> == <see cref="ChunkHash"/> (large file),
    /// 5 fields otherwise (small/tar-bundled file).
    /// </summary>
    public string Serialize() =>
        IsLargeChunk
            ? $"{ContentHash} {OriginalSize} {ChunkSize} {SerializeTier(StorageTierHint)}"
            : $"{ContentHash} {ChunkHash} {OriginalSize} {ChunkSize} {SerializeTier(StorageTierHint)}";

    /// <summary>
    /// Parses a single shard line. Returns <c>null</c> on blank or comment lines.
    /// 4 fields = large file (chunk-hash reconstructed as content-hash).
    /// 5 fields = small file (explicit chunk-hash).
    /// </summary>
    public static ShardEntry? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            return null;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            4 => new ShardEntry(
                ContentHash:     ContentHash.Parse(parts[0]),
                ChunkHash:       ChunkHash.Parse(parts[0]),              // large file: chunk-hash == content-hash
                OriginalSize:    long.Parse(parts[1]),
                ChunkSize:       long.Parse(parts[2]),
                StorageTierHint: ParseTier(parts[3], line)),
            5 => new ShardEntry(
                ContentHash:     ContentHash.Parse(parts[0]),
                ChunkHash:       ChunkHash.Parse(parts[1]),
                OriginalSize:    long.Parse(parts[2]),
                ChunkSize:       long.Parse(parts[3]),
                StorageTierHint: ParseTier(parts[4], line)),
            _ => throw new FormatException($"Invalid shard entry (expected 4 or 5 fields): '{line}'")
        };
    }

    // ── Tier wire mapping ──────────────────────────────────────────────────────
    // Explicit values, independent of the BlobTier enum ordering.

    public static int SerializeTier(BlobTier tier) => tier switch
    {
        BlobTier.Hot     => 1,
        BlobTier.Cool    => 2,
        BlobTier.Cold    => 3,
        BlobTier.Archive => 4,
        _                => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown storage tier")
    };

    public static BlobTier DeserializeTier(int wireValue) => wireValue switch
    {
        1 => BlobTier.Hot,
        2 => BlobTier.Cool,
        3 => BlobTier.Cold,
        4 => BlobTier.Archive,
        _ => throw new FormatException($"Invalid storage tier wire value: {wireValue}")
    };

    private static BlobTier ParseTier(string field, string line) =>
        int.TryParse(field, out var wireValue)
            ? DeserializeTier(wireValue)
            : throw new FormatException($"Invalid storage tier in shard entry: '{line}'");
}

/// <summary>
/// Mutable in-memory representation of one chunk-index shard, keyed by content hash.
/// </summary>
internal sealed class Shard
{
    private readonly Dictionary<ContentHash, ShardEntry> _entries;

    public Shard() => _entries = [];

    public int Count => _entries.Count;

    public IEnumerable<ShardEntry> Entries => _entries.Values;

    // ── Mutation ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds or replaces an entry in this shard.
    /// </summary>
    public void AddOrUpdate(ShardEntry entry) => _entries[entry.ContentHash] = entry;

    // ── Serialize ──────────────────────────────────────────────────────────────

    /// <summary>Serializes all entries to a text stream (one line per entry, sorted by content-hash).</summary>
    public void WriteTo(TextWriter writer)
    {
        // Fixed '\n' line terminator (not writer.WriteLine, which uses Environment.NewLine) so the
        // serialized shard bytes are deterministic across platforms.
        foreach (var entry in _entries.Values.OrderBy(e => e.ContentHash.ToString(), StringComparer.Ordinal))
        {
            writer.Write(entry.Serialize());
            writer.Write('\n');
        }
    }

    // ── Deserialize ───────────────────────────────────────────────────────────

    /// <summary>Parses a shard from a text reader.</summary>
    public static Shard ReadFrom(TextReader reader)
    {
        var shard = new Shard();
        while (reader.ReadLine() is { } line)
        {
            var entry = ShardEntry.TryParse(line);
            if (entry is not null)
                shard.AddOrUpdate(entry);
        }

        return shard;
    }
}
