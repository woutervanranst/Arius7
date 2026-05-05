using Arius.Core.Shared.Paths;

namespace Arius.Tests.Shared.IO;

public static class FileSystemHelper
{
    public static async Task CopyDirectoryAsync(LocalRootPath sourceRootPath, LocalRootPath targetRootPath, CancellationToken cancellationToken = default)
    {
        if (targetRootPath.ExistsDirectory)
            targetRootPath.DeleteDirectory(recursive: true);

        targetRootPath.CreateDirectory();

        foreach (var directoryPath in Directory.EnumerateDirectories(sourceRootPath.ToString(), "*", SearchOption.AllDirectories))
        {
            var relativePath = sourceRootPath.GetRelativePath(directoryPath);
            (targetRootPath / relativePath).CreateDirectory();
        }

        foreach (var filePath in (sourceRootPath / RelativePath.Root).EnumerateFiles(searchOption: SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await filePath.CopyToAsync(filePath.RelativePath.RootedAt(targetRootPath), overwrite: true, cancellationToken);
        }
    }
}
