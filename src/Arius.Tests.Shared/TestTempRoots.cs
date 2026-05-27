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
    public static void CleanupOldTempDirs()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), FolderName);
        if (!Directory.Exists(tempRoot))
            return;

        foreach (var directory in Directory.EnumerateDirectories(tempRoot, "*", SearchOption.TopDirectoryOnly).ToArray())
        {
            var lastWriteUtc = Directory.GetLastWriteTimeUtc(directory);
            if (DateTime.UtcNow - lastWriteUtc <= TimeSpan.FromHours(24))
                continue;

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException exception)
            {
                // Another test process may still be touching the directory; leave it for a later cleanup pass.
                Console.Error.WriteLine($"Skipping temp cleanup for '{directory}': {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                // Antivirus or another process can temporarily block deletion; leave it for a later cleanup pass.
                Console.Error.WriteLine($"Skipping temp cleanup for '{directory}': {exception.Message}");
            }
        }
    }
}
