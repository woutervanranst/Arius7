using Arius.AzureBlob;
using Arius.Core;
using Arius.Core.Shared.Storage;
using Arius.Explorer.Settings;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arius.Explorer.Infrastructure;

public interface IRepositorySession : IDisposable
{
    IMediator? Mediator { get; }
    RepositoryOptions? Repository { get; }
    Task ConnectAsync(RepositoryOptions repository, CancellationToken cancellationToken = default);
}

public sealed class RepositorySession(IServiceProvider rootProvider) : IRepositorySession
{
    private readonly IBlobServiceFactory blobServiceFactory = rootProvider.GetRequiredService<IBlobServiceFactory>();
    private ServiceProvider? serviceProvider;

    public IMediator? Mediator { get; private set; }
    public RepositoryOptions? Repository { get; private set; }

    public async Task ConnectAsync(RepositoryOptions repository, CancellationToken cancellationToken = default)
    {
        DisposeCurrentProvider();

        var blobService = await blobServiceFactory.CreateAsync(repository.AccountName, repository.AccountKey, cancellationToken).ConfigureAwait(false);
        var blobContainer = await blobService.OpenContainerServiceAsync(repository.ContainerName, PreflightMode.ReadOnly, cancellationToken).ConfigureAwait(false);

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            var factory = rootProvider.GetRequiredService<ILoggerFactory>();
            builder.Services.AddSingleton(factory);
        });
        services.AddMediator();
        services.AddAzureBlobStorage();
        services.AddArius(blobContainer, repository.Passphrase, repository.AccountName, repository.ContainerName);

        serviceProvider = services.BuildServiceProvider();
        Mediator = serviceProvider.GetRequiredService<IMediator>();
        Repository = repository;
    }

    public void Dispose() => DisposeCurrentProvider();

    private void DisposeCurrentProvider()
    {
        Mediator = null;
        Repository = null;
        serviceProvider?.Dispose();
        serviceProvider = null;
    }
}
