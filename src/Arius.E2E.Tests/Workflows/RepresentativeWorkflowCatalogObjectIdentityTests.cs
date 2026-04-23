namespace Arius.E2E.Tests.Workflows;

public class RepresentativeWorkflowCatalogObjectIdentityTests
{
    [Test]
    public async Task Catalog_ExposesNamedWorkflowInstances_InAllCollection()
    {
        await Task.CompletedTask;

        RepresentativeWorkflowCatalog.All.ShouldContain(RepresentativeWorkflowCatalog.ArchiveTierPlanning);
        RepresentativeWorkflowCatalog.All.ShouldContain(RepresentativeWorkflowCatalog.RestoreLatestColdCache);
        RepresentativeWorkflowCatalog.All.ShouldContain(RepresentativeWorkflowCatalog.RestoreLocalConflictNoOverwrite);
        RepresentativeWorkflowCatalog.All.ShouldContain(RepresentativeWorkflowCatalog.RestoreLocalConflictOverwrite);
    }
}
