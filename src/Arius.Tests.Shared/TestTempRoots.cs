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
        Directory.Delete(Path.Combine(Path.GetTempPath(), FolderName), recursive: true);
    }
}
