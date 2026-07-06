using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Testing;
using Arius.Core.Features.ArchiveCommand;

namespace Arius.Api.Integration.Tests;

public class ScenarioRegistryTests
{
    [Test]
    public async Task Set_then_take_returns_the_scenario_once_per_repo()
    {
        var registry = new ScenarioRegistry();
        var scenario = new ArchiveScenario(
            Events: [new ScanCompleteEvent(1, 100)],
            Result: NewArchiveResult());

        registry.SetArchive(7, scenario);

        await Assert.That(registry.TakeArchive(7)).IsSameReferenceAs(scenario);
        await Assert.That(registry.TakeArchive(99)).IsNull();   // other repo unaffected
    }

    private static ArchiveResult NewArchiveResult() => new()
    {
        Success = true, FilesScanned = 1, EntriesExcluded = 0, FilesUploaded = 1, FilesDeduped = 0,
        OriginalSize = 100, IncrementalSize = 100, IncrementalStoredSize = 40, FastHashReused = 0,
        FastHashRehashed = 1, RootHash = null, SnapshotTime = DateTimeOffset.UnixEpoch,
    };
}
