using Arius.Api.Shared.AppData;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Shared.Composition;

/// <summary>
/// Registers the Arius.Core service graph for one repository into a new provider's service collection.
/// Production uses <see cref="AzureRepositoryCoreComposer"/>; tests substitute a scripted fake. The registry calls
/// <c>AddMediator()</c> itself, because the generated mediator and the Api's event forwarders must be composed in
/// the Api assembly. Both methods run after <c>AddMediator()</c>, so handlers they register override its
/// auto-registered ones.
/// </summary>
public interface IRepositoryCoreComposer
{
    Task ComposeAsync(IServiceCollection services, RepositoryConnection connection, PreflightMode mode, CancellationToken cancellationToken);

    /// <summary>Registers the services for requests that need no repository, such as listing an account's
    /// containers.</summary>
    void ComposeWithoutRepository(IServiceCollection services);
}
