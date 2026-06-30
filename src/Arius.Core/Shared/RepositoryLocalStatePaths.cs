namespace Arius.Core.Shared;

/// <summary>
/// Builds repository-level cache roots under the user's <c>.arius</c> home.
///
/// This helper owns the top-level on-disk layout for one repository identity
/// (<c>accountName</c> + <c>containerName</c>).
///
/// Example repository root:
/// <c>~/.arius/<container>/</c>
/// </summary>
public static class RepositoryLocalStatePaths
{
    private static readonly PathSegment chunkIndexCache = PathSegment.Parse("chunk-index");
    private static readonly PathSegment fileTreeCache   = PathSegment.Parse("filetrees");
    private static readonly PathSegment snapshotCache   = PathSegment.Parse("snapshots");
    private static readonly PathSegment hashCache       = PathSegment.Parse("hash");
    private static readonly PathSegment logs            = PathSegment.Parse("logs");

    internal static LocalDirectory GetAriusRoot()
        => LocalDirectory.Parse(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) / PathSegment.Parse(".arius");

    internal static LocalDirectory GetRepositoryRoot(string accountName, string containerName) 
        => GetAriusRoot() / GetRepositoryDirectoryName(accountName, containerName);

    /// <summary>
    /// Returns the stable repository directory name for one archive/container pair.
    /// Example: <c>archive-container</c>
    /// </summary>
    private static PathSegment GetRepositoryDirectoryName(string accountName, string containerName) => PathSegment.Parse($"{accountName}-{containerName}");

    internal static LocalDirectory GetChunkIndexCacheRoot(string accountName, string containerName) => GetRepositoryRoot(accountName, containerName) / chunkIndexCache;
    internal static LocalDirectory GetFileTreeCacheRoot(string accountName, string containerName)   => GetRepositoryRoot(accountName, containerName) / fileTreeCache;
    internal static LocalDirectory GetSnapshotCacheRoot(string accountName, string containerName)   => GetRepositoryRoot(accountName, containerName) / snapshotCache;
    internal static LocalDirectory GetHashCacheRoot(string accountName, string containerName)       => GetRepositoryRoot(accountName, containerName) / hashCache;
    public static   string         GetLogsDirectory(string accountName, string containerName)            => (GetRepositoryRoot(accountName, containerName) / logs).ToString();
}
