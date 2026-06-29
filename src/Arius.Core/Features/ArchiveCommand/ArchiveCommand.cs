using Arius.Core.Shared.Storage;
using Mediator;

namespace Arius.Core.Features.ArchiveCommand;

/// <summary>
/// Mediator command: archive a local directory to blob storage.
/// </summary>
public sealed record ArchiveCommand(ArchiveCommandOptions CommandOptions) : ICommand<ArchiveResult>;

/// <summary>
/// Options controlling the archive pipeline.
/// </summary>
public sealed record ArchiveCommandOptions
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
    public BlobTier UploadTier { get; init; } = BlobTier.Archive;

    /// <summary>If <c>true</c>, delete local binary files after a successful snapshot.</summary>
    public bool RemoveLocal { get; init; } = false;

    /// <summary>If <c>true</c>, do not create or update <c>.pointer.arius</c> files.</summary>
    public bool NoPointers { get; init; } = false;

    /// <summary>If <c>true</c>, skip re-reading a binary whose content the hashcache verifies as unchanged.</summary>
    public bool FastHash { get; init; } = false;

    /// <summary>
    /// Optional factory invoked when a file begins hashing.
    /// Parameters: relative path, file size in bytes.
    /// Returns an <see cref="IProgress{T}"/> that receives cumulative bytes hashed.
    /// When <c>null</c>, no byte-level progress is reported for hashing.
    /// </summary>
    public Func<RelativePath, long, IProgress<long>>? CreateHashProgress { get; init; }

    /// <summary>
    /// Optional factory invoked when a chunk begins uploading.
    /// Parameters: chunk hash, uncompressed size in bytes.
    /// Returns an <see cref="IProgress{T}"/> that receives cumulative bytes read from the source stream.
    /// When <c>null</c>, no byte-level progress is reported for uploads.
    /// </summary>
    public Func<ChunkHash, long, IProgress<long>>? CreateUploadProgress { get; init; }

    /// <summary>
    /// Optional callback invoked after the hash-stage input channel is created.
    /// The argument is a getter that returns the current number of pending items in the hash queue.
    /// The CLI stores this getter and polls it during display updates.
    /// </summary>
    public Action<Func<int>>? OnHashQueueReady { get; init; }

    /// <summary>
    /// Optional callback invoked after the upload-stage input channels are created.
    /// The argument is a getter that returns the combined depth of the large-file and sealed-tar queues.
    /// The CLI stores this getter and polls it during display updates.
    /// </summary>
    public Action<Func<int>>? OnUploadQueueReady { get; init; }
}

/// <summary>
/// Result returned by <see cref="ArchiveCommand"/>.
/// </summary>
public sealed record ArchiveResult
{
    /// <summary>
    /// <c>true</c> if the pipeline ran to completion. <c>false</c> on option validation failure,
    /// failure to open the staging session, or any unhandled pipeline exception; see <see cref="ErrorMessage"/>.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>Total number of files enumerated from the source directory, before deduplication.</summary>
    public required long FilesScanned { get; init; }

    /// <summary>
    /// Number of entries excluded during enumeration — excluded by the configured filter (name or
    /// System/Hidden attribute), a broken symlink, or an unreadable directory. A pruned directory counts
    /// as one (its contents are never enumerated). These never enter <see cref="FilesScanned"/>.
    /// </summary>
    public required long EntriesExcluded { get; init; }

    /// <summary>
    /// Number of files whose content was uploaded during this run (one per new large file plus one per
    /// new entry bundled into a tar). Excludes files skipped by deduplication (<see cref="FilesDeduped"/>).
    /// </summary>
    public required long FilesUploaded { get; init; }

    /// <summary>
    /// Number of files skipped because their content was already present — either in the chunk index from a
    /// prior run or already queued earlier in this run (includes pointer-only files resolved to an existing chunk).
    /// </summary>
    public required long FilesDeduped { get; init; }

    /// <summary>Sum of original (uncompressed) sizes of all files in the snapshot, in bytes.</summary>
    public required long OriginalSize { get; init; }

    /// <summary>Original (uncompressed) bytes newly uploaded during this run, in bytes.</summary>
    public required long IncrementalSize { get; init; }

    /// <summary>Stored (compressed + encrypted) bytes newly written to storage during this run, in bytes.</summary>
    public required long IncrementalStoredSize { get; init; }

    /// <summary>
    /// Root hash of the snapshot's file tree. <c>null</c> when no snapshot was produced — an empty source
    /// tree, or a failure before the tree was built.
    /// </summary>
    public required FileTreeHash? RootHash { get; init; }

    /// <summary>
    /// Timestamp of the snapshot: the existing snapshot's timestamp when the tree is unchanged, or the
    /// newly created snapshot's timestamp otherwise. Falls back to the current time when no snapshot exists.
    /// </summary>
    public required DateTimeOffset SnapshotTime { get; init; }

    /// <summary>Human-readable error description when <see cref="Success"/> is <c>false</c>; otherwise <c>null</c>.</summary>
    public string? ErrorMessage { get; init; }
}
