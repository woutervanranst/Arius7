namespace Arius.E2E.Tests.Scenarios;

public class RepresentativeScenarioCatalogTests
{
    [Test]
    public async Task Catalog_ContainsApprovedCoreScenarios()
    {
        await Task.CompletedTask;

        var scenarios = RepresentativeScenarioCatalog.All;

        scenarios.Select(x => x.Name).ShouldContain("initial-archive-v1");
        scenarios.Select(x => x.Name).ShouldContain("incremental-archive-v2");
        scenarios.Select(x => x.Name).ShouldContain("second-archive-no-changes");
        scenarios.Select(x => x.Name).ShouldContain("restore-latest-cold-cache");
        scenarios.Select(x => x.Name).ShouldContain("restore-latest-warm-cache");
        scenarios.Select(x => x.Name).ShouldContain("restore-previous-cold-cache");
        scenarios.Select(x => x.Name).ShouldContain("restore-previous-warm-cache");
        scenarios.Select(x => x.Name).ShouldContain("restore-multiple-versions");
        scenarios.Select(x => x.Name).ShouldContain("restore-local-conflict-no-overwrite");
        scenarios.Select(x => x.Name).ShouldContain("restore-local-conflict-overwrite");
        scenarios.Select(x => x.Name).ShouldContain("archive-no-pointers");
        scenarios.Select(x => x.Name).ShouldContain("archive-remove-local-then-thin-followup");
    }
}
