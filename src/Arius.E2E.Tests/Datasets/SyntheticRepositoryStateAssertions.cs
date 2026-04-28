using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;

namespace Arius.E2E.Tests.Datasets;

internal static class SyntheticRepositoryStateAssertions
{
    public static async Task AssertMatchesDiskTreeAsync(SyntheticRepositoryState expected, string rootPath, IEncryptionService encryption, bool includePointerFiles)
    {
        var actual = new Dictionary<string, ContentHash>(StringComparer.Ordinal);

        foreach (var filePath in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(rootPath, filePath).Replace(Path.DirectorySeparatorChar, '/');

            if (!includePointerFiles && relativePath.EndsWith(".pointer.arius", StringComparison.Ordinal))
                continue;

            await using var stream = File.OpenRead(filePath);
            actual[relativePath] = await encryption.ComputeHashAsync(stream);
        }

        actual.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray()
            .ShouldBe(expected.Files.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray());
    }
}
