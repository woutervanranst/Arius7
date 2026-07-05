using Arius.Api.AppData;
using Arius.Api.Composition;
using Arius.Core.Features.ArchiveCommand;
// using Arius.Core.Features.RestoreCommand; // Task 8 re-enables this once ScriptedRestoreHandler exists.
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Fakes;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests.Harness;

/// <summary>Test composer: registers a scripted Core (scenario-driven command handlers + a deterministic
/// cost estimator) into the per-repo provider. Never opens a real container.</summary>
public sealed class ScriptedRepositoryCoreComposer(ScenarioRegistry scenarios) : IRepositoryCoreComposer
{
    public Task ComposeAsync(IServiceCollection services, RepositoryConnection connection, PreflightMode mode, CancellationToken cancellationToken)
    {
        services.AddSingleton<IStorageCostEstimator>(new FakeStorageCostEstimator());

        var archive = scenarios.TakeArchive(connection.RepositoryId);
        if (archive is not null)
        {
            services.AddSingleton(archive);
            services.AddSingleton<ICommandHandler<ArchiveCommand, ArchiveResult>, ScriptedArchiveHandler>();
        }

        // Task 8 re-enables this once ScriptedRestoreHandler exists.
        //var restore = scenarios.TakeRestore(connection.RepositoryId);
        //if (restore is not null)
        //{
        //    services.AddSingleton(restore);
        //    services.AddSingleton<ICommandHandler<RestoreCommand, RestoreResult>, ScriptedRestoreHandler>();
        //}

        return Task.CompletedTask;
    }
}
