using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;

namespace Arius.E2E.Tests.Datasets;

internal static class SyntheticRepositoryStateAssertions
{
    public static async Task AssertMatchesDiskTreeAsync(SyntheticRepositoryState expected, LocalDirectory rootDirectory, IEncryptionService encryption, bool includePointerFiles)
    {
        var fileSystem = new RelativeFileSystem(rootDirectory);
        var actual = new Dictionary<RelativePath, ContentHash>();

        foreach (var file in fileSystem.EnumerateFiles())
        {
            var relativePath = file.Path;

            if (!includePointerFiles && relativePath.ToString().EndsWith(".pointer.arius", StringComparison.Ordinal))
                continue;

            await using var stream = fileSystem.OpenRead(relativePath);
            actual[relativePath] = await encryption.ComputeHashAsync(stream);
        }

        actual.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal).ToArray()
            .ShouldBe(expected.Files.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal).ToArray());
    }
}
