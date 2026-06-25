namespace Arius.Core.Shared.ChunkIndex;

// These chunk-index failures are part of the index's contract: RestoreCommandHandler (a different
// namespace) classifies them in its catch block to map each to actionable guidance, so they are
// intentionally shared across namespaces within the assembly.
[SharedWithinAssembly]
internal sealed class ChunkIndexRepairIncompleteException()
    : InvalidOperationException($"Chunk index repair is incomplete. Rerun the explicit chunk-index repair command before archive, restore, or list operations.")
{
}

[SharedWithinAssembly]
internal sealed class ChunkIndexCorruptException(RelativePath shardBlobName, Exception innerException)
    : InvalidOperationException($"Chunk index shard '{shardBlobName}' is corrupt. Run the explicit chunk-index repair command to rebuild the index.", innerException)
{
    public RelativePath ShardBlobName { get; } = shardBlobName;
}

internal sealed class ChunkIndexRepairException(RelativePath chunkBlobName, string reason) 
    : InvalidOperationException($"Chunk index repair failed for chunk '{chunkBlobName}': {reason}")
{
    public RelativePath ChunkBlobName { get; } = chunkBlobName;
}

[SharedWithinAssembly]
internal sealed class ChunkIndexLocalStoreException(string message, Exception innerException)
    : InvalidOperationException(message, innerException)
{
}
