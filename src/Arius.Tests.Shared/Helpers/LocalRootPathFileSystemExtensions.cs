using Arius.Core.Shared.Paths;

namespace Arius.Tests.Shared.Helpers;

public static class LocalRootPathExtensions
{
    extension(LocalRootPath root)
    {
        public LocalRootPath GetSubdirectoryRoot(string child) => root.GetSubdirectoryRoot(PathSegment.Parse(child));
    }
}
