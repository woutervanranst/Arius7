namespace Arius.E2E.Tests.Workflows.Steps;

internal interface IRepresentativeWorkflowStep
{
    string Name { get; }

    Task ExecuteAsync(RepresentativeWorkflowState state, CancellationToken cancellationToken);
}
