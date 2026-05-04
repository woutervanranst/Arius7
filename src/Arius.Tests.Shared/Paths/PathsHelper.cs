using Arius.Core.Shared.Paths;

namespace Arius.Tests.Shared.Paths;

public static class PathsHelper
{
    public static RelativePath PathOf(string value) => RelativePath.Parse(value);
}
