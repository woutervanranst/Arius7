using Arius.Core.Application.Abstractions;
using Arius.Core.Models;

namespace Arius.Core.Application.Restore;

public sealed record RestoreRequest(
    string ConnectionString,
    string ContainerName,
    string Passphrase,
    string SnapshotId,
    string TargetPath,
    string? Include = null) : IStreamRequest<RestoreEvent>;

public abstract record RestoreEvent;

public sealed record RestorePlanReady(int TotalFiles, long TotalBytes) : RestoreEvent;

public sealed record RestoreFileRestored(string Path, long Size) : RestoreEvent;

public sealed record RestoreCompleted(int RestoredFiles, long RestoredBytes) : RestoreEvent;
