namespace Arius.Core.Shared.Snapshot;

/// <summary>
/// Snapshot manifest: the root of a complete archive state.
/// Stored (gzip + optional encrypt) at <c>snapshots/&lt;timestamp&gt;</c>.
/// </summary>
public sealed record SnapshotManifest
{
    /// <summary>UTC timestamp of snapshot creation (ISO-8601 round-trip format).</summary>
    public required DateTimeOffset Timestamp   { get; init; }

    /// <summary>Root tree hash (SHA-256 hex, 64 chars) produced by the tree builder.</summary>
    public required FileTreeHash   RootHash    { get; init; }

    /// <summary>Total number of files in this snapshot.</summary>
    public required long           FileCount   { get; init; }

    /// <summary>
    /// Sum of original (uncompressed) sizes of all files in bytes, counting duplicates once per file —
    /// the logical size of this snapshot (the size you would restore). Not deduplicated or compressed.
    /// </summary>
    public required long           OriginalSize { get; init; }

    /// <summary>Arius tool version that created this snapshot.</summary>
    public required string         AriusVersion { get; init; }
}