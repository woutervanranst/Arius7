using System.Security.Cryptography;
using System.Text;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Paths;

namespace Arius.Core.Shared.FileTree;

/// <summary>
/// Builds filetree-specific disk paths under the repository's filetree cache directory.
/// </summary>
internal static class FileTreePaths
{
    private static readonly RelativePath StagingDirectoryRelativePath = RelativePath.Parse(".staging");
    private static readonly RelativePath StagingLockRelativePath      = RelativePath.Parse(".staging.lock");

    // -- FileTree Cache Paths ---

    /// <summary>
    /// Returns the persisted cache file path for one filetree hash.
    /// Example: <c>~/.arius/<container>/filetrees/0123abcd...</c>
    /// </summary>
    public static string GetCachePath(LocalRootPath fileTreeCacheDirectory, FileTreeHash hash)
        => GetCachePath(fileTreeCacheDirectory.ToString(), hash);

    public static string GetCachePath(string fileTreeCacheDirectory, FileTreeHash hash)
        => GetCachePath(fileTreeCacheDirectory, hash.ToString());

    /// <summary>
    /// Returns the persisted cache file path for one filetree hash text.
    /// Example: <c>~/.arius/<container>/filetrees/0123abcd...</c>
    /// </summary>
    public static string GetCachePath(LocalRootPath fileTreeCacheDirectory, string hashText)
        => GetCachePath(fileTreeCacheDirectory.ToString(), hashText);

    public static string GetCachePath(string fileTreeCacheDirectory, string hashText)
        => Path.Combine(fileTreeCacheDirectory, hashText);


    // -- FileTree Staging Paths ---

    /// <summary>
    /// Returns the deterministic staging directory id for one relative directory path.
    /// Example output usage: <c>~/.arius/<container>/filetrees/.staging/&lt;directoryId&gt;</c>
    /// </summary>
    public static string GetStagingDirectoryId(RelativePath directoryPath)
    {
        return HashCodec.ToLowerHex(SHA256.HashData(Encoding.UTF8.GetBytes(directoryPath.ToString())));
    }

    /// <summary>
    /// Returns the staging node file path for one staged directory id.
    /// Example: <c>~/.arius/<container>/filetrees/.staging/&lt;directoryId&gt;</c>
    /// </summary>
    public static RootedPath GetStagingNodePath(LocalRootPath stagingRoot, string directoryId)
        => stagingRoot / RelativePath.Parse(directoryId);

    /// <summary>
    /// Returns the root directory used by the active filetree staging session.
    /// Example: <c>~/.arius/<container>/filetrees/.staging/</c>
    /// </summary>
    public static LocalRootPath GetStagingRootDirectory(LocalRootPath fileTreeCacheDirectory)
        => LocalRootPath.Parse((fileTreeCacheDirectory / StagingDirectoryRelativePath).FullPath);

    /// <summary>
    /// Returns the process lock file path that guards a single active filetree staging session.
    /// Example: <c>~/.arius/<container>/filetrees/.staging.lock</c>
    /// </summary>
    public static RootedPath GetStagingLockPath(LocalRootPath fileTreeCacheDirectory)
        => fileTreeCacheDirectory / StagingLockRelativePath;
}
