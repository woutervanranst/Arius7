namespace Arius.E2E.Tests.Datasets;

internal static class RepositoryTreeAssertions
{
    public static async Task AssertMatchesDiskTreeAsync(
        RepositoryTreeSnapshot expected,
        string rootPath)
    {
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var filePath in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(rootPath, filePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            var bytes = await File.ReadAllBytesAsync(filePath);
            actual[relativePath] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        }

        actual.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray()
            .ShouldBe(expected.Files.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray());
    }
}
