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
/// <param name="RelativePath">Forward-slash relative path of the file.</param>
/// <param name="ContentHash">The content hash of the file.</param>
/// <param name="FastHashReused">
/// <c>true</c> when the hash was served from the hashcache without reading the file
/// (only possible when <c>--fast-hash</c> is on and the cache entry was valid).
/// </param>
/// <param name="FastHashRehashed">
/// <c>true</c> when the file was fully read and its hash recorded to the hashcache.
/// <c>false</c> for pointer-only files (hash taken from the pointer) and for cache hits.
/// </param>
public sealed record FileHashedEvent(RelativePath RelativePath, ContentHash ContentHash, bool FastHashReused, bool FastHashRehashed) : INotification;

/// <summary>
/// An already-scanned file was dropped <i>during</i> the pipeline because it could no longer be
/// read/opened (deleted, permission revoked, or broken mid-run) at hashing or upload time. It had a
/// prior <see cref="FileScannedEvent"/>, so consumers use it to clear that file's progress row.
/// Contrast <see cref="EntryExcludedEvent"/>, which fires at enumeration before a file is ever scanned.
/// </summary>
/// <param name="RelativePath">Relative path of the dropped file; used to clear its progress row.</param>
public sealed record FileSkippedEvent(RelativePath RelativePath) : INotification;

/// <summary>Why a file or directory was excluded at enumeration (before it ever entered the pipeline).</summary>
public enum ExclusionReason
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
/// A file or directory was excluded at <i>enumeration</i> — before it ever entered the pipeline — so it is
/// never scanned, hashed, uploaded, or placed in the snapshot, and (unlike <see cref="FileSkippedEvent"/>)
/// it never had a <see cref="FileScannedEvent"/>. A pruned directory raises a single event; its contents
/// are never enumerated. The handler tallies these into <c>ArchiveResult.EntriesExcluded</c>.
/// </summary>
/// <param name="RelativePath">Relative path of the excluded entry (a file, or a pruned directory).</param>
/// <param name="Reason">Why it was excluded.</param>
public sealed record EntryExcludedEvent(RelativePath RelativePath, ExclusionReason Reason) : INotification;

/// <summary>A chunk upload started.</summary>
public sealed record ChunkUploadingEvent(ChunkHash ChunkHash, long Size) : INotification;

/// <summary>A chunk upload completed.</summary>
/// <param name="ChunkHash">Content hash of the uploaded chunk.</param>
/// <param name="StoredSize">Bytes written to storage (compressed + encrypted); the upload denominator.</param>
/// <param name="OriginalSize">
/// Uncompressed size in bytes of the chunk's source content, so a byte-progress consumer can express the
/// uploaded layer in the same original-dataset units as the scanned/hashed layers (stored size understates
/// progress because of compression). For a large chunk this is the file's original size; tar bundles carry
/// their own uncompressed total on <see cref="TarBundleSealingEvent"/>.
/// </param>
public sealed record ChunkUploadedEvent(ChunkHash ChunkHash, long StoredSize, long OriginalSize) : INotification;

/// <summary>
/// A hashed file's content was found already stored — a hit in the chunk index or the in-run in-flight-hashes
/// set at the Dedup + Router stage — so it is <i>not</i> re-uploaded and contributes only a filetree entry.
/// Consumers tally it as deduplicated (bytes not re-uploaded). Contrast <see cref="ChunkUploadedEvent"/>,
/// which fires for content that <i>is</i> uploaded.
/// </summary>
/// <param name="ContentHash">Content hash of the deduplicated file (already present in the repository).</param>
/// <param name="OriginalSize">Uncompressed size in bytes of the file whose content was not re-uploaded.</param>
public sealed record FileDedupedEvent(ContentHash ContentHash, long OriginalSize) : INotification;

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
