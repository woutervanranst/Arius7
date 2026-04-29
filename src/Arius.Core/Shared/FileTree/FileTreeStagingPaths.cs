using System.Security.Cryptography;
using System.Text;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.FileTree;

internal static class FileTreeStagingPaths
{
    public static string GetDirectoryId(string directoryPath)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);

        var canonicalPath = directoryPath.Replace('\\', '/').Trim('/');
        return HashCodec.ToLowerHex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath)));
    }

    public static string GetNodeDirectory(string stagingRoot, string directoryId)
    {
        ArgumentException.ThrowIfNullOrEmpty(stagingRoot);
        ArgumentException.ThrowIfNullOrEmpty(directoryId);

        return Path.Combine(stagingRoot, "dirs", directoryId[..2], directoryId);
    }
}
