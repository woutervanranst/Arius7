using System.Security.Cryptography;
using System.Text;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.FileTree;

internal static class FileTreeStagingPaths
{
    public static string GetDirectoryId(string directoryPath)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);

        var canonicalPath = directoryPath.Replace('\\', '/');
        if (IsAbsolutePath(canonicalPath))
            throw new ArgumentException("Directory path must be relative.", nameof(directoryPath));

        canonicalPath = canonicalPath.TrimEnd('/');
        return HashCodec.ToLowerHex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath)));

        static bool IsAbsolutePath(string path)
            => path.StartsWith('/')
               || Path.IsPathRooted(path)
               || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/');
    }

    public static string GetNodeDirectory(string stagingRoot, string directoryId)
    {
        ArgumentException.ThrowIfNullOrEmpty(stagingRoot);
        ArgumentException.ThrowIfNullOrEmpty(directoryId);

        return Path.Combine(stagingRoot, "dirs", directoryId[..2], directoryId);
    }
}
