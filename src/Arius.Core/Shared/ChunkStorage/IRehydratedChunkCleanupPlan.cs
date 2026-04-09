namespace Arius.Core.Shared.ChunkStorage;

public interface IRehydratedChunkCleanupPlan : IAsyncDisposable
{
    int ChunkCount { get; }

    long TotalBytes { get; }

    Task<RehydratedChunkCleanupResult> ExecuteAsync(CancellationToken cancellationToken = default);
}
