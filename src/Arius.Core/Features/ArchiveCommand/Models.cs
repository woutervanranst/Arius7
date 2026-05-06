using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.LocalFile;

namespace Arius.Core.Features.ArchiveCommand;

// ── Pipeline intermediate models ──────────────────────────────────────────────

/// <summary>
/// A <see cref="Shared.LocalFile.FilePair"/> after content hash has been computed.
/// Used between Hash stage and Dedup stage.
/// </summary>
internal sealed record HashedFilePair(
    FilePair       FilePair,
    ContentHash    ContentHash
);

/// <summary>
/// A file that has passed dedup check and needs to be uploaded.
/// Carries the resolved content hash and the source path for streaming.
/// </summary>
internal sealed record FileToUpload(
    HashedFilePair HashedPair,
    long           FileSize     // bytes (0 for pointer-only)
);

/// <summary>
/// An index entry recording the content-hash → chunk-hash mapping after upload.
/// </summary>
internal sealed record IndexEntry(
    ContentHash ContentHash,
    ChunkHash ChunkHash,
    long   OriginalSize,
    long   CompressedSize
);

/// <summary>
/// A small file that has been added to the current tar accumulator.
/// Carries original-size for proportional compressed-size estimation,
/// and the full <see cref="HashedFilePair"/> for manifest-entry writing.
/// </summary>
internal sealed record TarEntry(
    ContentHash    ContentHash,
    long           OriginalSize,
    HashedFilePair HashedPair
);

/// <summary>
/// A sealed tar archive ready for upload.
/// </summary>
internal sealed record SealedTar(
    string          TarFilePath,       // temp file on disk
    ChunkHash       TarHash,           // hash of the tar body (before gzip+encrypt)
    long            UncompressedSize,  // sum of file sizes
    IReadOnlyList<TarEntry> Entries    // per-file info for thin chunk creation
);
