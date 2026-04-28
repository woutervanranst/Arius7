using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.ChunkStorage;

public sealed record ChunkUploadResult(ChunkHash ChunkHash, long StoredSize, bool AlreadyExisted);
