using Arius.Core.Shared.FileSystem;

namespace Arius.Tests.Shared;

public static class RelativePathExtensions
{
    extension(RelativePath)
    {
        public static RelativePath operator /(RelativePath rp, string segment) => rp / PathSegment.Parse(segment);
    }
}