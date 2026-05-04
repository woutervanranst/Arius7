using Arius.Core.Shared.Paths;

namespace Arius.Tests.Shared.Paths;

public static class PathsHelper
{
    public static LocalRootPath RootOf(string value)    => LocalRootPath.Parse(value);
    public static RelativePath PathOf(string value)    => RelativePath.Parse(value);
    public static PathSegment  SegmentOf(string value) => PathSegment.Parse(value);
}
