namespace Arius.E2E.Tests.Scenarios;

public class RepresentativeScenarioCatalogObjectIdentityTests
{
    [Test]
    public async Task Catalog_ExposesNamedScenarioInstances_InAllCollection()
    {
        await Task.CompletedTask;

        RepresentativeScenarioCatalog.All.ShouldContain(RepresentativeScenarioCatalog.ArchiveTierPlanning);
        RepresentativeScenarioCatalog.All.ShouldContain(RepresentativeScenarioCatalog.RestoreLatestColdCache);
        RepresentativeScenarioCatalog.All.ShouldContain(RepresentativeScenarioCatalog.RestoreLocalConflictNoOverwrite);
        RepresentativeScenarioCatalog.All.ShouldContain(RepresentativeScenarioCatalog.RestoreLocalConflictOverwrite);
    }
}
