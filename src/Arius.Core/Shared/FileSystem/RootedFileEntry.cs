namespace Arius.Core.Shared.FileSystem;

internal readonly record struct RootedFileEntry(RootedPath Path, long Length, DateTime LastWriteTimeUtc);
