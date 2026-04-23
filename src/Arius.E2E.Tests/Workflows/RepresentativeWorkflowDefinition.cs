using Arius.E2E.Tests.Datasets;
using Arius.E2E.Tests.Workflows.Steps;

namespace Arius.E2E.Tests.Workflows;

internal sealed record RepresentativeWorkflowDefinition(
    string Name,
    SyntheticRepositoryProfile Profile,
    int Seed,
    IReadOnlyList<IRepresentativeWorkflowStep> Steps);
