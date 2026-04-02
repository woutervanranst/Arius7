using Mediator;

namespace Arius.Core.Features.Archive;

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