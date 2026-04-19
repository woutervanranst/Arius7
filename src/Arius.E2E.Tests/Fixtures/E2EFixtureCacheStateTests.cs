using Arius.Core.Shared;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Shouldly;
using TUnit.Core;

namespace Arius.E2E.Tests.Fixtures;

public class E2EFixtureCacheStateTests
{
    [Test]
    public async Task ResetLocalCache_RemovesRepositoryCacheDirectory()
    {
        var repositoryDirectory = RepositoryPaths.GetRepositoryDirectory("account", "container");
        Directory.CreateDirectory(repositoryDirectory);

        await E2EFixture.ResetLocalCacheAsync("account", "container");

        Directory.Exists(repositoryDirectory).ShouldBeFalse();
    }

    [Test]
    public async Task MaterializeSourceAsync_ReplacesLocalTreeWithRequestedVersion()
    {
        var definition = SyntheticRepositoryDefinitionFactory.Create(SyntheticRepositoryProfile.Small);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"arius-e2e-fixture-tests-{Guid.NewGuid():N}");
        var localRoot = Path.Combine(tempRoot, "source");
        var restoreRoot = Path.Combine(tempRoot, "restore");

        Directory.CreateDirectory(localRoot);
        Directory.CreateDirectory(restoreRoot);

        await using var fixture = CreateFixtureForTests(tempRoot, localRoot, restoreRoot);

        fixture.WriteFile("stale.txt", [1, 2, 3]);

        var snapshot = await fixture.MaterializeSourceAsync(definition, SyntheticRepositoryVersion.V2, seed: 12345);

        File.Exists(Path.Combine(localRoot, "stale.txt")).ShouldBeFalse();
        snapshot.Files.Keys.ShouldContain("src/simple/c.bin");
        File.Exists(Path.Combine(localRoot, "src", "simple", "c.bin")).ShouldBeTrue();
    }

    static E2EFixture CreateFixtureForTests(string tempRoot, string localRoot, string restoreRoot)
    {
        return new E2EFixture(
            blobContainer: null!,
            encryption: new PlaintextPassthroughService(),
            index: null!,
            chunkStorage: null!,
            fileTreeService: null!,
            snapshot: null!,
            tempRoot,
            localRoot,
            restoreRoot,
            account: "account",
            containerName: "container",
            defaultTier: BlobTier.Cool);
    }
}
