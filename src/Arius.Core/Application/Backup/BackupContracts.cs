using Arius.Core.Application.Abstractions;
using Arius.Core.Models;

namespace Arius.Core.Application.Backup;

public sealed record BackupRequest(
    string ConnectionString,
    string ContainerName,
    string Passphrase,
    IReadOnlyList<string> Paths,
    BlobAccessTier DataTier = BlobAccessTier.Archive,
    ParallelismOptions? Parallelism = null) : IStreamRequest<BackupEvent>;

public abstract record BackupEvent;

public sealed record BackupStarted(int TotalFiles) : BackupEvent;

public sealed record BackupFileProcessed(string Path, long Size, bool IsDeduplicated) : BackupEvent;

/// <summary>Emitted when a file cannot be processed; operation continues with remaining files.</summary>
public sealed record BackupFileError(string Path, string Error) : BackupEvent;

public sealed record BackupCompleted(
    Snapshot? Snapshot,
    int StoredFiles,
    int DeduplicatedFiles,
    int Failed,
    long TotalChunks,
    long NewChunks,
    long DeduplicatedChunks,
    long TotalBytes,
    long NewBytes,
    long DeduplicatedBytes) : BackupEvent;
