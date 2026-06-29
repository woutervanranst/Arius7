using Arius.Api.AppData;
using Arius.Api.Jobs;
using Arius.AzureBlob;
using Arius.Core;
using Arius.Core.Shared;
using Arius.Core.Shared.Storage;
using Serilog;
using Serilog.Events;
using Serilog.Templates;

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
    private readonly IBlobServiceFactory _blobServiceFactory;
    private readonly ILoggerFactory      _loggerFactory;
    private readonly ILogger<RepositoryProviderRegistry> _logger;

    private readonly object _gate = new();
    private readonly Dictionary<long, Lazy<Task<ServiceProvider>>> _readProviders = new();

    // One shared logger factory per repository, writing a rolling file into the repo's CLI logs
    // directory. Cached independently of providers: a job disposing its provider (or Evict) must
    // not close the repo's rolling log. Disposed only at registry shutdown (DisposeAsync) or when
    // the repository is removed for good (Remove) — never on a plain Evict.
    private readonly Dictionary<long, ILoggerFactory> _repoLoggerFactories = new();

    public RepositoryProviderRegistry(
        AppDatabase database,
        SecretProtector secrets,
        IBlobServiceFactory blobServiceFactory,
        ILoggerFactory loggerFactory)
    {
        _database           = database;
        _secrets            = secrets;
        _blobServiceFactory = blobServiceFactory;
        _loggerFactory      = loggerFactory;
        _logger             = loggerFactory.CreateLogger<RepositoryProviderRegistry>();
    }

    /// <summary>Gets (building once, then caching) the shared read-only provider for a repository.</summary>
    public Task<ServiceProvider> GetReadProviderAsync(long repositoryId, CancellationToken cancellationToken)
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

        return lazy.Value;
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
    /// Fully removes a repository from the registry: evicts its cached read provider AND disposes its
    /// shared rolling-log factory (releasing the file handle). Use on repository <b>delete</b> — unlike
    /// <see cref="Evict"/>, which is for archive/properties changes where the repo lives on and its log
    /// must keep writing.
    /// </summary>
    public void Remove(long repositoryId)
    {
        Evict(repositoryId); // dispose + drop the cached read provider (fire-and-forget, as today)

        ILoggerFactory? factory;
        lock (_gate)
        {
            if (!_repoLoggerFactories.Remove(repositoryId, out factory))
                return;
        }

        factory.Dispose(); // disposes the SerilogLoggerProvider → flushes & closes the rolling file
    }

    private async Task<ServiceProvider> BuildAsync(long repositoryId, PreflightMode mode, JobSink jobSink, CancellationToken cancellationToken)
    {
        var connection = LoadConnection(repositoryId);

        var blobService   = await _blobServiceFactory.CreateAsync(connection.AccountName, connection.AccountKey, cancellationToken).ConfigureAwait(false);
        var blobContainer = await blobService.OpenContainerServiceAsync(connection.Container, mode, cancellationToken).ConfigureAwait(false);

        var services = new ServiceCollection();

        // Route Core's logging to the repository's shared rolling log file (same location the CLI
        // writes to), so every Web-launched operation is captured on disk, not just the console.
        var repoLoggerFactory = GetOrCreateRepoLoggerFactory(repositoryId, connection.AccountName, connection.Container);
        services.AddSingleton(repoLoggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // Per-job sink resolved by the event forwarders (auto-registered by AddMediator).
        services.AddSingleton(jobSink);

        // AddMediator() (generated in this assembly) must run here, not inside AddArius.
        services.AddMediator();
        services.AddAzureBlobStorage();
        services.AddArius(blobContainer, connection.Passphrase, connection.AccountName, connection.Container);

        _logger.LogInformation("Built {Mode} provider for repository {RepositoryId} ({Account}/{Container})", mode, repositoryId, connection.AccountName, connection.Container);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Gets (building once, then caching) the shared logger factory for a repository. It writes a
    /// per-repository rolling log file into <c>~/.arius/{account}-{container}/logs/</c> — the same
    /// directory the CLI uses — plus the console. The line format mirrors
    /// <c>Arius.Cli.CliBuilder.ConfigureAuditLogging</c> so CLI and Web logs read identically.
    /// </summary>
    private ILoggerFactory GetOrCreateRepoLoggerFactory(long repositoryId, string accountName, string containerName)
    {
        lock (_gate)
        {
            if (_repoLoggerFactories.TryGetValue(repositoryId, out var existing))
                return existing;

            var logDir = RepositoryLocalStatePaths.GetLogsDirectory(accountName, containerName);
            Directory.CreateDirectory(logDir);

            var formatter = new ExpressionTemplate(
                "[{@t:HH:mm:ss.fff}] [{@l:u3}] [T:{ThreadId}] [{Coalesce(Substring(SourceContext, LastIndexOf(SourceContext, '.') + 1), 'Arius')}] {@m}\n{@x}");

            var serilog = new LoggerConfiguration()
                .MinimumLevel.Information()
                .Enrich.WithThreadId()
                .WriteTo.Console()
                .WriteTo.File(
                    formatter,
                    Path.Combine(logDir, "arius-.txt"),
                    rollingInterval:        RollingInterval.Day,
                    fileSizeLimitBytes:     100L * 1024 * 1024,
                    rollOnFileSizeLimit:    true,
                    retainedFileCountLimit: 366,
                    restrictedToMinimumLevel: LogEventLevel.Information)
                .CreateLogger();

            // dispose: true → disposing this factory disposes the Serilog logger, flushing the file.
            var factory = LoggerFactory.Create(b => b.AddSerilog(serilog, dispose: true));
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
