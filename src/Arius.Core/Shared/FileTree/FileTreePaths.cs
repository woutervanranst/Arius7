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
    private const string StagingDirectoryName   = ".staging";
    private const string LockFileName           = ".staging.lock";

    // -- FileTree Cache Paths ---

    /// <summary>
    /// Returns the persisted cache file path for one filetree hash.
    /// Example: <c>~/.arius/<container>/filetrees/0123abcd...</c>
    /// </summary>
    public static string GetCachePath(string fileTreeCacheDirectory, FileTreeHash hash)
        => GetCachePath(fileTreeCacheDirectory, hash.ToString());

    /// <summary>
    /// Returns the persisted cache file path for one filetree hash text.
    /// Example: <c>~/.arius/<container>/filetrees/0123abcd...</c>
    /// </summary>
    public static string GetCachePath(string fileTreeCacheDirectory, string hashText) 
        => Path.Combine(fileTreeCacheDirectory, hashText);


    // -- FileTree Staging Paths ---

    /// <summary>
    /// Returns the deterministic staging directory id for one relative directory path.
    /// Example output usage: <c>~/.arius/<container>/filetrees/.staging/&lt;directoryId&gt;</c>
    /// </summary>
    public static string GetStagingDirectoryId(string directoryPath)
    {
        _ = RelativePath.Parse(directoryPath, allowEmpty: true);

        var canonicalPath = directoryPath;
        return HashCodec.ToLowerHex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath)));
    }

    /// <summary>
    /// Returns the staging node file path for one staged directory id.
    /// Example: <c>~/.arius/<container>/filetrees/.staging/&lt;directoryId&gt;</c>
    /// </summary>
    public static string GetStagingNodePath(string stagingRoot, string directoryId)
        => Path.Combine(stagingRoot, directoryId);

    /// <summary>
    /// Returns the root directory used by the active filetree staging session.
    /// Example: <c>~/.arius/<container>/filetrees/.staging/</c>
    /// </summary>
    public static string GetStagingRootDirectory(string fileTreeCacheDirectory) 
        => Path.Combine(fileTreeCacheDirectory, StagingDirectoryName);

    /// <summary>
    /// Returns the process lock file path that guards a single active filetree staging session.
    /// Example: <c>~/.arius/<container>/filetrees/.staging.lock</c>
    /// </summary>
    public static string GetStagingLockPath(string fileTreeCacheDirectory) 
        => Path.Combine(fileTreeCacheDirectory, LockFileName);
}
