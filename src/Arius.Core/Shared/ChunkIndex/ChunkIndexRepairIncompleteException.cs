namespace Arius.Core.Shared.ChunkIndex;

public sealed class ChunkIndexRepairIncompleteException : InvalidOperationException
{
    public ChunkIndexRepairIncompleteException(RelativePath markerPath)
        : base($"Chunk index repair is incomplete. Rerun the explicit chunk-index repair command before archive, restore, or list operations. Repair marker: {markerPath}")
    {
        MarkerPath = markerPath;
    }

    public RelativePath MarkerPath { get; }
}
