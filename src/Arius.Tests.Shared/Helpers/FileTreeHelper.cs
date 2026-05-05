using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Paths;

namespace Arius.Tests.Shared.Helpers;

public static class FileTreeHelper
{
    public static FileEntry FileOf(string name, string hash, DateTimeOffset created, DateTimeOffset modified)
        => FileEntryOf(PathsHelper.SegmentOf(name), ContentHash.Parse(NormalizeHash(hash)), created, modified);

    public static DirectoryEntry DirOf(string name, string hash)
        => DirectoryEntryOf(PathsHelper.SegmentOf(name), FileTreeHash.Parse(NormalizeHash(hash)));

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

    private static string NormalizeHash(string hash)
        => hash.Length == 64 ? hash : hash[0].ToString().PadRight(64, char.ToLowerInvariant(hash[0]));
}
