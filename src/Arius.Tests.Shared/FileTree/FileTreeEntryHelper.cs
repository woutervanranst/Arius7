using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Paths;

namespace Arius.Tests.Shared.FileTree;

public static class FileTreeEntryHelper
{
    public static FileEntry FileEntryOf(PathSegment name, ContentHash hash, DateTimeOffset created, DateTimeOffset modified) => new()
    {
        Name = name,
        ContentHash = hash,
        Created = created,
        Modified = modified
    };

    public static DirectoryEntry DirectoryEntryOf(PathSegment name, FileTreeHash hash) => new()
    {
        Name = name,
        FileTreeHash = hash
    };
}
