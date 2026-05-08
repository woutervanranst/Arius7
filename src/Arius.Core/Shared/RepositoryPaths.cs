using Arius.Core.Shared.FileSystem;

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
public static class RepositoryPaths
{
    internal static RelativePath ChunkIndexCacheRelativePath { get; } = RelativePath.Parse("chunk-index");
    internal static RelativePath FileTreeCacheRelativePath   { get; } = RelativePath.Parse("filetrees");
    internal static RelativePath SnapshotCacheRelativePath   { get; } = RelativePath.Parse("snapshots");
    internal static RelativePath LogsRelativePath            { get; } = RelativePath.Parse("logs");

    /// <summary>
    /// Returns the stable repository directory name for one archive/container pair.
    /// Example: <c>archive-container</c>
    /// </summary>
    public static PathSegment GetRepositoryDirectoryName(string accountName, string containerName) =>
        PathSegment.Parse($"{accountName}-{containerName}");

    internal static PathSegment GetRepoDirectoryName(string accountName, string containerName) =>
        GetRepositoryDirectoryName(accountName, containerName);

    internal static LocalDirectory GetRepositoryRoot(string accountName, string containerName)
    {
        var homeRoot = LocalDirectory.Parse(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return LocalDirectory.Parse(homeRoot.Resolve(RelativePath.Root / ".arius" / GetRepoDirectoryName(accountName, containerName)));
    }

    internal static LocalDirectory GetChunkIndexCacheRoot(string accountName, string containerName) =>
        LocalDirectory.Parse(GetRepositoryRoot(accountName, containerName).Resolve(ChunkIndexCacheRelativePath));

    internal static LocalDirectory GetFileTreeCacheRoot(string accountName, string containerName) =>
        LocalDirectory.Parse(GetRepositoryRoot(accountName, containerName).Resolve(FileTreeCacheRelativePath));

    internal static LocalDirectory GetSnapshotCacheRoot(string accountName, string containerName) =>
        LocalDirectory.Parse(GetRepositoryRoot(accountName, containerName).Resolve(SnapshotCacheRelativePath));

    internal static LocalDirectory GetLogsRoot(string accountName, string containerName) =>
        LocalDirectory.Parse(GetRepositoryRoot(accountName, containerName).Resolve(LogsRelativePath));
}
