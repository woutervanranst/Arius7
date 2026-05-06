namespace Arius.Core.Shared.FileSystem;

public static class PathSegmentExtensions
{
    extension(PathSegment segment)
    {
        public string Extension => Path.GetExtension(segment.ToString());
    }
}
