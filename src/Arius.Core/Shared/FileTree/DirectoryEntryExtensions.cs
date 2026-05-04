using Arius.Core.Shared.Paths;

namespace Arius.Core.Shared.FileTree;

internal static class DirectoryEntryExtensions
{
    public static PathSegment GetDirectoryName(this DirectoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Name;
    }
}
