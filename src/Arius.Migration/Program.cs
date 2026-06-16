using Arius.AzureBlob;
using Arius.Core;
using Arius.Core.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arius.Migration;

/// <summary>
/// Standalone tool that migrates an old "v5" Arius repository (woutervanranst/Arius, DatabaseVersion 5)
/// to the v7 (arius7) format, in place, against an Azure Blob container.
///
/// Usage:
///   Arius.Migration -a &lt;account&gt; -k &lt;key&gt; -c &lt;container&gt; -p &lt;passphrase&gt; [--dry-run]
///
/// The migration does NOT re-download, re-encrypt, or re-hash any chunk data — v5 chunk hashes,
/// encryption (AES-256-CBC) and compression (gzip) are already readable by v7. It reads the v5
/// SQLite state DB, upserts v7 metadata onto the existing chunk blobs, rebuilds the chunk index with
/// the existing repair functionality, then builds the v7 filetrees + snapshot from the SQLite data.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = MigrationOptions.Parse(args);
        if (options is null)
            return 1; // Parse printed the usage/error.

        // ── Resolve credentials (CLI flag → ARIUS_ACCOUNT / ARIUS_KEY env) ─────────────
        var account = options.Account ?? Environment.GetEnvironmentVariable("ARIUS_ACCOUNT");
        if (string.IsNullOrWhiteSpace(account))
        {
            Console.Error.WriteLine("Error: no account. Use --account/-a or set ARIUS_ACCOUNT.");
            return 1;
        }

        var key = options.Key ?? Environment.GetEnvironmentVariable("ARIUS_KEY");

        using var loggerFactory = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Information)
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }));
        var logger = loggerFactory.CreateLogger("Arius.Migration");

        try
        {
            // ── Build the same DI graph the CLI uses, against the real Azure container ─────
            var blobService   = await new AzureBlobServiceFactory().CreateAsync(account, key);
            var blobContainer = await blobService.OpenContainerServiceAsync(options.Container, PreflightMode.ReadWrite);

            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(loggerFactory);
            services.AddLogging();
            services.AddArius(blobContainer, options.Passphrase, account, options.Container);
            await using var provider = services.BuildServiceProvider();

            var migrator = new MigrateV5(provider, account, options.Container, options.Passphrase);
            await migrator.RunAsync(options.DryRun, CancellationToken.None);
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Migration failed: {Message}", ex.Message);
            return 1;
        }
    }
}

/// <summary>Parsed command-line options. Mirrors the arius7 CLI flag names (-a/-k/-p/-c).</summary>
internal sealed record MigrationOptions
{
    public string? Account    { get; init; }
    public string? Key        { get; init; }
    public string? Passphrase { get; init; }
    public required string Container { get; init; }
    public bool DryRun        { get; init; }

    public static MigrationOptions? Parse(string[] args)
    {
        string? account = null, key = null, passphrase = null, container = null;
        var dryRun = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-a" or "--account":    account    = Next(args, ref i); break;
                case "-k" or "--key":        key        = Next(args, ref i); break;
                case "-p" or "--passphrase": passphrase = Next(args, ref i); break;
                case "-c" or "--container":  container  = Next(args, ref i); break;
                case "--dry-run":            dryRun     = true; break;
                default:
                    Console.Error.WriteLine($"Error: unknown argument '{args[i]}'.");
                    PrintUsage();
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(container))
        {
            Console.Error.WriteLine("Error: --container/-c is required.");
            PrintUsage();
            return null;
        }

        return new MigrationOptions { Account = account, Key = key, Passphrase = passphrase, Container = container, DryRun = dryRun };

        static string? Next(string[] args, ref int i) => ++i < args.Length ? args[i] : null;
    }

    private static void PrintUsage() =>
        Console.Error.WriteLine("Usage: Arius.Migration -a <account> -k <key> -c <container> -p <passphrase> [--dry-run]");
}
