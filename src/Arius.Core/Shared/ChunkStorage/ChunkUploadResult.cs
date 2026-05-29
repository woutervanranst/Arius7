namespace Arius.Core.Shared.ChunkStorage;

public sealed record ChunkUploadResult(ChunkHash ChunkHash, long StoredSize, bool AlreadyExisted, long? OriginalSize = null);
