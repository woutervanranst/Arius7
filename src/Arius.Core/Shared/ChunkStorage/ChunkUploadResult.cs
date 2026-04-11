namespace Arius.Core.Shared.ChunkStorage;

public sealed record ChunkUploadResult(string ChunkHash, long StoredSize, bool AlreadyExisted);
