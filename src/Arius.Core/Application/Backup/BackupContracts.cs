using Arius.Core.Application.Abstractions;
using Arius.Core.Models;

namespace Arius.Core.Application.Backup;

public sealed record BackupRequest(string RepoPath, string Passphrase, IReadOnlyList<string> Paths) : IStreamRequest<BackupEvent>;

public abstract record BackupEvent;

public sealed record BackupStarted(int TotalFiles) : BackupEvent;

public sealed record BackupFileProcessed(string Path, long Size, bool IsDeduplicated) : BackupEvent;

public sealed record BackupCompleted(Snapshot Snapshot, int StoredFiles, int DeduplicatedFiles) : BackupEvent;
