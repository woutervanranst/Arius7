using Arius.Core.Shared.LocalFile;
using Arius.Core.Shared.Storage;
using Mediator;

namespace Arius.Core.Features.Archive;

// ── Pipeline intermediate models ──────────────────────────────────────────────

/// <summary>
/// A <see cref="Shared.LocalFile.FilePair"/> after content hash has been computed.
/// Used between Hash stage and Dedup stage.
/// </summary>
public sealed record HashedFilePair(
    FilePair       FilePair,
    string         ContentHash,   // hex SHA-256 (64 chars)
    string         LocalRootPath  // absolute path to archive root (for stream reading)
);

/// <summary>
/// A file that has passed dedup check and needs to be uploaded.
/// Carries the resolved content hash and the source path for streaming.
/// </summary>
public sealed record FileToUpload(
    HashedFilePair HashedPair,
    long           FileSize     // bytes (0 for pointer-only)
);

/// <summary>
/// An index entry recording the content-hash → chunk-hash mapping after upload.
/// </summary>
public sealed record IndexEntry(
    string ContentHash,
    string ChunkHash,
    long   OriginalSize,
    long   CompressedSize
);

/// <summary>
/// A small file that has been added to the current tar accumulator.
/// Carries original-size for proportional compressed-size estimation,
/// and the full <see cref="HashedFilePair"/> for manifest-entry writing.
/// </summary>
public sealed record TarEntry(
    string         ContentHash,
    long           OriginalSize,
    HashedFilePair HashedPair
);

/// <summary>
/// A sealed tar archive ready for upload.
/// </summary>
public sealed record SealedTar(
    string          TarFilePath,       // temp file on disk
    string          TarHash,           // hash of the tar body (before gzip+encrypt)
    long            UncompressedSize,  // sum of file sizes
    IReadOnlyList<TarEntry> Entries    // per-file info for thin chunk creation
);

// ── Task 8.14: Progress events ─────────────────────────────────────────────────

/// <summary>A single file discovered during enumeration (published per-file).</summary>
/// <param name="RelativePath">Forward-slash relative path of the file.</param>
/// <param name="FileSize">File size in bytes.</param>
public sealed record FileScannedEvent(string RelativePath, long FileSize) : INotification;

/// <summary>File enumeration complete: final totals known.</summary>
/// <param name="TotalFiles">Total number of files enumerated.</param>
/// <param name="TotalBytes">Total uncompressed bytes of all enumerated files.</param>
public sealed record ScanCompleteEvent(long TotalFiles, long TotalBytes) : INotification;

/// <summary>A file started hashing.</summary>
/// <param name="RelativePath">Relative path of the file being hashed.</param>
/// <param name="FileSize">File size in bytes; used as the progress bar denominator.</param>
public sealed record FileHashingEvent(string RelativePath, long FileSize) : INotification;

/// <summary>A file finished hashing.</summary>
public sealed record FileHashedEvent(string RelativePath, string ContentHash) : INotification;

/// <summary>A chunk upload started.</summary>
public sealed record ChunkUploadingEvent(string ContentHash, long Size) : INotification;

/// <summary>A chunk upload completed.</summary>
public sealed record ChunkUploadedEvent(string ContentHash, long CompressedSize) : INotification;

/// <summary>A tar bundle is being sealed.</summary>
/// <param name="EntryCount">Number of entries in the sealed tar.</param>
/// <param name="UncompressedSize">Total uncompressed size of all entries in bytes.</param>
/// <param name="TarHash">Content hash of the sealed tar archive file (used as the bundle identifier).</param>
/// <param name="ContentHashes">Content hash of every file entry in the tar, in insertion order.</param>
public sealed record TarBundleSealingEvent(int EntryCount, long UncompressedSize, string TarHash, IReadOnlyList<string> ContentHashes) : INotification;

/// <summary>A tar bundle was uploaded.</summary>
public sealed record TarBundleUploadedEvent(string TarHash, long CompressedSize, int EntryCount) : INotification;

/// <summary>Snapshot creation complete.</summary>
public sealed record SnapshotCreatedEvent(string RootHash, DateTimeOffset Timestamp, long FileCount) : INotification;

/// <summary>The tar builder started accumulating a new tar bundle.</summary>
public sealed record TarBundleStartedEvent() : INotification;

/// <summary>A file was written to the current tar bundle.</summary>
/// <param name="ContentHash">Content hash of the file added.</param>
/// <param name="CurrentEntryCount">Number of entries in the current tar bundle so far.</param>
/// <param name="CurrentTarSize">Cumulative uncompressed size of the current tar bundle in bytes.</param>
public sealed record TarEntryAddedEvent(string ContentHash, int CurrentEntryCount, long CurrentTarSize) : INotification;
