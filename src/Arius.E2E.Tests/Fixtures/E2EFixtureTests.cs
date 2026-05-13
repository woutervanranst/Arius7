using Arius.Core.Shared;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Fixtures;
using Arius.Tests.Shared.Storage;
using Shouldly;

namespace Arius.E2E.Tests.Fixtures;

public class E2EFixtureTests
{
    [Test]
    public async Task DisposeAsync_PreservesLocalCache_WhenRequested()
    {
        var accountName = $"acct-e2e-preserve-{Guid.NewGuid():N}";
        var containerName = $"ctr-e2e-preserve-{Guid.NewGuid():N}";
        var cacheRoot = RepositoryPaths.GetRepositoryRoot(accountName, containerName).ToString();

        await E2EFixture.ResetLocalCacheAsync(accountName, containerName);

        try
        {
            var fixture = await E2EFixture.CreateAsync(
                new FakeInMemoryBlobContainerService(),
                accountName,
                containerName,
                BlobTier.Hot);

            Directory.CreateDirectory(cacheRoot);
            await File.WriteAllTextAsync(Path.Combine(cacheRoot, "lease-marker.txt"), "preserve");

            await fixture.PreserveLocalCacheAsync();
            await fixture.DisposeAsync();

            Directory.Exists(cacheRoot).ShouldBeTrue();
            File.Exists(Path.Combine(cacheRoot, "lease-marker.txt")).ShouldBeTrue();
        }
        finally
        {
            await E2EFixture.ResetLocalCacheAsync(accountName, containerName);
        }
    }
}
