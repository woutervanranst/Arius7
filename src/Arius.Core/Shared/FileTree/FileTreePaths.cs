using System.Security.Cryptography;
using System.Text;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.FileTree;

/// <summary>
/// Builds filetree-specific disk paths under the repository's filetree cache directory.
/// </summary>
internal static class FileTreePaths
{
    private const string StagingDirectoryName   = ".staging";
    private const string LockFileName           = ".staging.lock";
    private static readonly RelativePath StagingRootRelativePath = RelativePath.Parse(StagingDirectoryName);
    private static readonly RelativePath StagingLockRelativePath = RelativePath.Parse(LockFileName);

    // -- FileTree Cache Paths ---

    /// <summary>
    /// Returns the persisted cache file path for one filetree hash.
    /// Example: <c>~/.arius/<container>/filetrees/0123abcd...</c>
    /// </summary>
    public static RelativePath GetCachePath(FileTreeHash hash)
        => GetCachePath(hash.ToString());

    /// <summary>
    /// Returns the persisted cache file path for one filetree hash text.
    /// Example: <c>~/.arius/<container>/filetrees/0123abcd...</c>
    /// </summary>
    public static RelativePath GetCachePath(string hashText)
        => RelativePath.Parse(hashText);


    // -- FileTree Staging Paths ---

    /// <summary>
    /// Returns the deterministic staging directory id for one relative directory path.
    /// Example output usage: <c>~/.arius/<container>/filetrees/.staging/&lt;directoryId&gt;</c>
    /// </summary>
    public static PathSegment GetStagingDirectoryId(RelativePath directoryPath)
    {
        var canonicalPath = directoryPath == RelativePath.Root ? string.Empty : directoryPath.ToString();
        return PathSegment.Parse(HashCodec.ToLowerHex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath))));
    }

    /// <summary>
    /// Returns the staging node file path for one staged directory id.
    /// Example: <c>~/.arius/<container>/filetrees/.staging/&lt;directoryId&gt;</c>
    /// </summary>
    public static RelativePath GetStagingNodePath(PathSegment directoryId)
        => RelativePath.Root / directoryId;

    /// <summary>
    /// Returns the root directory used by the active filetree staging session.
    /// Example: <c>~/.arius/<container>/filetrees/.staging/</c>
    /// </summary>
    public static LocalDirectory GetStagingRootDirectory(LocalDirectory fileTreeCacheDirectory)
        => LocalDirectory.Parse(fileTreeCacheDirectory.Resolve(StagingRootRelativePath));

    /// <summary>
    /// Returns the process lock file path that guards a single active filetree staging session.
    /// Example: <c>~/.arius/<container>/filetrees/.staging.lock</c>
    /// </summary>
    public static RelativePath GetStagingLockPath()
        => StagingLockRelativePath;
}
