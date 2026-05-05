using Arius.Core.Shared.Paths;

namespace Arius.Core.Tests.Shared;

public class RootedPathTests
{
    [Test]
    public void RootedOf_ComposesRelativePathUnderTypedRoot()
    {
        var rooted = RootedOf("C:/repo", "docs/readme.txt");

        rooted.Root.ShouldBe(RootOf("C:/repo"));
        rooted.RelativePath.ShouldBe(PathOf("docs/readme.txt"));
    }

    [Test]
    public void RootedAt_ComposesRootAndRelativePath()
    {
        var root = RootOf(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "arius-rooted")));
        var path = RelativePath.Parse("photos/2024/a.jpg");

        var rooted = path.RootedAt(root);

        rooted.Root.ShouldBe(root);
        rooted.RelativePath.ShouldBe(path);
        rooted.FullPath.ShouldBe(Path.Combine(root.ToString(), "photos", "2024", "a.jpg"));
    }

    [Test]
    public void SlashOperator_ComposesRootAndRelativePath()
    {
        var root = RootOf(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "arius-rooted-operator")));
        var path = PathOf("docs/readme.txt");

        var rooted = root / path;

        rooted.ShouldBe(new RootedPath(root, path));
        rooted.FullPath.ShouldBe(Path.Combine(root.ToString(), "docs", "readme.txt"));
    }

    [Test]
    public void GetRelativePath_RoundTripsAbsolutePathUnderRoot()
    {
        var root = RootOf(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "arius-roundtrip")));
        var fullPath = Path.Combine(root.ToString(), "docs", "readme.txt");

        root.GetRelativePath(fullPath).ShouldBe(RelativePath.Parse("docs/readme.txt"));
    }

    [Test]
    public async Task ExistsFile_ReadAllTextAsync_AndTimestampProperties_WorkAgainstFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"arius-rooted-io-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var root = RootOf(tempRoot);
            var directory = root / PathOf("docs");
            var path = root / PathOf("docs/readme.txt");

            directory.CreateDirectory();
            await path.WriteAllTextAsync("hello");

            path.ExistsFile.ShouldBeTrue();
            path.ReadAllText().ShouldBe("hello");
            (await path.ReadAllTextAsync()).ShouldBe("hello");
            path.Extension.ShouldBe(".txt");
            path.Length.ShouldBe(5);

            var modified = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            path.LastWriteTimeUtc = modified;
            path.LastWriteTimeUtc.ShouldBe(modified);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Test]
    public void ExistsDirectory_And_CreateDirectory_WorkAgainstDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"arius-rooted-dir-{Guid.NewGuid():N}");

        try
        {
            var root = RootOf(tempRoot);
            var path = root / PathOf("photos/2024");

            path.ExistsDirectory.ShouldBeFalse();

            path.CreateDirectory();

            path.ExistsDirectory.ShouldBeTrue();
            Directory.Exists(path.FullPath).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Test]
    public async Task CopyToAsync_CopiesContent_And_Timestamps()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"arius-rooted-copy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var root = RootOf(tempRoot);
            var sourceDirectory = root / PathOf("source");
            var targetDirectory = root / PathOf("target");
            var source = root / PathOf("source/file.txt");
            var destination = root / PathOf("target/file.txt");

            sourceDirectory.CreateDirectory();
            targetDirectory.CreateDirectory();
            await source.WriteAllTextAsync("copy-me");

            var modified = new DateTime(2024, 6, 7, 8, 9, 10, DateTimeKind.Utc);
            source.LastWriteTimeUtc = modified;

            await source.CopyToAsync(destination, overwrite: true);

            destination.ExistsFile.ShouldBeTrue();
            destination.ReadAllText().ShouldBe("copy-me");
            destination.LastWriteTimeUtc.ShouldBe(modified);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
