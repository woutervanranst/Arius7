using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;

namespace Arius.E2E.Tests.Datasets;

internal static class SyntheticRepositoryStateAssertions
{
    public static async Task AssertMatchesDiskTreeAsync(SyntheticRepositoryState expected, string rootPath, IEncryptionService encryption, bool includePointerFiles)
    {
        var actual = new Dictionary<RelativePath, ContentHash>();

        foreach (var filePath in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = RelativePath.FromPlatformRelativePath(Path.GetRelativePath(rootPath, filePath));

            if (!includePointerFiles && relativePath.ToString().EndsWith(".pointer.arius", StringComparison.Ordinal))
                continue;

            await using var stream = File.OpenRead(filePath);
            actual[relativePath] = await encryption.ComputeHashAsync(stream);
        }

        actual.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal).ToArray()
            .ShouldBe(expected.Files.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal).ToArray());
    }
}
