using Arius.AzureBlob;
using Arius.Core;
using Arius.Core.Storage;
using Arius.Explorer.Settings;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
        var blobContainer = await blobService.GetContainerServiceAsync(repository.ContainerName, PreflightMode.ReadOnly, cancellationToken).ConfigureAwait(false);

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            var factory = rootProvider.GetRequiredService<ILoggerFactory>();
            builder.Services.AddSingleton(factory);
        });
        services.AddMediator();
        services.AddArius(blobContainer, repository.Passphrase, repository.AccountName, repository.ContainerName);

        serviceProvider = services.BuildServiceProvider();
        Mediator = serviceProvider.GetRequiredService<IMediator>();
        Repository = repository;
    }

    public static void AddRootCorePlaceholders(IServiceCollection services)
    {
        services.AddSingleton<IBlobContainerService, NullBlobContainerService>();
        services.AddArius(new NullBlobContainerService(), passphrase: null, accountName: "root", containerName: "root");
    }

    public void Dispose() => DisposeCurrentProvider();

    private void DisposeCurrentProvider()
    {
        Mediator = null;
        Repository = null;
        serviceProvider?.Dispose();
        serviceProvider = null;
    }

    private sealed class NullBlobContainerService : IBlobContainerService
    {
        public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default) => Task.FromResult(new BlobMetadata { Exists = false });
        public async IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken cancellationToken = default) { yield break; }
        public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
