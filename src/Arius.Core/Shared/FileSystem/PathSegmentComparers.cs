namespace Arius.Core.Shared.FileSystem;

internal sealed class PathSegmentOrdinalIgnoreCaseComparer : IComparer<PathSegment>
{
    public static PathSegmentOrdinalIgnoreCaseComparer Instance { get; } = new();

    public int Compare(PathSegment x, PathSegment y) =>
        x.Compare(y, StringComparer.OrdinalIgnoreCase);
}

internal sealed class PathSegmentOrdinalComparer : IComparer<PathSegment>
{
    public static PathSegmentOrdinalComparer Instance { get; } = new();

    public int Compare(PathSegment x, PathSegment y) => x.Compare(y, StringComparer.Ordinal);
}
