using Arius.Api.Shared.AppData;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Shared.Composition;

/// <summary>
/// Registers the Arius.Core service graph for one repository into a new provider's service collection.
/// Production uses <see cref="AzureRepositoryCoreComposer"/>; tests substitute a scripted fake. The registry calls
/// <c>AddMediator()</c> itself, because the generated mediator and the Api's event forwarders must be composed in
/// the Api assembly.
/// </summary>
public interface IRepositoryCoreComposer
{
    Task ComposeAsync(IServiceCollection services, RepositoryConnection connection, PreflightMode mode, CancellationToken cancellationToken);

    /// <summary>Registers the Core handlers that need no repository (listing an account's containers).</summary>
    void ComposeWithoutRepository(IServiceCollection services);
}
