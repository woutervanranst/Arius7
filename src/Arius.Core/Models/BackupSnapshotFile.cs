namespace Arius.Core.Models;

/// <summary>
/// Represents a single file entry in a snapshot.
/// A file may span multiple chunks; <see cref="ChunkHashes"/> lists them in order.
/// </summary>
public sealed record BackupSnapshotFile(
    string                    Path,
    IReadOnlyList<BlobHash>   ChunkHashes,
    long                      Size)
{
    /// <summary>
    /// Convenience property: for single-chunk files (or legacy), the first (only) hash.
    /// </summary>
    public BlobHash BlobHash => ChunkHashes[0];
}
