namespace Arius.Core.Shared.FileSystem;

internal readonly record struct RootedFileEntry(
    RootedPath Path,
    PathSegment Name,
    long Length,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc);
