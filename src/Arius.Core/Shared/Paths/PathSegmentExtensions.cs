namespace Arius.Core.Shared.Paths;

public static class PathSegmentExtensions
{
    extension(PathSegment segment)
    {
        public string Extension => Path.GetExtension(segment.ToString());
    }
}
