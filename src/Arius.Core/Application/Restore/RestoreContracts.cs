using Arius.Core.Application.Abstractions;
using Arius.Core.Models;

namespace Arius.Core.Application.Restore;

public sealed record RestoreRequest(
    string ConnectionString,
    string ContainerName,
    string Passphrase,
    string SnapshotId,
    string TargetPath,
    string? Include = null,
    ParallelismOptions? Parallelism = null,
    string? TempPath = null) : IStreamRequest<RestoreEvent>;

public abstract record RestoreEvent;

public sealed record RestorePlanReady(int TotalFiles, long TotalBytes, int PacksToDownload) : RestoreEvent;

public sealed record RestoreFileRestored(string Path, long Size) : RestoreEvent;

/// <summary>Emitted when a file cannot be restored; operation continues with remaining files.</summary>
public sealed record RestoreFileError(string Path, string Error) : RestoreEvent;

/// <summary>Emitted after each pack is downloaded and extracted to the temp directory.</summary>
public sealed record RestorePackFetched(string PackId, int BlobCount) : RestoreEvent;

public sealed record RestoreCompleted(int RestoredFiles, long RestoredBytes, int Failed) : RestoreEvent;
