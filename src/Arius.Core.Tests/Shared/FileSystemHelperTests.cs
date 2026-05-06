using Arius.Core.Shared.FileSystem;
using Arius.Tests.Shared.Helpers;

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

    [Test]
    public async Task CopyDirectoryAsync_CopiesMultipleNestedFiles()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"arius-copy-dir-multi-{Guid.NewGuid():N}");
        var sourceText = Path.Combine(tempRoot, "source");
        var targetText = Path.Combine(tempRoot, "target");

        Directory.CreateDirectory(tempRoot);

        try
        {
            var sourceRoot = LocalRootPath.Parse(sourceText);
            var targetRoot = LocalRootPath.Parse(targetText);
            var sourceDocs = sourceRoot / PathOf("docs");
            var sourceDeep = sourceRoot / PathOf("docs/nested");
            var targetFiles = new[]
            {
                targetRoot / PathOf("docs/a.txt"),
                targetRoot / PathOf("docs/nested/b.txt")
            };

            sourceDocs.CreateDirectory();
            sourceDeep.CreateDirectory();
            await (sourceRoot / PathOf("docs/a.txt")).WriteAllTextAsync("a");
            await (sourceRoot / PathOf("docs/nested/b.txt")).WriteAllTextAsync("b");

            await FileSystemHelper.CopyDirectoryAsync(sourceRoot, targetRoot);

            targetFiles.All(path => path.ExistsFile).ShouldBeTrue();
            targetFiles.Select(path => path.ReadAllText()).OrderBy(x => x, StringComparer.Ordinal).ToArray().ShouldBe(["a", "b"]);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
