namespace Arius.Core.Shared.ChunkStorage;

public sealed record RehydratedChunkCleanupResult(int DeletedChunkCount, long FreedBytes);
