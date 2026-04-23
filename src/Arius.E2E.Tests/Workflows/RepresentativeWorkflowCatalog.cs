using Arius.E2E.Tests.Datasets;

namespace Arius.E2E.Tests.Workflows;

internal static class RepresentativeWorkflowCatalog
{
    internal static readonly RepresentativeWorkflowDefinition Canonical =
        new("canonical", SyntheticRepositoryProfile.Representative, 20260419, []);

    public static IReadOnlyList<RepresentativeWorkflowDefinition> All { get; } =
    [
        Canonical,
    ];
}
