using Arius.Core.Features.Archive;
using Arius.Core.Features.Hydration;
using Arius.Core.Features.List;
using Arius.Core.Features.Restore;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
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
    /// <param name="cacheBudgetBytes">Maximum cache budget, in bytes, for the chunk index service.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance for chaining.</returns>
    public static IServiceCollection AddArius(
        this IServiceCollection services,
        IBlobContainerService     blobContainer,
        string?                 passphrase,
        string                  accountName,
        string                  containerName,
        long                    cacheBudgetBytes = ChunkIndexService.DefaultCacheBudgetBytes)
    {
        // Storage
        services.AddSingleton(blobContainer);

        // Encryption
        IEncryptionService encryption = passphrase is not null
            ? new PassphraseEncryptionService(passphrase)
            : new PlaintextPassthroughService();
        services.AddSingleton(encryption);

        // Chunk index
        services.AddSingleton(sp =>
            new ChunkIndexService(
                sp.GetRequiredService<IBlobContainerService>(),
                sp.GetRequiredService<IEncryptionService>(),
                accountName,
                containerName,
                cacheBudgetBytes));

        // NOTE: AddMediator() is intentionally NOT called here.
        // The source generator must run in the outermost assembly (Arius.Cli or test project)
        // so it can discover INotificationHandler<T> implementations in both Core and CLI.
        // The caller (CLI startup or test harness) is responsible for calling AddMediator().

        // Re-register the handler interfaces that Arius resolves through IMediator.
        // The source generator can see these handlers, but DI cannot supply the per-repository
        // account/container constructor arguments without explicit factories here.
        services.AddSingleton<ICommandHandler<ArchiveCommand, ArchiveResult>>(sp =>
            new ArchivePipelineHandler(
                sp.GetRequiredService<IBlobContainerService>(),
                sp.GetRequiredService<IEncryptionService>(),
                sp.GetRequiredService<ChunkIndexService>(),
                sp.GetRequiredService<IMediator>(),
                sp.GetRequiredService<ILogger<ArchivePipelineHandler>>(),
                accountName,
                containerName));

        services.AddSingleton<ICommandHandler<RestoreCommand, RestoreResult>>(sp =>
            new RestorePipelineHandler(
                sp.GetRequiredService<IBlobContainerService>(),
                sp.GetRequiredService<IEncryptionService>(),
                sp.GetRequiredService<ChunkIndexService>(),
                sp.GetRequiredService<IMediator>(),
                sp.GetRequiredService<ILogger<RestorePipelineHandler>>(),
                accountName,
                containerName));

        services.AddSingleton<IStreamQueryHandler<ListRepositoryEntriesCommand, RepositoryEntry>>(sp =>
            new ListRepositoryEntriesHandler(
                sp.GetRequiredService<IBlobContainerService>(),
                sp.GetRequiredService<IEncryptionService>(),
                sp.GetRequiredService<ChunkIndexService>(),
                sp.GetRequiredService<ILogger<ListRepositoryEntriesHandler>>(),
                accountName,
                containerName));

        services.AddSingleton<IStreamQueryHandler<ResolveFileHydrationStatusesCommand, FileHydrationStatusResult>>(sp =>
            new ResolveFileHydrationStatusesHandler(
                sp.GetRequiredService<IBlobContainerService>(),
                sp.GetRequiredService<ChunkIndexService>(),
                sp.GetRequiredService<ILogger<ResolveFileHydrationStatusesHandler>>()));

        return services;
    }
}
