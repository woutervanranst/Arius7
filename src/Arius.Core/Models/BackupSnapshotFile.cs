namespace Arius.Core.Models;

public sealed record BackupSnapshotFile(string Path, BlobHash BlobHash, long Size);
