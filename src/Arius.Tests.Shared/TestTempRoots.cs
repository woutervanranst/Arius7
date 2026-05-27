using Arius.Core.Shared.FileSystem;

namespace Arius.Tests.Shared;

internal static class TestTempRoots
{
    public const string FolderName = "arius";

    public static LocalDirectory CreateDirectory(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        return LocalDirectory.Parse(Path.Combine(Path.GetTempPath(), FolderName, $"{prefix}-{Guid.NewGuid():N}"));
    }

    [After(TestSession)]
    public static void CleanupAllTempDirs()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), FolderName);
        if (Directory.Exists(tempRoot))
            Directory.Delete(tempRoot, recursive: true);
    }
}
