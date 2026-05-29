namespace Arius.Core.Shared.ChunkIndex;

public sealed class ChunkIndexCorruptException : InvalidOperationException
{
    public ChunkIndexCorruptException(RelativePath shardBlobName, Exception innerException)
        : base($"Chunk index shard '{shardBlobName}' is corrupt. Run the explicit chunk-index repair command to rebuild the index.", innerException)
    {
        ShardBlobName = shardBlobName;
    }

    public RelativePath ShardBlobName { get; }
}
