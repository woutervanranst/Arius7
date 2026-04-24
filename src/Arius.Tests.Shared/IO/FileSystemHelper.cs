namespace Arius.Tests.Shared.IO;

internal static class FileSystemHelper
{
    public static void CopyDirectory(string sourceRootPath, string targetRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRootPath);

        if (Directory.Exists(targetRootPath))
            Directory.Delete(targetRootPath, recursive: true);

        Directory.CreateDirectory(targetRootPath);

        foreach (var directoryPath in Directory.EnumerateDirectories(sourceRootPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRootPath, directoryPath);
            Directory.CreateDirectory(Path.Combine(targetRootPath, relativePath));
        }

        foreach (var filePath in Directory.EnumerateFiles(sourceRootPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRootPath, filePath);
            var targetPath = Path.Combine(targetRootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            File.Copy(filePath, targetPath, overwrite: true);
        }
    }
}
