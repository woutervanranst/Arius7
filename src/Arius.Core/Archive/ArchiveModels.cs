using Arius.Core.LocalFile;
using Mediator;

namespace Arius.Core.Archive;

// ── Pipeline intermediate models ──────────────────────────────────────────────

/// <summary>
/// A <see cref="LocalFile.FilePair"/> after content hash has been computed.
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

// ── Task 8.1: Mediator ArchiveCommand ─────────────────────────────────────────

/// <summary>
/// Options controlling the archive pipeline.
/// </summary>
public sealed record ArchiveOptions
{
    /// <summary>Root directory to archive.</summary>
    public required string RootDirectory { get; init; }

    /// <summary>
    /// Files smaller than this threshold are bundled into tar archives.
    /// Default: 1 MB.
    /// </summary>
    public long SmallFileThreshold { get; init; } = 1024 * 1024; // 1 MB

    /// <summary>
    /// Target uncompressed size of a tar bundle before sealing.
    /// Default: 64 MB.
    /// </summary>
    public long TarTargetSize { get; init; } = 64L * 1024 * 1024; // 64 MB

    /// <summary>Upload tier for chunk blobs. Default: Archive.</summary>
    public Storage.BlobTier UploadTier { get; init; } = Storage.BlobTier.Archive;

    /// <summary>If <c>true</c>, delete local binary files after a successful snapshot.</summary>
    public bool RemoveLocal { get; init; } = false;

    /// <summary>If <c>true</c>, do not create or update <c>.pointer.arius</c> files.</summary>
    public bool NoPointers { get; init; } = false;

    /// <summary>
    /// Optional factory invoked when a file begins hashing.
    /// Parameters: relative path, file size in bytes.
    /// Returns an <see cref="IProgress{T}"/> that receives cumulative bytes hashed.
    /// When <c>null</c>, no byte-level progress is reported for hashing.
    /// </summary>
    public Func<string, long, IProgress<long>>? CreateHashProgress { get; init; }

    /// <summary>
    /// Optional factory invoked when a chunk begins uploading.
    /// Parameters: content hash, uncompressed size in bytes.
    /// Returns an <see cref="IProgress{T}"/> that receives cumulative bytes read from the source stream.
    /// When <c>null</c>, no byte-level progress is reported for uploads.
    /// </summary>
    public Func<string, long, IProgress<long>>? CreateUploadProgress { get; init; }
}

/// <summary>
/// Mediator command: archive a local directory to blob storage.
/// </summary>
public sealed record ArchiveCommand(ArchiveOptions Options)
    : ICommand<ArchiveResult>;

/// <summary>
/// Result returned by <see cref="ArchiveCommand"/>.
/// </summary>
public sealed record ArchiveResult
{
    public required bool             Success       { get; init; }
    public required long             FilesScanned  { get; init; }
    public required long             FilesUploaded { get; init; }
    public required long             FilesDeduped  { get; init; }
    public required long             TotalSize     { get; init; }
    public required string?          RootHash      { get; init; }
    public required DateTimeOffset   SnapshotTime  { get; init; }
    public          string?          ErrorMessage  { get; init; }
}

// ── Task 8.14: Progress events ─────────────────────────────────────────────────

/// <summary>File enumeration complete: total count known.</summary>
public sealed record FileScannedEvent(long TotalFiles) : INotification;

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

/// <summary>A file was written to the current tar bundle.</summary>
/// <param name="ContentHash">Content hash of the file added.</param>
/// <param name="CurrentEntryCount">Number of entries in the current tar bundle so far.</param>
/// <param name="CurrentTarSize">Cumulative uncompressed size of the current tar bundle in bytes.</param>
public sealed record TarEntryAddedEvent(string ContentHash, int CurrentEntryCount, long CurrentTarSize) : INotification;
