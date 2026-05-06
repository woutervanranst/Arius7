using Arius.Core.Shared;

namespace Arius.Tests.Shared;

public static class RepositoryCachePaths
{
    public static string GetRepositoryDirectory(string accountName, string containerName) =>
        RepositoryPaths.GetRepositoryRoot(accountName, containerName).ToString();

    public static string GetChunkIndexCacheDirectory(string accountName, string containerName) =>
        RepositoryPaths.GetChunkIndexCacheRoot(accountName, containerName).ToString();

    public static string GetFileTreeCacheDirectory(string accountName, string containerName) =>
        RepositoryPaths.GetFileTreeCacheRoot(accountName, containerName).ToString();

    public static string GetSnapshotCacheDirectory(string accountName, string containerName) =>
        RepositoryPaths.GetSnapshotCacheRoot(accountName, containerName).ToString();
}
