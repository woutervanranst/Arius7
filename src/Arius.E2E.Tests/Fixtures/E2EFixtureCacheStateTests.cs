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
    public async Task CreateAsync_MaterializeSourceAsync_ReplacesLocalTreeWithRequestedVersion()
    {
        var definition = SyntheticRepositoryDefinitionFactory.Create(SyntheticRepositoryProfile.Small);
        var blobContainer = Substitute.For<IBlobContainerService>();
        var accountName = $"account-{Guid.NewGuid():N}";
        var containerName = $"container-{Guid.NewGuid():N}";

        await using var fixture = await E2EFixture.CreateAsync(
            blobContainer,
            accountName,
            containerName,
            BlobTier.Cool);

        fixture.WriteFile("stale.txt", [1, 2, 3]);

        var snapshot = await fixture.MaterializeSourceAsync(definition, SyntheticRepositoryVersion.V2, seed: 12345);

        File.Exists(Path.Combine(fixture.LocalRoot, "stale.txt")).ShouldBeFalse();
        snapshot.Files.Keys.ShouldContain("src/simple/c.bin");
        File.Exists(Path.Combine(fixture.LocalRoot, "src", "simple", "c.bin")).ShouldBeTrue();
    }

    [Test]
    public async Task DisposeAsync_DoubleDispose_DoesNotCorruptRepositoryCoordination()
    {
        var accountName = $"account-{Guid.NewGuid():N}";
        var containerName = $"container-{Guid.NewGuid():N}";
        var repositoryDirectory = Arius.Core.Shared.RepositoryPaths.GetRepositoryDirectory(accountName, containerName);
        var markerFile = Path.Combine(repositoryDirectory, "marker.txt");
        var blobContainer = Substitute.For<IBlobContainerService>();

        Directory.CreateDirectory(repositoryDirectory);
        await File.WriteAllTextAsync(markerFile, "preserve-me");

        try
        {
            var fixture = await E2EFixture.CreateAsync(
                blobContainer,
                accountName,
                containerName,
                BlobTier.Cool);
            await fixture.PreserveLocalCacheAsync();

            await fixture.DisposeAsync();
            await fixture.DisposeAsync();

            Directory.Exists(repositoryDirectory).ShouldBeTrue();
            File.Exists(markerFile).ShouldBeTrue();

            var secondFixture = await E2EFixture.CreateAsync(
                blobContainer,
                accountName,
                containerName,
                BlobTier.Cool);

            await secondFixture.DisposeAsync();

            Directory.Exists(repositoryDirectory).ShouldBeFalse();
        }
        finally
        {
            await E2EFixture.ResetLocalCacheAsync(accountName, containerName);
        }
    }

    [Test]
    public async Task DisposeAsync_WhileAnotherFixtureForSameRepositoryIsAlive_LeavesCacheUntilLastFixtureDisposes()
    {
        var accountName = $"account-{Guid.NewGuid():N}";
        var containerName = $"container-{Guid.NewGuid():N}";
        var repositoryDirectory = Arius.Core.Shared.RepositoryPaths.GetRepositoryDirectory(accountName, containerName);
        var blobContainer = Substitute.For<IBlobContainerService>();

        await E2EFixture.ResetLocalCacheAsync(accountName, containerName);
        Directory.CreateDirectory(repositoryDirectory);

        var firstFixture = await E2EFixture.CreateAsync(
            blobContainer,
            accountName,
            containerName,
            BlobTier.Cool);
        var secondFixture = await E2EFixture.CreateAsync(
            blobContainer,
            accountName,
            containerName,
            BlobTier.Cool);

        try
        {
            await firstFixture.DisposeAsync();

            Directory.Exists(repositoryDirectory).ShouldBeTrue();

            await secondFixture.DisposeAsync();

            Directory.Exists(repositoryDirectory).ShouldBeFalse();
        }
        finally
        {
            await firstFixture.DisposeAsync();
            await secondFixture.DisposeAsync();
        }
    }

    [Test]
    public async Task DisposeAsync_LastNonPreservingFixture_StillPreservesCacheWhenAnotherFixtureRequestedPreserve()
    {
        var accountName = $"account-{Guid.NewGuid():N}";
        var containerName = $"container-{Guid.NewGuid():N}";
        var repositoryDirectory = Arius.Core.Shared.RepositoryPaths.GetRepositoryDirectory(accountName, containerName);
        var markerFile = Path.Combine(repositoryDirectory, "marker.txt");
        var blobContainer = Substitute.For<IBlobContainerService>();

        await E2EFixture.ResetLocalCacheAsync(accountName, containerName);
        Directory.CreateDirectory(repositoryDirectory);
        await File.WriteAllTextAsync(markerFile, "preserve-me");

        var preservingFixture = await E2EFixture.CreateAsync(
            blobContainer,
            accountName,
            containerName,
            BlobTier.Cool);
        var nonPreservingFixture = await E2EFixture.CreateAsync(
            blobContainer,
            accountName,
            containerName,
            BlobTier.Cool);

        try
        {
            await preservingFixture.PreserveLocalCacheAsync();

            await preservingFixture.DisposeAsync();
            await nonPreservingFixture.DisposeAsync();

            Directory.Exists(repositoryDirectory).ShouldBeTrue();
            File.Exists(markerFile).ShouldBeTrue();
        }
        finally
        {
            await preservingFixture.DisposeAsync();
            await nonPreservingFixture.DisposeAsync();
            await E2EFixture.ResetLocalCacheAsync(accountName, containerName);
        }
    }
}
