namespace Arius.Core.Shared.ChunkIndex;

/// <summary>
/// Summary of an explicit chunk-index repair run.
/// </summary>
/// <param name="ListedChunkCount">Number of chunk blobs listed while rebuilding.</param>
/// <param name="RebuiltEntryCount">Number of chunk-index entries rebuilt from authoritative chunk blobs.</param>
/// <param name="RebuiltShardCount">Number of shard prefixes represented by the rebuilt entries.</param>
/// <param name="UploadedShardCount">Number of shard blobs uploaded after rebuild.</param>
/// <param name="DeletedStaleShardCount">Number of stale shard blobs deleted after rebuild.</param>
public sealed record ChunkIndexRepairResult(
    int ListedChunkCount,
    int RebuiltEntryCount,
    int RebuiltShardCount,
    int UploadedShardCount,
    int DeletedStaleShardCount);
