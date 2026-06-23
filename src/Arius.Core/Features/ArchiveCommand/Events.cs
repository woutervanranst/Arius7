using Mediator;

namespace Arius.Core.Features.ArchiveCommand;

// ── Task 8.14: Progress events ─────────────────────────────────────────────────

/// <summary>A single file discovered during enumeration (published per-file).</summary>
/// <param name="RelativePath">Forward-slash relative path of the file.</param>
/// <param name="FileSize">File size in bytes.</param>
public sealed record FileScannedEvent(RelativePath RelativePath, long FileSize) : INotification;

/// <summary>File enumeration complete: final totals known.</summary>
/// <param name="TotalFiles">Total number of files enumerated.</param>
/// <param name="TotalBytes">Total uncompressed bytes of all enumerated files.</param>
public sealed record ScanCompleteEvent(long TotalFiles, long TotalBytes) : INotification;

/// <summary>A file started hashing.</summary>
/// <param name="RelativePath">Relative path of the file being hashed.</param>
/// <param name="FileSize">File size in bytes; used as the progress bar denominator.</param>
public sealed record FileHashingEvent(RelativePath RelativePath, long FileSize) : INotification;

/// <summary>A file finished hashing.</summary>
public sealed record FileHashedEvent(RelativePath RelativePath, ContentHash ContentHash) : INotification;

/// <summary>A file was skipped because it could not be read/opened during the pipeline.</summary>
/// <param name="RelativePath">Relative path of the skipped file; used to clear its progress row.</param>
public sealed record FileSkippedEvent(RelativePath RelativePath) : INotification;

/// <summary>Why a file or directory was skipped during enumeration (before it ever entered the pipeline).</summary>
public enum SkipReason
{
    /// <summary>Name matched the configured exclusion list (file or directory).</summary>
    ExcludedByName,
    /// <summary>Carried an excluded <see cref="FileAttributes.System"/>/<see cref="FileAttributes.Hidden"/> attribute.</summary>
    ExcludedByAttribute,
    /// <summary>A dangling file or directory symbolic link.</summary>
    BrokenSymlink,
    /// <summary>A directory whose listing could not be read (e.g. permission denied).</summary>
    UnreadableDirectory,
}

/// <summary>
/// A file or directory was skipped during enumeration and never scanned/backed up. A pruned directory
/// is a single event (its contents are never enumerated).
/// </summary>
/// <param name="RelativePath">Relative path of the skipped entry.</param>
/// <param name="Reason">Why it was skipped.</param>
public sealed record EntrySkippedEvent(RelativePath RelativePath, SkipReason Reason) : INotification;

/// <summary>A chunk upload started.</summary>
public sealed record ChunkUploadingEvent(ChunkHash ChunkHash, long Size) : INotification;

/// <summary>A chunk upload completed.</summary>
public sealed record ChunkUploadedEvent(ChunkHash ChunkHash, long StoredSize) : INotification;

/// <summary>A tar bundle is being sealed.</summary>
/// <param name="EntryCount">Number of entries in the sealed tar.</param>
/// <param name="UncompressedSize">Total uncompressed size of all entries in bytes (sum of file contents, excluding tar metadata).</param>
/// <param name="TarByteSize">Size in bytes of the sealed tar archive itself, including per-entry tar headers and padding. This is the number of bytes streamed during upload, so it is the correct denominator for upload progress.</param>
/// <param name="TarHash">Content hash of the sealed tar archive file (used as the bundle identifier).</param>
/// <param name="ContentHashes">Content hash of every file entry in the tar, in insertion order.</param>
public sealed record TarBundleSealingEvent(int EntryCount, long UncompressedSize, long TarByteSize, ChunkHash TarHash, IReadOnlyList<ContentHash> ContentHashes) : INotification;

/// <summary>A tar bundle was uploaded.</summary>
public sealed record TarBundleUploadedEvent(ChunkHash TarHash, long StoredSize, int EntryCount) : INotification;

/// <summary>Snapshot creation complete.</summary>
public sealed record SnapshotCreatedEvent(FileTreeHash RootHash, DateTimeOffset Timestamp, long FileCount) : INotification;

/// <summary>The tar builder started accumulating a new tar bundle.</summary>
public sealed record TarBundleStartedEvent() : INotification;

/// <summary>A file was written to the current tar bundle.</summary>
/// <param name="ContentHash">Content hash of the file added.</param>
/// <param name="CurrentEntryCount">Number of entries in the current tar bundle so far.</param>
/// <param name="CurrentTarSize">Cumulative uncompressed size of the current tar bundle in bytes.</param>
public sealed record TarEntryAddedEvent(ContentHash ContentHash, int CurrentEntryCount, long CurrentTarSize) : INotification;
