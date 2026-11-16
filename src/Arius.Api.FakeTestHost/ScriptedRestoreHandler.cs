using Arius.Core.Features.RestoreCommand;
using Mediator;

namespace Arius.Api.FakeTestHost;

/// <summary>Fake restore handler: publishes scripted restore events and drives the run's ConfirmRehydration
/// cost handshake exactly as the real handler would, so JobRunner's awaiting-cost path runs unchanged.</summary>
public sealed class ScriptedRestoreHandler(IPublisher publisher, RestoreScenario scenario, ScenarioGate gate, ScenarioContext ctx)
    : ICommandHandler<RestoreCommand, RestoreResult>
{
    public async ValueTask<RestoreResult> Handle(RestoreCommand command, CancellationToken cancellationToken)
    {
        scenario.Received.Enqueue(command);

        foreach (var evt in scenario.PreCostEvents)
            await PublishAsync(evt, cancellationToken);

        if (scenario.CostPrompt is not null && command.Options.ConfirmRehydration is not null)
        {
            var priority = await command.Options.ConfirmRehydration(scenario.CostPrompt, cancellationToken);
            if (priority is null)
                return scenario.Result;   // declined / timed-out — run stops (mirrors Core exiting with pending)
        }

        foreach (var evt in scenario.PostApproveEvents)
            await PublishAsync(evt, cancellationToken);
        if (scenario.Gated)
            await gate.WaitForRelease(ctx.RepositoryId, cancellationToken);

        return scenario.Result;

        async ValueTask PublishAsync(INotification evt, CancellationToken ct)
        {
            if (scenario.WritesFiles && evt is FileRestoredEvent restored)
            {
                var path = Path.Combine(command.Options.RootDirectory, restored.RelativePath.ToString());
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, $"scripted:{restored.RelativePath}", ct);
            }

            await publisher.Publish(evt, ct);
        }
    }
}
