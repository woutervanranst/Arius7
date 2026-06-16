using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.ChunkHydrationStatusQuery;
using Arius.Core.Features.ContainerNamesQuery;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.RepairChunkIndexCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Features.SnapshotsQuery;
using Arius.Core.Features.StatsQuery;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    /// <returns>The same <see cref="IServiceCollection"/> instance for chaining.</returns>
    public static IServiceCollection AddArius(
        this IServiceCollection services,
        IBlobContainerService     blobContainer,
        string?                 passphrase,
        string                  accountName,
        string                  containerName)
    {
        // Storage
        services.AddSingleton(blobContainer);
        if (services.All(service => service.ServiceType != typeof(IBlobServiceFactory)))
        {
            services.AddSingleton<IBlobServiceFactory, NullBlobServiceFactory>();
        }

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
                containerName));

        services.AddSingleton<ICommandHandler<RestoreCommand, RestoreResult>>(sp =>
            new RestoreCommandHandler(
                sp.GetRequiredService<IEncryptionService>(),
                sp.GetRequiredService<IChunkIndexService>(),
                sp.GetRequiredService<IChunkStorageService>(),
                sp.GetRequiredService<IFileTreeService>(),
                sp.GetRequiredService<ISnapshotService>(),
                sp.GetRequiredService<IMediator>(),
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

        services.AddSingleton<ICommandHandler<SnapshotsQuery, IReadOnlyList<SnapshotInfo>>>(sp =>
            new SnapshotsQueryHandler(
                sp.GetRequiredService<ISnapshotService>(),
                sp.GetRequiredService<ILogger<SnapshotsQueryHandler>>()));

        services.AddSingleton<ICommandHandler<StatsQuery, RepositoryStats>>(sp =>
            new StatsQueryHandler(
                sp.GetRequiredService<ISnapshotService>(),
                sp.GetRequiredService<IChunkIndexService>(),
                sp.GetRequiredService<ILogger<StatsQueryHandler>>()));

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
