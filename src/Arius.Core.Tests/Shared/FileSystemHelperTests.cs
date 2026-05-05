using Arius.Core.Shared.Paths;
using Arius.Tests.Shared.IO;

namespace Arius.Core.Tests.Shared;

public class FileSystemHelperTests
{
    [Test]
    public async Task CopyDirectoryAsync_CopiesFiles_Subdirectories_And_Timestamps()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"arius-copy-dir-{Guid.NewGuid():N}");
        var sourceText = Path.Combine(tempRoot, "source");
        var targetText = Path.Combine(tempRoot, "target");

        Directory.CreateDirectory(tempRoot);

        try
        {
            var sourceRoot = LocalRootPath.Parse(sourceText);
            var targetRoot = LocalRootPath.Parse(targetText);
            var sourceDirectory = sourceRoot / PathOf("docs");
            var sourceFile = sourceRoot / PathOf("docs/readme.txt");
            var targetFile = targetRoot / PathOf("docs/readme.txt");

            sourceDirectory.CreateDirectory();
            await sourceFile.WriteAllTextAsync("hello directory copy");

            var modified = new DateTime(2024, 8, 9, 10, 11, 12, DateTimeKind.Utc);
            sourceFile.LastWriteTimeUtc = modified;

            await FileSystemHelper.CopyDirectoryAsync(sourceRoot, targetRoot);

            targetRoot.ExistsDirectory.ShouldBeTrue();
            targetFile.ExistsFile.ShouldBeTrue();
            targetFile.ReadAllText().ShouldBe("hello directory copy");
            targetFile.LastWriteTimeUtc.ShouldBe(modified);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
