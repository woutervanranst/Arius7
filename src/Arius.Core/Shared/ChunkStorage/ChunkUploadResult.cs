using Arius.Core.Shared.Storage;

namespace Arius.Core.Shared.ChunkStorage;

/// <summary>
/// The outcome of a chunk upload. <see cref="ActualTier"/> is the tier the blob is actually in, which for a
/// chunk within the small-file threshold is an online tier even when the archive tier was requested — the
/// chunk index records this, not the request, so restore does not pay to rehydrate an online blob.
/// </summary>
public sealed record ChunkUploadResult(ChunkHash ChunkHash, long StoredSize, bool AlreadyExisted, BlobTier ActualTier, long? OriginalSize = null);
