namespace Arius.Core.Shared.ChunkIndex;

internal sealed record LoadedPrefixState(
    PathSegment Prefix,
    bool RemoteExists,
    string? RemoteBlobIdentity,
    string ValidatedSnapshotIdentity);
