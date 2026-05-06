using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;
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
    public required LocalRootPath RootDirectory { get; init; }

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

    /// <summary>
    /// Optional factory invoked when a file begins hashing.
    /// Parameters: relative path, file size in bytes.
    /// Returns an <see cref="IProgress{T}"/> that receives cumulative bytes hashed.
    /// When <c>null</c>, no byte-level progress is reported for hashing.
    /// </summary>
    public Func<string, long, IProgress<long>>? CreateHashProgress { get; init; }

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
    public required bool Success { get; init; }
    public required long FilesScanned { get; init; }
    public required long FilesUploaded { get; init; }
    public required long FilesDeduped { get; init; }
    public required long TotalSize { get; init; }
    public required FileTreeHash? RootHash { get; init; }
    public required DateTimeOffset SnapshotTime { get; init; }
    public string? ErrorMessage { get; init; }
}
