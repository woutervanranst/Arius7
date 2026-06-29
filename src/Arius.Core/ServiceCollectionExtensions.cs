using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.ChunkHydrationStatusQuery;
using Arius.Core.Features.ContainerNamesQuery;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.RepairChunkIndexCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Features.SnapshotDiffQuery;
using Arius.Core.Features.SnapshotsListQuery;
using Arius.Core.Features.StatisticsQuery;
using Arius.Core.Features.StorageAccountInfoQuery;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arius.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Arius core services and mediator handler interfaces into the provided <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="blobContainer">The blob storage implementation to use for persisted data.</param>
    /// <param name="passphrase">If non-null, enables passphrase-based encryption; if null, a plaintext passthrough is used.</param>
    /// <param name="accountName">The account name used to scope chunk indexing and handler operations.</param>
    /// <param name="containerName">The container name used to scope chunk indexing and handler operations.</param>
    /// <param name="configuration">
    /// Optional host configuration layered on top of Arius.Core's embedded defaults. When it contains an
    /// <c>Arius:Exclusions</c> section, those values override the central defaults for file/folder exclusions;
    /// otherwise the embedded defaults apply. Pass <c>null</c> (the default) to use the central defaults only.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> instance for chaining.</returns>
    /// <remarks>
    /// The statistics and restore handlers resolve <see cref="IStorageCostEstimator"/> from the provider, so a
    /// provider's cost estimator must be registered separately (e.g. via <c>AddAzureBlobStorage()</c>) before use.
    /// </remarks>
    public static IServiceCollection AddArius(
        this IServiceCollection services,
        IBlobContainerService     blobContainer,
        string?                 passphrase,
        string                  accountName,
        string                  containerName,
        IConfiguration?         configuration = null)
    {
        // Storage
        services.AddSingleton(blobContainer);
        if (services.All(service => service.ServiceType != typeof(IBlobServiceFactory)))
        {
            services.AddSingleton<IBlobServiceFactory, NullBlobServiceFactory>();
        }

        // File/folder exclusions (options pattern): Arius.Core's embedded appsettings.json is the central
        // base layer so every host inherits the same defaults; an optional host configuration overrides them.
        var exclusionConfig = new ConfigurationBuilder()
            .AddConfiguration(FileExclusionOptions.EmbeddedDefaultConfiguration())
            .AddConfiguration(configuration ?? new ConfigurationBuilder().Build())
            .Build();
        services.AddOptions<FileExclusionOptions>()
            .Bind(exclusionConfig.GetSection(FileExclusionOptions.SectionName));
        services.AddSingleton<FileExclusionFilter>(sp =>
            new FileExclusionFilter(sp.GetRequiredService<IOptions<FileExclusionOptions>>().Value));

        // Encryption
        IEncryptionService encryption = passphrase is not null
            ? new PassphraseEncryptionService(passphrase)
            : new PlaintextPassthroughService();
        services.AddSingleton(encryption);

        services.AddSingleton<ICompressionService>(new ZstdCompressionService());

        // Snapshot service
        services.AddSingleton<ISnapshotService>(sp =>
            new SnapshotService(
                sp.GetRequiredService<IBlobContainerService>(),
                sp.GetRequiredService<IEncryptionService>(),
                sp.GetRequiredService<ICompressionService>(),
                accountName,
                containerName));

        // Chunk index
        services.AddSingleton<IChunkIndexService>(sp =>
            new ChunkIndexService(
                sp.GetRequiredService<IBlobContainerService>(),
                sp.GetRequiredService<IEncryptionService>(),
                sp.GetRequiredService<ICompressionService>(),
                sp.GetRequiredService<ISnapshotService>(),
                accountName,
                containerName,
                sp.GetRequiredService<ILoggerFactory>()));

        services.AddSingleton<IChunkStorageService>(sp =>
            new ChunkStorageService(
                sp.GetRequiredService<IBlobContainerService>(),
                sp.GetRequiredService<IEncryptionService>(),
                sp.GetRequiredService<ICompressionService>()));

        // File tree service
        services.AddSingleton<IFileTreeService>(sp =>
            new FileTreeService(
                sp.GetRequiredService<IBlobContainerService>(),
                sp.GetRequiredService<IEncryptionService>(),
                sp.GetRequiredService<ICompressionService>(),
                accountName,
                containerName,
                sp.GetRequiredService<ILogger<FileTreeService>>()));

        // NOTE: AddMediator() is intentionally NOT called here.
        // The source generator must run in the outermost assembly (Arius.Cli or test project)
        // so it can discover INotificationHandler<T> implementations in both Core and CLI.
        // The caller (CLI startup or test harness) is responsible for calling AddMediator().

        // Re-register the handler interfaces that Arius resolves through IMediator.
        // The source generator can see these handlers, but DI cannot supply the per-repository
        // account/container constructor arguments without explicit factories here.
        services.AddSingleton<ICommandHandler<ArchiveCommand, ArchiveResult>>(sp =>
            new ArchiveCommandHandler(
                sp.GetRequiredService<IBlobContainerService>(),
                sp.GetRequiredService<IEncryptionService>(),
                sp.GetRequiredService<IChunkIndexService>(),
                sp.GetRequiredService<IChunkStorageService>(),
                sp.GetRequiredService<IFileTreeService>(),
                sp.GetRequiredService<ISnapshotService>(),
                sp.GetRequiredService<IMediator>(),
                sp.GetRequiredService<ILogger<ArchiveCommandHandler>>(),
                sp.GetRequiredService<ILoggerFactory>(),
                accountName,
                containerName,
                sp.GetRequiredService<FileExclusionFilter>()));

        services.AddSingleton<ICommandHandler<RestoreCommand, RestoreResult>>(sp =>
            new RestoreCommandHandler(
                sp.GetRequiredService<IEncryptionService>(),
                sp.GetRequiredService<IChunkIndexService>(),
                sp.GetRequiredService<IChunkStorageService>(),
                sp.GetRequiredService<IFileTreeService>(),
                sp.GetRequiredService<ISnapshotService>(),
                sp.GetRequiredService<IMediator>(),
                sp.GetRequiredService<IStorageCostEstimator>(),
                sp.GetRequiredService<ILogger<RestoreCommandHandler>>(),
                accountName,
                containerName));

        services.AddSingleton<ICommandHandler<RepairChunkIndexCommand, RepairChunkIndexResult>>(sp =>
            new RepairChunkIndexCommandHandler(
                sp.GetRequiredService<IChunkIndexService>(),
                sp.GetRequiredService<ILogger<RepairChunkIndexCommandHandler>>(),
                accountName,
                containerName));

        services.AddSingleton<IStreamQueryHandler<ListQuery, RepositoryEntry>>(sp =>
            new ListQueryHandler(
                sp.GetRequiredService<IChunkIndexService>(),
                sp.GetRequiredService<IFileTreeService>(),
                sp.GetRequiredService<ISnapshotService>(),
                sp.GetRequiredService<ILogger<ListQueryHandler>>(),
                sp.GetRequiredService<FileExclusionFilter>(),
                accountName,
                containerName));

        services.AddSingleton<IStreamQueryHandler<ContainerNamesQuery, string>>(sp =>
            new ContainerNamesQueryHandler(
                sp));

        services.AddSingleton<IStreamQueryHandler<ChunkHydrationStatusQuery, ChunkHydrationStatusResult>>(sp =>
            new ChunkHydrationStatusQueryHandler(
                sp.GetRequiredService<IChunkIndexService>(),
                sp.GetRequiredService<IChunkStorageService>(),
                sp.GetRequiredService<ILogger<ChunkHydrationStatusQueryHandler>>()));

        services.AddSingleton<IStreamQueryHandler<SnapshotsListQuery, SnapshotInfo>>(sp =>
            new SnapshotsListQueryHandler(
                sp.GetRequiredService<ISnapshotService>(),
                sp.GetRequiredService<ILogger<SnapshotsListQueryHandler>>()));

        services.AddSingleton<IStreamQueryHandler<SnapshotDiffQuery, SnapshotDiffEntry>>(sp =>
            new SnapshotDiffQueryHandler(
                sp.GetRequiredService<ISnapshotService>(),
                sp.GetRequiredService<IFileTreeService>(),
                sp.GetRequiredService<ILogger<SnapshotDiffQueryHandler>>()));

        services.AddSingleton<IQueryHandler<StatisticsQuery, RepositoryStatistics>>(sp =>
            new StatisticsQueryHandler(
                sp.GetRequiredService<ISnapshotService>(),
                sp.GetRequiredService<IChunkIndexService>(),
                sp.GetRequiredService<IStorageCostEstimator>(),
                sp.GetRequiredService<ILogger<StatisticsQueryHandler>>()));

        services.AddSingleton<IQueryHandler<StorageAccountInfoQuery, StorageAccountInfo>>(sp =>
            new StorageAccountInfoQueryHandler(
                sp.GetRequiredService<IBlobContainerService>(),
                sp.GetRequiredService<IStorageCostEstimator>()));

        return services;
    }

    private sealed class NullBlobServiceFactory : IBlobServiceFactory
    {
        public Task<IBlobService> CreateAsync(
            string accountName,
            string? accountKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IBlobService>(new NullBlobService());
    }

    private sealed class NullBlobService : IBlobService
    {
        public async IAsyncEnumerable<string> GetContainerNamesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }

        public Task<IBlobContainerService> OpenContainerServiceAsync(
            string containerName,
            PreflightMode preflightMode,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
