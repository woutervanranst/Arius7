using Arius.Core.Shared.Encryption;

namespace Arius.E2E.Tests.Datasets;

internal static class SyntheticRepositoryStateAssertions
{
    public static async Task AssertMatchesDiskTreeAsync(
        SyntheticRepositoryState expected,
        string rootPath)
    {
        await AssertMatchesDiskTreeAsync(expected, rootPath, includePointerFiles: true);
    }

    public static async Task AssertMatchesDiskTreeAsync(
        SyntheticRepositoryState expected,
        string rootPath,
        bool includePointerFiles)
    {
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        var hasher = new PlaintextPassthroughService();

        foreach (var filePath in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(rootPath, filePath)
                .Replace(Path.DirectorySeparatorChar, '/');

            if (!includePointerFiles && relativePath.EndsWith(".pointer.arius", StringComparison.Ordinal))
                continue;

            var bytes = await hasher.ComputeHashAsync(File.OpenRead(filePath));
            actual[relativePath] = Convert.ToHexString(bytes);
        }

        actual.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray()
            .ShouldBe(expected.Files.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray());
    }
}
