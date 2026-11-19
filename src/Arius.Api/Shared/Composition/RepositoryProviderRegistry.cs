using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Jobs;
using Arius.Core.Shared.Storage;
using Arius.Core.Shared;
using Arius.Core;

namespace Arius.Api.Shared.Composition;

/// <summary>
/// Builds and caches service providers. <see cref="ServiceCollectionExtensions.AddArius"/> registers Arius.Core as
/// singletons bound to one account, container and passphrase, so each repository gets its own provider with its
/// own <c>IMediator</c>.
/// <list type="bullet">
///   <item><b>Read providers</b>: cached per repository and <see cref="PreflightMode.ReadOnly"/>, so caches stay warm
///   across requests. Evicted on a properties change, a delete, or after an archive.</item>
///   <item><b>Job providers</b>: one per archive or restore, owned and disposed by the job. This isolates the job's
///   events and avoids reusing a chunk index, which is single-shot after flush.</item>
///   <item><b>The repository-less provider</b>: built once, for Core requests that need no repository, such as
///   listing an account's containers.</item>
/// </list>
/// </summary>
public sealed class RepositoryProviderRegistry : IAsyncDisposable
{
    private readonly AppDatabase         _database;
    private readonly SecretProtector     _secrets;
    private readonly IRepositoryCoreComposer _coreComposer;
    private readonly ILoggerFactory      _loggerFactory;
    private readonly ILogger<RepositoryProviderRegistry> _logger;

    private readonly object _gate = new();
    private readonly Dictionary<long, Lazy<Task<ServiceProvider>>> _readProviders = new();

    // One logger factory per repository, owning the Serilog logger that writes its log file. Cached so all of the
    // repository's providers share one file handle and Remove can dispose it on delete.
    private readonly Dictionary<long, ILoggerFactory> _repoLoggerFactories = new();

    // Per-repository generation, incremented by Remove. A build captures it before its awaited compose and checks it
    // again when registering the logger factory; otherwise a build in flight during a delete would cache a new
    // factory under the removed id that nothing disposes, keeping the deleted repository's log file open.
    private readonly Dictionary<long, int> _repoGenerations = new();

    private readonly Lazy<ServiceProvider> _repositoryLessProvider;

    public RepositoryProviderRegistry(
        AppDatabase database,
        SecretProtector secrets,
        IRepositoryCoreComposer coreComposer,
        ILoggerFactory loggerFactory)
    {
        _database           = database;
        _secrets            = secrets;
        _coreComposer       = coreComposer;
        _loggerFactory      = loggerFactory;
        _logger             = loggerFactory.CreateLogger<RepositoryProviderRegistry>();
        _repositoryLessProvider = new Lazy<ServiceProvider>(BuildRepositoryLessProvider);
    }

    /// <summary>Gets the provider for Core requests that need no repository, building it on first use. The registry
    /// owns it; do not dispose it.</summary>
    public ServiceProvider GetRepositoryLessProvider() => _repositoryLessProvider.Value;

    private ServiceProvider BuildRepositoryLessProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new JobSink());   // inert; the event forwarders registered by AddMediator depend on it
        services.AddMediator();
        _coreComposer.ComposeWithoutRepository(services);
        services.AddSingleton(_loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        return services.BuildServiceProvider();
    }

    /// <summary>Gets the cached read-only provider for a repository, building it on first use.</summary>
    public async Task<ServiceProvider> GetReadProviderAsync(long repositoryId, CancellationToken cancellationToken)
    {
        Lazy<Task<ServiceProvider>> lazy;
        lock (_gate)
        {
            if (!_readProviders.TryGetValue(repositoryId, out lazy!))
            {
                // Read providers get an inert JobSink; the event forwarders never fire for them.
                lazy = new Lazy<Task<ServiceProvider>>(() => BuildAsync(repositoryId, PreflightMode.ReadOnly, new JobSink(), CancellationToken.None));
                _readProviders[repositoryId] = lazy;
            }
        }

        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            // Evict a failed build (e.g. the container does not exist before the first archive) so the next call
            // retries instead of replaying the fault.
            lock (_gate)
            {
                if (_readProviders.TryGetValue(repositoryId, out var current) && current == lazy)
                    _readProviders.Remove(repositoryId);
            }
            throw;
        }
    }

    /// <summary>
    /// Builds a fresh, dedicated provider for a single long-running command, wired to the given
    /// per-job <see cref="JobSink"/>. The caller owns it and must dispose it when the job ends.
    /// </summary>
    public Task<ServiceProvider> CreateJobProviderAsync(long repositoryId, PreflightMode mode, JobSink jobSink, CancellationToken cancellationToken)
        => BuildAsync(repositoryId, mode, jobSink, cancellationToken);

    /// <summary>Disposes and removes the cached read provider for a repository (e.g. after a properties change or archive).</summary>
    public void Evict(long repositoryId)
    {
        Lazy<Task<ServiceProvider>>? lazy;
        lock (_gate)
        {
            if (!_readProviders.Remove(repositoryId, out lazy))
                return;
        }

        _ = DisposeProviderAsync(lazy);
    }

    /// <summary>
    /// Removes a deleted repository: disposes its cached read provider and its logger factory, which closes its log
    /// file so the logs can be deleted. Use <see cref="Evict"/> when the repository still exists.
    /// </summary>
    public void Remove(long repositoryId)
    {
        Lazy<Task<ServiceProvider>>? provider;
        ILoggerFactory? factory;
        lock (_gate)
        {
            _readProviders.Remove(repositoryId, out provider);
            _repoLoggerFactories.Remove(repositoryId, out factory);
            // Invalidates any build in flight, so it cannot register a factory afterwards (see _repoGenerations).
            _repoGenerations[repositoryId] = CurrentGenerationLocked(repositoryId) + 1;
        }

        // Fire-and-forget; the await chain preserves the disposal order.
        _ = DisposeProviderThenFactoryAsync(provider, factory);
    }

    /// <summary>Attaches the repository's diagnostics logger to a job sink before its provider is built, so the
    /// <c>[ETA]</c> trace also covers the provider build. Best-effort: a failure only disables the trace. No-op for an
    /// inert sink.</summary>
    public void AttachJobDiagnostics(JobSink sink, long repositoryId)
    {
        if (sink.JobId is null)
            return;

        try
        {
            // Captured before the database read, so a delete during the read rejects this attachment instead of
            // caching a factory for a removed repository.
            var generation = CurrentGeneration(repositoryId);
            var connection = LoadConnection(repositoryId);
            var repoLoggerFactory = GetOrCreateRepoLoggerFactory(repositoryId, generation, connection.AccountName, connection.Container);
            sink.AttachDiagnosticsLogger(repoLoggerFactory.CreateLogger<JobSink>());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not attach the diagnostics logger for repository {RepositoryId}; [ETA] tracing will be off for this job", repositoryId);
        }
    }

    private async Task<ServiceProvider> BuildAsync(long repositoryId, PreflightMode mode, JobSink jobSink, CancellationToken cancellationToken)
    {
        var connection = LoadConnection(repositoryId);
        // Captured before the awaited compose below; re-checked when the logger factory is registered.
        var generation = CurrentGeneration(repositoryId);

        var services = new ServiceCollection();

        // Per-job sink resolved by the event forwarders (auto-registered by AddMediator).
        services.AddSingleton(jobSink);

        // AddMediator() (generated in this assembly) must run here, not inside the composer.
        services.AddMediator();

        await _coreComposer.ComposeAsync(services, connection, mode, cancellationToken).ConfigureAwait(false);

        // Core logs to the repository's own log file, through the same factory AttachJobDiagnostics uses.
        var repoLoggerFactory = GetOrCreateRepoLoggerFactory(repositoryId, generation, connection.AccountName, connection.Container);
        services.AddSingleton(repoLoggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        _logger.LogInformation("Built {Mode} provider for repository {RepositoryId} ({Account}/{Container})", mode, repositoryId, connection.AccountName, connection.Container);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Gets the logger factory shared by a repository's providers, creating it on first use. Throws
    /// <see cref="RepositoryNotFoundException"/> when <paramref name="generation"/> is stale, i.e. the repository was
    /// removed after the caller started.
    /// </summary>
    private ILoggerFactory GetOrCreateRepoLoggerFactory(long repositoryId, int generation, string accountName, string containerName)
    {
        lock (_gate)
        {
            if (CurrentGenerationLocked(repositoryId) != generation)
                throw new RepositoryNotFoundException(repositoryId);

            if (_repoLoggerFactories.TryGetValue(repositoryId, out var existing))
                return existing;

            var logDir = RepositoryLocalStatePaths.GetLogsDirectory(accountName, containerName);
            var factory = AriusLogging.CreateRepositoryLoggerFactory(logDir);
            _repoLoggerFactories[repositoryId] = factory;
            return factory;
        }
    }

    private int CurrentGeneration(long repositoryId)
    {
        lock (_gate) return CurrentGenerationLocked(repositoryId);
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private int CurrentGenerationLocked(long repositoryId) =>
        _repoGenerations.TryGetValue(repositoryId, out var generation) ? generation : 0;

    private RepositoryConnection LoadConnection(long repositoryId)
    {
        var repository = _database.GetRepository(repositoryId)
            ?? throw new RepositoryNotFoundException(repositoryId);
        var account = _database.GetAccount(repository.AccountId)
            ?? throw new InvalidOperationException($"Repository {repositoryId} references missing account {repository.AccountId}.");

        return new RepositoryConnection(
            RepositoryId: repository.Id,
            Alias:        repository.Alias,
            AccountName:  account.Name,
            AccountKey:   _secrets.Unprotect(account.EncryptedAccountKey),
            Container:    repository.Container,
            Passphrase:   _secrets.Unprotect(repository.EncryptedPassphrase),
            LocalPath:    repository.LocalPath);
    }

    public async ValueTask DisposeAsync()
    {
        List<Lazy<Task<ServiceProvider>>> providers;
        List<ILoggerFactory> loggerFactories;
        lock (_gate)
        {
            providers = _readProviders.Values.ToList();
            _readProviders.Clear();
            loggerFactories = _repoLoggerFactories.Values.ToList();
            _repoLoggerFactories.Clear();
        }

        foreach (var provider in providers)
            await DisposeProviderAsync(provider).ConfigureAwait(false);

        if (_repositoryLessProvider.IsValueCreated)
            await _repositoryLessProvider.Value.DisposeAsync().ConfigureAwait(false);

        // Dispose loggers last so any provider-disposal logging still lands in the rolling file.
        foreach (var factory in loggerFactories)
            factory.Dispose();
    }

    private static async Task DisposeProviderAsync(Lazy<Task<ServiceProvider>> lazy)
    {
        try
        {
            var provider = await lazy.Value.ConfigureAwait(false);
            await provider.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // A provider that never built successfully has nothing to dispose.
        }
    }

    /// <summary>Disposes a removed repository's read provider, then its logger factory: the provider resolves
    /// loggers from the factory, so it must go first.</summary>
    private static async Task DisposeProviderThenFactoryAsync(Lazy<Task<ServiceProvider>>? provider, ILoggerFactory? factory)
    {
        if (provider is not null)
            await DisposeProviderAsync(provider).ConfigureAwait(false);
        factory?.Dispose();
    }
}

/// <summary>Thrown when a repository id does not exist in the app database.</summary>
internal sealed class RepositoryNotFoundException(long repositoryId)
    : Exception($"Repository {repositoryId} was not found.")
{
    public long RepositoryId { get; } = repositoryId;
}
