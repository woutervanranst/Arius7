using Arius.Core.Shared.Paths;

namespace Arius.Core.Shared;

internal static class RepositoryRelativePath
{
    public static void ValidateCanonical(string path, bool allowEmpty = false)
        => RelativePath.Parse(path, allowEmpty);
}
