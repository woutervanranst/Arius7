namespace Arius.Core.Shared.ChunkIndex;

public sealed class ChunkIndexRepairIncompleteException(RelativePath markerPath) 
    : InvalidOperationException($"Chunk index repair is incomplete. Rerun the explicit chunk-index repair command before archive, restore, or list operations. Repair marker: {markerPath}")
{
    public RelativePath MarkerPath { get; } = markerPath;
}

public sealed class ChunkIndexCorruptException(RelativePath shardBlobName, Exception innerException) 
    : InvalidOperationException($"Chunk index shard '{shardBlobName}' is corrupt. Run the explicit chunk-index repair command to rebuild the index.", innerException)
{
    public RelativePath ShardBlobName { get; } = shardBlobName;
}

public sealed class ChunkIndexRepairException(RelativePath chunkBlobName, string reason) 
    : InvalidOperationException($"Chunk index repair failed for chunk '{chunkBlobName}': {reason}")
{
    public RelativePath ChunkBlobName { get; } = chunkBlobName;
}
