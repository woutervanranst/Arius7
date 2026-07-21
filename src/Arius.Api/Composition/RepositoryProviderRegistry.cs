using Arius.Api.AppData;
using Arius.Api.Jobs;
using Arius.Core;
using Arius.Core.Shared;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Composition;

/// <summary>
/// Builds and caches per-repository service providers. Each provider has its own
/// <c>IMediator</c> + Arius.Core service graph (via <see cref="ServiceCollectionExtensions.AddArius"/>)
/// bound to one account/container/passphrase — Arius.Core registers everything as singletons scoped
/// to a single repository, so providers are reused, not rebuilt per request.
///
/// Two lifetimes:
/// <list type="bullet">
///   <item><b>Read providers</b> — long-lived, cached per repo, <see cref="PreflightMode.ReadOnly"/>; warm
///   caches across requests. Evicted/disposed on properties change, delete, or after an archive.</item>
///   <item><b>Job providers</b> — fresh per long-running archive/restore, owned and disposed by the job:
///   this isolates per-job events (own IMediator) and avoids reusing a chunk index that becomes single-shot
///   after flush.</item>
/// </list>
/// </summary>
public sealed class RepositoryProviderRegistry : IAsyncDisposable
{
    private readonly AppDatabase         _database;
    private readonly SecretProtector     _secrets;
    private readonly IRepositoryCoreComposer _coreComposer;
    private readonly ILoggerFactory      _loggerFactory;
    private readonly ILogger<RepositoryProviderRegistry> _logger;
    private readonly Serilog.ILogger     _rootLogger;   // the one process-wide logger; per-repo factories are repo-tagged views of it

    private readonly object _gate = new();
    private readonly Dictionary<long, Lazy<Task<ServiceProvider>>> _readProviders = new();

    // One repo-tagged logger factory per repository (a view of the shared root logger, not an owner of any
    // file). Cached only to avoid rebuilding the wrapper per provider. The rolling file itself is owned by
    // the root logger's Map sink and flushed at process shutdown, so disposing a factory here is cheap and
    // never touches the file.
    private readonly Dictionary<long, ILoggerFactory> _repoLoggerFactories = new();

    public RepositoryProviderRegistry(
        AppDatabase database,
        SecretProtector secrets,
        IRepositoryCoreComposer coreComposer,
        ILoggerFactory loggerFactory,
        Serilog.ILogger rootLogger)
    {
        _database           = database;
        _secrets            = secrets;
        _coreComposer       = coreComposer;
        _loggerFactory      = loggerFactory;
        _logger             = loggerFactory.CreateLogger<RepositoryProviderRegistry>();
        _rootLogger         = rootLogger;
    }

    /// <summary>Gets (building once, then caching) the shared read-only provider for a repository.</summary>
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
            // A build that failed (e.g. the container doesn't exist yet — no archive has run) must not
            // poison the cache forever: evict it so the next call rebuilds from scratch instead of
            // replaying the same fault indefinitely.
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
    /// Fully removes a repository from the registry: evicts its cached read provider AND drops its cached
    /// repo-tagged logger factory. Use on repository <b>delete</b> — unlike <see cref="Evict"/>, which is for
    /// archive/properties changes where the repo lives on. The rolling log file is owned by the shared root
    /// logger's Map sink (flushed at process shutdown), so removal drops the wrapper but does not close the
    /// file mid-process — acceptable for a deleted repo.
    /// </summary>
    public void Remove(long repositoryId)
    {
        Evict(repositoryId); // dispose + drop the cached read provider (fire-and-forget)

        ILoggerFactory? factory;
        lock (_gate)
        {
            if (!_repoLoggerFactories.Remove(repositoryId, out factory))
                return;
        }

        factory.Dispose(); // MEL wrapper only — the shared root logger keeps ownership of the rolling file
    }

    private async Task<ServiceProvider> BuildAsync(long repositoryId, PreflightMode mode, JobSink jobSink, CancellationToken cancellationToken)
    {
        var connection = LoadConnection(repositoryId);

        var services = new ServiceCollection();

        // Per-job sink resolved by the event forwarders (auto-registered by AddMediator).
        services.AddSingleton(jobSink);

        // AddMediator() (generated in this assembly) must run here, not inside the composer.
        services.AddMediator();

        // The Arius.Core graph (handlers + storage) is composed behind an interface so tests can
        // swap in a scripted fake without touching Arius.Core.
        await _coreComposer.ComposeAsync(services, connection, mode, cancellationToken).ConfigureAwait(false);

        // Route Core's logging to the repository's shared rolling log file.
        var repoLoggerFactory = GetOrCreateRepoLoggerFactory(repositoryId, connection.AccountName, connection.Container);
        services.AddSingleton(repoLoggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // Route the job sink's [ETA]/throughput diagnostics to the SAME per-repo rolling file as Core's
        // events, so ARIUS_LOG_LEVEL=Debug surfaces them in arius-{date}.txt (not just the API host console,
        // which is invisible under the desktop/Explorer host). Inert read sinks never report, so skip them.
        if (jobSink.JobId is not null)
            jobSink.AttachDiagnosticsLogger(repoLoggerFactory.CreateLogger<JobSink>());

        _logger.LogInformation("Built {Mode} provider for repository {RepositoryId} ({Account}/{Container})", mode, repositoryId, connection.AccountName, connection.Container);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Gets (building once, then caching) the logger factory a repository's providers use. It is a
    /// repo-tagged view of the one process-wide logger (<see cref="AriusLogging"/>): every event is stamped
    /// with the repo's logs directory so the root logger's <c>WriteTo.Map</c> sink routes it into that repo's
    /// rolling <c>arius-{date}.txt</c> under <c>~/.arius/{account}-{container}/logs/</c> — the same file the
    /// CLI writes beside, in the same format. The factory does NOT own the root logger, so it is cheap to
    /// build/dispose; the file sink is owned and flushed by the root logger at shutdown.
    /// </summary>
    private ILoggerFactory GetOrCreateRepoLoggerFactory(long repositoryId, string accountName, string containerName)
    {
        lock (_gate)
        {
            if (_repoLoggerFactories.TryGetValue(repositoryId, out var existing))
                return existing;

            var logDir = RepositoryLocalStatePaths.GetLogsDirectory(accountName, containerName);
            Directory.CreateDirectory(logDir);

            var factory = AriusLogging.CreateRepositoryLoggerFactory(_rootLogger, logDir);
            _repoLoggerFactories[repositoryId] = factory;
            return factory;
        }
    }

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
            LocalPath:    repository.LocalPath,
            DefaultTier:  repository.DefaultTier);
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
}

/// <summary>Thrown when a repository id does not exist in the app database.</summary>
internal sealed class RepositoryNotFoundException(long repositoryId)
    : Exception($"Repository {repositoryId} was not found.")
{
    public long RepositoryId { get; } = repositoryId;
}
