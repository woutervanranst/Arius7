using Arius.Api.Shared.Jobs;
using Arius.Core.Features.ArchiveCommand;
using Mediator;

namespace Arius.Api.FakeTestHost;

/// <summary>Fake archive handler: publishes the scenario's Core events, so the Api's forwarders and JobSink run as in
/// production, then returns the scenario's result. No storage is touched.</summary>
public sealed class ScriptedArchiveHandler(IPublisher publisher, ArchiveScenario scenario, ScenarioGate gate, ScenarioContext ctx)
    : ICommandHandler<ArchiveCommand, ArchiveResult>
{
    public async ValueTask<ArchiveResult> Handle(ArchiveCommand command, CancellationToken cancellationToken)
    {
        foreach (var evt in scenario.Events)
            await publisher.Publish(evt, cancellationToken);
        if (scenario.Gated)
            await gate.WaitForRelease(ctx.RepositoryId, cancellationToken);   // keeps the job running until a control endpoint releases it
        return scenario.Result;
    }
}
