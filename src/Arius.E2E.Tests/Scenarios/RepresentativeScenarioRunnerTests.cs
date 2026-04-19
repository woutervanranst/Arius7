using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Fixtures;

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
    public async Task ScenarioRunner_RunsArchiveScenario_OnAzuriteBackend()
    {
        var scenario = RepresentativeScenarioCatalog.All.Single(x => x.Name == "initial-archive-v1");
        await using var backend = new AzuriteE2EBackendFixture();
        await backend.InitializeAsync();

        var result = await RepresentativeScenarioRunner.RunAsync(
            backend,
            scenario,
            SyntheticRepositoryProfile.Small,
            seed: 12345);

        result.WasSkipped.ShouldBeFalse();
        result.SkipReason.ShouldBeNull();
    }

    [Test]
    public async Task ScenarioRunner_RunsRestoreScenario_OnAzuriteBackend()
    {
        var scenario = RepresentativeScenarioCatalog.All.Single(x => x.Name == "restore-latest-cold-cache");
        await using var backend = new AzuriteE2EBackendFixture();
        await backend.InitializeAsync();

        var result = await RepresentativeScenarioRunner.RunAsync(
            backend,
            scenario,
            SyntheticRepositoryProfile.Small,
            seed: 12345);

        result.WasSkipped.ShouldBeFalse();
        result.SkipReason.ShouldBeNull();
    }

    [Test]
    public async Task ScenarioRunner_RunsArchiveThenRestoreScenario_OnAzuriteBackend()
    {
        var scenario = RepresentativeScenarioCatalog.All.Single(x => x.Name == "archive-remove-local-then-thin-followup");
        await using var backend = new AzuriteE2EBackendFixture();
        await backend.InitializeAsync();

        var result = await RepresentativeScenarioRunner.RunAsync(
            backend,
            scenario,
            SyntheticRepositoryProfile.Small,
            seed: 12345);

        result.WasSkipped.ShouldBeFalse();
        result.SkipReason.ShouldBeNull();
    }

    private sealed class FakeBackend(bool supportsArchiveTier) : IE2EStorageBackend
    {
        public string Name => "Fake";

        public E2EBackendCapabilities Capabilities { get; } = new(
            SupportsArchiveTier: supportsArchiveTier,
            SupportsRehydrationPlanning: supportsArchiveTier);

        public int CreateContextCallCount { get; private set; }

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<E2EStorageBackendContext> CreateContextAsync(CancellationToken cancellationToken = default)
        {
            CreateContextCallCount++;
            throw new InvalidOperationException("CreateContextAsync should not be called for skipped scenarios.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
