using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace Arius.E2E.Tests.Fixtures;

public class E2EFixtureCacheStateTests
{
    [Test]
    public async Task ResetLocalCache_RemovesRepositoryCacheDirectory_InsideExplicitRoot()
    {
        var cacheRoot = Path.Combine(Path.GetTempPath(), $"arius-cache-tests-{Guid.NewGuid():N}");
        var repositoryDirectory = Path.Combine(cacheRoot, ".arius", "account-container");
        Directory.CreateDirectory(repositoryDirectory);

        try
        {
            await E2EFixture.ResetLocalCacheAsync("account", "container", cacheRoot);

            Directory.Exists(repositoryDirectory).ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Test]
    public async Task PreserveLocalCache_LeavesRepositoryCacheDirectory_InsideExplicitRoot()
    {
        var cacheRoot = Path.Combine(Path.GetTempPath(), $"arius-cache-tests-{Guid.NewGuid():N}");
        var repositoryDirectory = Path.Combine(cacheRoot, ".arius", "account-container");
        Directory.CreateDirectory(repositoryDirectory);

        try
        {
            await E2EFixture.PreserveLocalCacheAsync("account", "container", cacheRoot);

            Directory.Exists(repositoryDirectory).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Test]
    public async Task CreateAsync_MaterializeSourceAsync_ReplacesLocalTreeWithRequestedVersion()
    {
        var definition = SyntheticRepositoryDefinitionFactory.Create(SyntheticRepositoryProfile.Small);
        var blobContainer = Substitute.For<IBlobContainerService>();

        await using var fixture = await E2EFixture.CreateAsync(
            blobContainer,
            "account",
            "container",
            BlobTier.Cool);

        fixture.WriteFile("stale.txt", [1, 2, 3]);

        var snapshot = await fixture.MaterializeSourceAsync(definition, SyntheticRepositoryVersion.V2, seed: 12345);

        File.Exists(Path.Combine(fixture.LocalRoot, "stale.txt")).ShouldBeFalse();
        snapshot.Files.Keys.ShouldContain("src/simple/c.bin");
        File.Exists(Path.Combine(fixture.LocalRoot, "src", "simple", "c.bin")).ShouldBeTrue();
    }
}
