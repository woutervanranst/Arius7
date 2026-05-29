namespace Arius.Core.Shared.ChunkIndex;

public sealed class ChunkIndexRepairException : InvalidOperationException
{
    public ChunkIndexRepairException(RelativePath chunkBlobName, string reason)
        : base($"Chunk index repair failed for chunk '{chunkBlobName}': {reason}")
    {
        ChunkBlobName = chunkBlobName;
    }

    public RelativePath ChunkBlobName { get; }
}
