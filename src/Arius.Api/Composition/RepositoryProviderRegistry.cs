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

    private readonly object _gate = new();
    private readonly Dictionary<long, Lazy<Task<ServiceProvider>>> _readProviders = new();

    // One logger factory per repository, each owning the Serilog logger that writes that repo's rolling file.
    // Cached so every provider shares one file handle, and so Remove can dispose it on repository delete.
    private readonly Dictionary<long, ILoggerFactory> _repoLoggerFactories = new();

    // Per-repository lifecycle generation, bumped by Remove. A build captures the generation before its
    // awaited compose and re-checks it when registering the logger factory: without that, a build already in
    // flight when the repository is deleted would cache a *fresh* factory under the removed id, which nothing
    // disposes — holding the deleted repository's rolling log file open for the rest of the process. Entries
    // are one int per removed id (ids are reused by SQLite, so a set of dead ids would not be enough).
    private readonly Dictionary<long, int> _repoGenerations = new();

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
    /// Fully removes a repository from the registry: drops its cached read provider AND its per-repo logger
    /// factory, disposing both. Use on repository <b>delete</b> — unlike <see cref="Evict"/>, which is for
    /// archive/properties changes where the repo lives on. Disposing the factory closes the repo's rolling log
    /// file, so its logs can be deleted along with it.
    /// </summary>
    public void Remove(long repositoryId)
    {
        Lazy<Task<ServiceProvider>>? provider;
        ILoggerFactory? factory;
        lock (_gate)
        {
            _readProviders.Remove(repositoryId, out provider);
            _repoLoggerFactories.Remove(repositoryId, out factory);
            // Invalidate any build already in flight for this repository, so it cannot re-register a factory
            // behind us (see _repoGenerations).
            _repoGenerations[repositoryId] = CurrentGenerationLocked(repositoryId) + 1;
        }

        // Fire-and-forget; the await chain keeps the ordering. A concurrent build either re-reads the
        // dictionaries under the gate and gets a fresh factory, or is rejected by the generation check.
        _ = DisposeProviderThenFactoryAsync(provider, factory);
    }

    /// <summary>Wires the repository's diagnostics logger onto a job sink before its provider is built, so the
    /// <c>[ETA]</c> trace covers the (potentially slow) provider-build phase too. Best-effort: a failure only
    /// leaves the trace silent, it never fails the job. No-op for an inert (non-job) sink.</summary>
    public void AttachJobDiagnostics(JobSink sink, long repositoryId)
    {
        if (sink.JobId is null)
            return;

        try
        {
            // Captured before the DB read, so a delete that lands while we resolve the connection rejects
            // this attachment instead of caching a factory for a repository that is going away.
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

        // The Arius.Core graph (handlers + storage) is composed behind an interface so tests can
        // swap in a scripted fake without touching Arius.Core.
        await _coreComposer.ComposeAsync(services, connection, mode, cancellationToken).ConfigureAwait(false);

        // Route Core's logging to the repository's own rolling log file — the same cached factory
        // AttachJobDiagnostics already wired onto the job sink.
        var repoLoggerFactory = GetOrCreateRepoLoggerFactory(repositoryId, generation, connection.AccountName, connection.Container);
        services.AddSingleton(repoLoggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        _logger.LogInformation("Built {Mode} provider for repository {RepositoryId} ({Account}/{Container})", mode, repositoryId, connection.AccountName, connection.Container);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Gets (building once, then caching) the logger factory a repository's providers use. It writes the repo's
    /// rolling <c>arius-{date}.txt</c> under <c>~/.arius/{account}-{container}/logs/</c> — the same directory and
    /// format the CLI uses (<see cref="AriusLogging"/>).
    /// Throws <see cref="RepositoryNotFoundException"/> when <paramref name="generation"/> is stale, i.e. the
    /// repository was removed after the caller started — the caller's whole build is void at that point.
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

    /// <summary>Disposes a removed repository's read provider and then its logger factory. The order matters:
    /// the provider resolves loggers from the factory, so it must be gone before the factory is.</summary>
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
