using Arius.Api.AppData;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Composition;

/// <summary>
/// Registers the per-repository Arius.Core service graph (command/query handlers + the storage
/// services they depend on) into a freshly-built per-job/read <see cref="IServiceCollection"/>.
/// Production wires the real Azure-backed Core (<see cref="AzureRepositoryCoreComposer"/>); tests can
/// swap in a scripted fake without touching Arius.Core.
///
/// The registry always runs <c>AddMediator()</c> itself (the generated mediator + the Api's event
/// forwarders must be composed in the Api assembly) and then calls this to add the handlers + storage.
/// </summary>
public interface IRepositoryCoreComposer
{
    Task ComposeAsync(IServiceCollection services, RepositoryConnection connection, PreflightMode mode, CancellationToken cancellationToken);
}
