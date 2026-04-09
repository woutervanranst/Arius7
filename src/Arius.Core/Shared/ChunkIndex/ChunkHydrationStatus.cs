namespace Arius.Core.Shared.ChunkIndex;

public enum ChunkHydrationStatus
{
    Missing,
    Available,
    NeedsRehydration,
    RehydrationPending,
}
