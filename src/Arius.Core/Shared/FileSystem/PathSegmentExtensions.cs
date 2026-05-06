namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Provides derived metadata helpers for canonical repository path segments.
///
/// These helpers keep string-derived path facts attached to the typed path model.
/// </summary>
public static class PathSegmentExtensions
{
    extension(PathSegment segment)
    {
        /// <summary>Returns the file extension portion of this path segment.</summary>
        public string Extension => Path.GetExtension(segment.ToString());
    }
}
