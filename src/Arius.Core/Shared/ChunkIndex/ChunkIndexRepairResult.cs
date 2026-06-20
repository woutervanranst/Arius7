namespace Arius.Core.Shared.ChunkIndex;

/// <summary>
/// Summary of an explicit chunk-index repair run.
/// </summary>
/// <param name="ListedChunkCount">Number of chunk blobs listed while rebuilding.</param>
/// <param name="RebuiltEntryCount">Number of chunk-index entries rebuilt from authoritative chunk blobs.</param>
/// <param name="RebuiltShardCount">Number of leaf shard prefixes produced from the rebuilt entries (equal to
/// <paramref name="UploadedShardCount"/> by construction, since only non-empty shards are built and each is uploaded).</param>
/// <param name="UploadedShardCount">Number of shard blobs uploaded after rebuild (one per rebuilt leaf prefix).</param>
/// <param name="DeletedStaleShardCount">Number of stale shard blobs deleted after rebuild.</param>
public sealed record ChunkIndexRepairResult(
    int ListedChunkCount,
    int RebuiltEntryCount,
    int RebuiltShardCount,
    int UploadedShardCount,
    int DeletedStaleShardCount);
