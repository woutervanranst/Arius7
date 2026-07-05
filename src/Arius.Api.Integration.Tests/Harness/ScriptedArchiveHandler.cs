using Arius.Core.Features.ArchiveCommand;
using Mediator;

namespace Arius.Api.Integration.Tests.Harness;

/// <summary>Fake archive handler: publishes the scenario's real Core events (so the Api's forwarders +
/// JobSink run exactly as in production), then returns the scenario's result. No storage is touched.</summary>
public sealed class ScriptedArchiveHandler(IPublisher publisher, ArchiveScenario scenario)
    : ICommandHandler<ArchiveCommand, ArchiveResult>
{
    public async ValueTask<ArchiveResult> Handle(ArchiveCommand command, CancellationToken cancellationToken)
    {
        foreach (var evt in scenario.Events)
            await publisher.Publish(evt, cancellationToken);
        return scenario.Result;
    }
}
