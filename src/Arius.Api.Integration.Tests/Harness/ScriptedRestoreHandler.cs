using Arius.Core.Features.RestoreCommand;
using Mediator;

namespace Arius.Api.Integration.Tests.Harness;

/// <summary>Fake restore handler: publishes scripted restore events and drives the run's ConfirmRehydration
/// cost handshake exactly as the real handler would, so JobRunner's awaiting-cost path runs unchanged.</summary>
public sealed class ScriptedRestoreHandler(IPublisher publisher, RestoreScenario scenario)
    : ICommandHandler<RestoreCommand, RestoreResult>
{
    public async ValueTask<RestoreResult> Handle(RestoreCommand command, CancellationToken cancellationToken)
    {
        foreach (var evt in scenario.PreCostEvents)
            await publisher.Publish(evt, cancellationToken);

        if (scenario.CostPrompt is not null && command.Options.ConfirmRehydration is not null)
        {
            var priority = await command.Options.ConfirmRehydration(scenario.CostPrompt, cancellationToken);
            if (priority is null)
                return scenario.Result;   // declined / timed-out — run stops (mirrors Core exiting with pending)
        }

        foreach (var evt in scenario.PostApproveEvents)
            await publisher.Publish(evt, cancellationToken);

        return scenario.Result;
    }
}
