namespace Arius.Core.Shared.ChunkStorage;

public enum ChunkHydrationStatus
{
    Unknown,
    Available,
    NeedsRehydration,
    RehydrationPending,
}
