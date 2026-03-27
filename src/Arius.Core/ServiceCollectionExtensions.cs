using Arius.Core.Archive;
using Arius.Core.ChunkIndex;
using Arius.Core.Encryption;
using Arius.Core.Ls;
using Arius.Core.Restore;
using Arius.Core.Storage;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arius.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Arius.Core services and command handlers.
    ///
    /// The Mediator source generator auto-registers handlers by concrete type, but cannot
    /// resolve their <c>string accountName</c> / <c>containerName</c> constructor parameters.
    /// This method calls <see cref="MediatorExtensions.AddMediator"/> first (required for the
    /// generated <c>Mediator</c> class), then re-registers each handler by its
    /// <c>ICommandHandler&lt;,&gt;</c> interface using explicit factory delegates — MS DI's
    /// "last registration wins" behaviour ensures these factories are what Mediator resolves.
    /// <summary>
    /// Registers Arius core services and command handlers into the provided <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="blobStorage">The blob storage implementation to use for persisted data.</param>
    /// <param name="passphrase">If non-null, enables passphrase-based encryption; if null, a plaintext passthrough is used.</param>
    /// <param name="accountName">The account name used to scope chunk indexing and handler operations.</param>
    /// <param name="containerName">The container name used to scope chunk indexing and handler operations.</param>
    /// <param name="cacheBudgetBytes">Maximum cache budget, in bytes, for the chunk index service.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance for chaining.</returns>
    public static IServiceCollection AddArius(
        this IServiceCollection services,
        IBlobStorageService     blobStorage,
        string?                 passphrase,
        string                  accountName,
        string                  containerName,
        long                    cacheBudgetBytes = ChunkIndexService.DefaultCacheBudgetBytes)
    {
        // Storage
        services.AddSingleton(blobStorage);

        // Encryption
        IEncryptionService encryption = passphrase is not null
            ? new PassphraseEncryptionService(passphrase)
            : new PlaintextPassthroughService();
        services.AddSingleton(encryption);

        // Chunk index
        services.AddSingleton(sp =>
            new ChunkIndexService(
                sp.GetRequiredService<IBlobStorageService>(),
                sp.GetRequiredService<IEncryptionService>(),
                accountName,
                containerName,
                cacheBudgetBytes));

        // NOTE: AddMediator() is intentionally NOT called here.
        // The source generator must run in the outermost assembly (Arius.Cli or test project)
        // so it can discover INotificationHandler<T> implementations in both Core and CLI.
        // The caller (CLI startup or test harness) is responsible for calling AddMediator().

        // Re-register handlers by ICommandHandler<,> interface with factory delegates.
        // MS DI "last registration wins" — these override Mediator's concrete-type registrations
        // and correctly pass accountName/containerName which DI cannot otherwise resolve.
        services.AddSingleton<ICommandHandler<ArchiveCommand, ArchiveResult>>(sp =>
            new ArchivePipelineHandler(
                sp.GetRequiredService<IBlobStorageService>(),
                sp.GetRequiredService<IEncryptionService>(),
                sp.GetRequiredService<ChunkIndexService>(),
                sp.GetRequiredService<IMediator>(),
                sp.GetRequiredService<ILogger<ArchivePipelineHandler>>(),
                accountName,
                containerName));

        services.AddSingleton<ICommandHandler<RestoreCommand, RestoreResult>>(sp =>
            new RestorePipelineHandler(
                sp.GetRequiredService<IBlobStorageService>(),
                sp.GetRequiredService<IEncryptionService>(),
                sp.GetRequiredService<ChunkIndexService>(),
                sp.GetRequiredService<IMediator>(),
                sp.GetRequiredService<ILogger<RestorePipelineHandler>>(),
                accountName,
                containerName));

        services.AddSingleton<ICommandHandler<LsCommand, LsResult>>(sp =>
            new LsHandler(
                sp.GetRequiredService<IBlobStorageService>(),
                sp.GetRequiredService<IEncryptionService>(),
                sp.GetRequiredService<ChunkIndexService>(),
                sp.GetRequiredService<ILogger<LsHandler>>(),
                accountName,
                containerName));

        return services;
    }
}
