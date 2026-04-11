namespace Arius.Core.Shared;

public static class RepositoryPaths
{
    public static string GetRepoDirectoryName(string accountName, string containerName) => $"{accountName}-{containerName}";

    public static string GetRepositoryDirectory(string accountName, string containerName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".arius", GetRepoDirectoryName(accountName, containerName));
    }

    public static string GetChunkIndexCacheDirectory(string accountName, string containerName) =>
        Path.Combine(GetRepositoryDirectory(accountName, containerName), "chunk-index");

    public static string GetFileTreeCacheDirectory(string accountName, string containerName) =>
        Path.Combine(GetRepositoryDirectory(accountName, containerName), "filetrees");

    public static string GetSnapshotCacheDirectory(string accountName, string containerName) =>
        Path.Combine(GetRepositoryDirectory(accountName, containerName), "snapshots");

    public static string GetLogsDirectory(string accountName, string containerName) =>
        Path.Combine(GetRepositoryDirectory(accountName, containerName), "logs");
}
