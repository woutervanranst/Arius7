namespace Arius.Core.Shared.ChunkIndex;

public sealed record ChunkIndexRepairResult(
    int ListedChunkCount,
    int RebuiltEntryCount,
    int RebuiltShardCount,
    int UploadedShardCount,
    int DeletedStaleShardCount);
