using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Workflows;

internal static class RepresentativeWorkflowCatalog
{
    internal static readonly RepresentativeWorkflowDefinition Canonical =
        new("canonical-representative-workflow", SyntheticRepositoryProfile.Representative, 20260419, []);
}
