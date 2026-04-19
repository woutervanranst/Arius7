using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace Arius.E2E.Tests.Fixtures;

public class E2EFixtureCacheStateTests
{
    [Test]
    public async Task ResetLocalCache_RemovesRepositoryCacheDirectory_OnRealRepositoryPath()
    {
        var accountName = $"account-{Guid.NewGuid():N}";
        var containerName = $"container-{Guid.NewGuid():N}";
        var repositoryDirectory = Arius.Core.Shared.RepositoryPaths.GetRepositoryDirectory(accountName, containerName);
        Directory.CreateDirectory(repositoryDirectory);

        try
        {
            await E2EFixture.ResetLocalCacheAsync(accountName, containerName);

            Directory.Exists(repositoryDirectory).ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(repositoryDirectory))
                Directory.Delete(repositoryDirectory, recursive: true);
        }
    }

    [Test]
    public async Task PreserveLocalCache_LeavesRepositoryCacheDirectoryAndContents_OnRealRepositoryPath()
    {
        var accountName = $"account-{Guid.NewGuid():N}";
        var containerName = $"container-{Guid.NewGuid():N}";
        var repositoryDirectory = Arius.Core.Shared.RepositoryPaths.GetRepositoryDirectory(accountName, containerName);
        var markerFile = Path.Combine(repositoryDirectory, "marker.txt");
        Directory.CreateDirectory(repositoryDirectory);
        await File.WriteAllTextAsync(markerFile, "preserve-me");

        try
        {
            await E2EFixture.PreserveLocalCacheAsync(accountName, containerName);

            Directory.Exists(repositoryDirectory).ShouldBeTrue();
            File.Exists(markerFile).ShouldBeTrue();
            (await File.ReadAllTextAsync(markerFile)).ShouldBe("preserve-me");
        }
        finally
        {
            if (Directory.Exists(repositoryDirectory))
                Directory.Delete(repositoryDirectory, recursive: true);
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
