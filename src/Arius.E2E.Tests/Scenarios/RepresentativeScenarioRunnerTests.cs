using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;
using NSubstitute;

namespace Arius.E2E.Tests.Scenarios;

public class RepresentativeScenarioRunnerTests
{
    [Test]
    public async Task ScenarioRunner_SkipsArchiveTierScenario_WhenBackendLacksCapability()
    {
        var scenario = RepresentativeScenarioCatalog.All.Single(x => x.Name == "archive-tier-planning");
        await using var backend = new FakeBackend(supportsArchiveTier: false);

        var result = await RepresentativeScenarioRunner.RunAsync(
            backend,
            scenario,
            SyntheticRepositoryProfile.Small,
            seed: 12345);

        result.WasSkipped.ShouldBeTrue();
        result.SkipReason.ShouldBe("Backend lacks archive-tier capability.");
        backend.CreateContextCallCount.ShouldBe(0);
    }

    [Test]
    public async Task ScenarioRunner_ArchiveScenario_UsesPreparedSourceTree_AndPassesArchiveOptions()
    {
        var scenario = RepresentativeScenarioCatalog.All.Single(x => x.Name == "incremental-archive-v2");
        await using var backend = new FakeBackend(supportsArchiveTier: true);
        var setupFixture = new FakeScenarioFixture();
        var operationFixture = new FakeScenarioFixture();
        var createdFixtures = new Queue<IRepresentativeScenarioFixture>([setupFixture, operationFixture]);

        var result = await RepresentativeScenarioRunner.RunAsync(
            backend,
            scenario,
            SyntheticRepositoryProfile.Small,
            seed: 12345,
            new RepresentativeScenarioRunnerDependencies
            {
                CreateFixtureAsync = (_, _) => Task.FromResult(createdFixtures.Dequeue()),
            });

        result.WasSkipped.ShouldBeFalse();
        result.SkipReason.ShouldBeNull();
        setupFixture.MaterializedVersions.ShouldBe([
            SyntheticRepositoryVersion.V1,
            SyntheticRepositoryVersion.V2,
        ]);
        operationFixture.MaterializedVersions.ShouldBe([
            SyntheticRepositoryVersion.V2,
        ]);
        operationFixture.ArchiveOptions.ShouldHaveSingleItem().RootDirectory.ShouldBe(operationFixture.LocalRoot);
        operationFixture.ArchiveOptions.Single().NoPointers.ShouldBeFalse();
        operationFixture.ArchiveOptions.Single().RemoveLocal.ShouldBeFalse();
    }

    [Test]
    public async Task ScenarioRunner_ArchiveThenRestoreScenario_PassesRemoveLocal_ToArchiveOperation()
    {
        var scenario = RepresentativeScenarioCatalog.All.Single(x => x.Name == "archive-remove-local-then-thin-followup");
        await using var backend = new FakeBackend(supportsArchiveTier: true);
        var setupFixture = new FakeScenarioFixture();
        var operationFixture = new FakeScenarioFixture();
        var createdFixtures = new Queue<IRepresentativeScenarioFixture>([setupFixture, operationFixture]);

        var result = await RepresentativeScenarioRunner.RunAsync(
            backend,
            scenario,
            SyntheticRepositoryProfile.Small,
            seed: 12345,
            new RepresentativeScenarioRunnerDependencies
            {
                CreateFixtureAsync = (_, _) => Task.FromResult(createdFixtures.Dequeue()),
            });

        result.WasSkipped.ShouldBeFalse();
        operationFixture.MaterializedVersions.ShouldBe([
            SyntheticRepositoryVersion.V1,
        ]);
        operationFixture.ArchiveOptions.ShouldHaveSingleItem().RemoveLocal.ShouldBeTrue();
        operationFixture.RestoreOptions.ShouldHaveSingleItem().Version.ShouldBeNull();
    }

    [Test]
    public async Task ScenarioRunner_RestoreLatestScenario_WithV2Source_ArchivesV2DuringSetup_AndUsesFreshRestoreFixture()
    {
        var scenario = RepresentativeScenarioCatalog.All.Single(x => x.Name == "restore-latest-cold-cache");
        await using var backend = new FakeBackend(supportsArchiveTier: true);
        var setupFixture = new FakeScenarioFixture();
        var operationFixture = new FakeScenarioFixture();
        var createdFixtures = new Queue<IRepresentativeScenarioFixture>([setupFixture, operationFixture]);

        var result = await RepresentativeScenarioRunner.RunAsync(
            backend,
            scenario,
            SyntheticRepositoryProfile.Small,
            seed: 12345,
            new RepresentativeScenarioRunnerDependencies
            {
                CreateFixtureAsync = (_, _) => Task.FromResult(createdFixtures.Dequeue()),
            });

        result.WasSkipped.ShouldBeFalse();
        result.SkipReason.ShouldBeNull();
        setupFixture.MaterializedVersions.ShouldBe([
            SyntheticRepositoryVersion.V1,
            SyntheticRepositoryVersion.V2,
        ]);
        setupFixture.ArchiveCallCount.ShouldBe(2);
        operationFixture.MaterializedVersions.Count.ShouldBe(0);
        operationFixture.RestoreOptions.ShouldHaveSingleItem().Version.ShouldBeNull();
    }

    [Test]
    public async Task ScenarioRunner_ColdPreviousRestore_UsesFreshFixture_AndPassesRestoreOptions()
    {
        var scenario = RepresentativeScenarioCatalog.All.Single(x => x.Name == "restore-previous-cold-cache");
        await using var backend = new FakeBackend(supportsArchiveTier: true);
        var setupFixture = new FakeScenarioFixture();
        var operationFixture = new FakeScenarioFixture();
        var createdFixtures = new Queue<IRepresentativeScenarioFixture>([setupFixture, operationFixture]);
        var cacheResets = new List<string>();

        var result = await RepresentativeScenarioRunner.RunAsync(
            backend,
            scenario,
            SyntheticRepositoryProfile.Small,
            seed: 12345,
            new RepresentativeScenarioRunnerDependencies
            {
                CreateFixtureAsync = (_, _) => Task.FromResult(createdFixtures.Dequeue()),
                ResetLocalCacheAsync = (accountName, containerName) =>
                {
                    cacheResets.Add($"{accountName}/{containerName}");
                    return Task.CompletedTask;
                },
            });

        result.WasSkipped.ShouldBeFalse();
        result.SkipReason.ShouldBeNull();
        setupFixture.MaterializedVersions.ShouldBe([
            SyntheticRepositoryVersion.V1,
            SyntheticRepositoryVersion.V2,
        ]);
        setupFixture.ArchiveCallCount.ShouldBe(2);
        setupFixture.RestoreCallCount.ShouldBe(0);
        setupFixture.DisposeCallCount.ShouldBe(1);
        operationFixture.RestoreOptions.ShouldHaveSingleItem().RootDirectory.ShouldBe(operationFixture.RestoreRoot);
        operationFixture.RestoreOptions.Single().Version.ShouldBe("previous");
        operationFixture.RestoreOptions.Single().Overwrite.ShouldBeTrue();
        cacheResets.Count.ShouldBe(2);
    }

    [Test]
    public async Task ScenarioRunner_MultipleVersionsRestore_PerformsPreviousAndLatestRestores()
    {
        var scenario = RepresentativeScenarioCatalog.All.Single(x => x.Name == "restore-multiple-versions");
        await using var backend = new FakeBackend(supportsArchiveTier: true);
        var setupFixture = new FakeScenarioFixture();
        var operationFixture = new FakeScenarioFixture();
        var createdFixtures = new Queue<IRepresentativeScenarioFixture>([setupFixture, operationFixture]);

        var result = await RepresentativeScenarioRunner.RunAsync(
            backend,
            scenario,
            SyntheticRepositoryProfile.Small,
            seed: 12345,
            new RepresentativeScenarioRunnerDependencies
            {
                CreateFixtureAsync = (_, _) => Task.FromResult(createdFixtures.Dequeue()),
            });

        result.WasSkipped.ShouldBeFalse();
        setupFixture.MaterializedVersions.ShouldBe([
            SyntheticRepositoryVersion.V1,
            SyntheticRepositoryVersion.V2,
        ]);
        operationFixture.RestoreOptions.Count.ShouldBe(2);
        operationFixture.RestoreOptions[0].Version.ShouldBe("previous");
        operationFixture.RestoreOptions[1].Version.ShouldBeNull();
    }

    private sealed class FakeBackend(bool supportsArchiveTier) : IE2EStorageBackend
    {
        private readonly IBlobContainerService _blobContainer = Substitute.For<IBlobContainerService>();

        public string Name => "Fake";

        public E2EBackendCapabilities Capabilities { get; } = new(
            SupportsArchiveTier: supportsArchiveTier,
            SupportsRehydrationPlanning: supportsArchiveTier);

        public int CreateContextCallCount { get; private set; }

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<E2EStorageBackendContext> CreateContextAsync(CancellationToken cancellationToken = default)
        {
            CreateContextCallCount++;

            return Task.FromResult(new E2EStorageBackendContext
            {
                BlobContainer = _blobContainer,
                AccountName = "account",
                ContainerName = "container",
                Capabilities = Capabilities,
                CleanupAsync = () => ValueTask.CompletedTask,
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeScenarioFixture : IRepresentativeScenarioFixture
    {
        public string LocalRoot { get; } = "/fake/source";

        public string RestoreRoot { get; } = "/fake/restore";

        public List<SyntheticRepositoryVersion> MaterializedVersions { get; } = [];

        public List<ArchiveCommandOptions> ArchiveOptions { get; } = [];

        public List<RestoreOptions> RestoreOptions { get; } = [];

        public int ArchiveCallCount { get; private set; }

        public int RestoreCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public Task PreserveLocalCacheAsync() => Task.CompletedTask;

        public Task<RepositoryTreeSnapshot> MaterializeSourceAsync(
            SyntheticRepositoryDefinition definition,
            SyntheticRepositoryVersion version,
            int seed)
        {
            MaterializedVersions.Add(version);
            return Task.FromResult(new RepositoryTreeSnapshot(new Dictionary<string, string>(StringComparer.Ordinal)));
        }

        public Task<ArchiveResult> ArchiveAsync(ArchiveCommandOptions options, CancellationToken ct = default)
        {
            ArchiveCallCount++;
            ArchiveOptions.Add(options);

            return Task.FromResult(new ArchiveResult
            {
                Success = true,
                FilesScanned = 0,
                FilesUploaded = 0,
                FilesDeduped = 0,
                TotalSize = 0,
                RootHash = "root",
                SnapshotTime = DateTimeOffset.UtcNow,
            });
        }

        public Task<RestoreResult> RestoreAsync(RestoreOptions options, CancellationToken ct = default)
        {
            RestoreCallCount++;
            RestoreOptions.Add(options);

            return Task.FromResult(new RestoreResult
            {
                Success = true,
                FilesRestored = 0,
                FilesSkipped = 0,
                ChunksPendingRehydration = 0,
            });
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }
    }
}
