using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.LocalFile;
using Arius.Core.Shared.Paths;

namespace Arius.E2E.Tests.Datasets;

internal static class SyntheticRepositoryStateAssertions
{
    public static async Task AssertMatchesDiskTreeAsync(SyntheticRepositoryState expected, LocalRootPath rootPath, IEncryptionService encryption, bool includePointerFiles)
    {
        var actual = new Dictionary<RelativePath, ContentHash>();

        foreach (var filePath in (rootPath / RelativePath.Root).EnumerateFiles(searchOption: SearchOption.AllDirectories))
        {
            var relativePath = filePath.RelativePath;

            if (!includePointerFiles && relativePath.IsPointerFilePath())
                continue;

            await using var stream = filePath.OpenRead();
            actual[relativePath] = await encryption.ComputeHashAsync(stream);
        }

        actual.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal).ToArray()
            .ShouldBe(expected.Files.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal).ToArray());
    }
}
