using Arius.AzureBlob;
using Arius.Cli.Commands.Archive;
using Arius.Cli.Commands.Ls;
using Arius.Cli.Commands.Restore;
using Arius.Cli.Commands.Update;
using Arius.Core;
using Arius.Core.ChunkIndex;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Spectre.Console;
using System.CommandLine;

namespace Arius.Cli;

/// <summary>
/// Builds the System.CommandLine root command with all verbs, options, and DI wiring.
/// Extracted from Program.cs so tests can reference it without executing the program.
/// </summary>
public static class CliBuilder
{
    /// <summary>
    /// Creates the CLI option for specifying the Azure Storage account name (`--account` / `-a`).
    /// </summary>
    public static Option<string?> AccountOption() => new("--account", "-a")
    {
        Description = "Azure Storage account name",
    };

    /// <summary>
    /// Creates a command-line option for specifying the Azure Storage account key.
    /// </summary>
    public static Option<string?> KeyOption() => new Option<string?>("--key", "-k")
    {
        Description = "Azure Storage account key (omit to use Azure CLI login)",
    };

    /// <summary>
    /// Creates the --passphrase (-p) option used to provide an optional encryption passphrase.
    /// </summary>
    public static Option<string?> PassphraseOption() => new Option<string?>("--passphrase", "-p")
    {
        Description = "Encryption passphrase (omit for no encryption)",
    };

    /// <summary>
    /// Creates the required CLI option for specifying the Azure Blob container name (`--container` / `-c`).
    /// </summary>
    public static Option<string> ContainerOption() => new Option<string>("--container", "-c")
    {
        Description = "Azure Blob container name",
        Required    = true,
    };

    // ── Root command ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the root command with all subcommands and DI wiring.
    /// Optionally accepts a service-provider factory so tests can inject mock handlers.
    /// </summary>
    /// <param name="serviceProviderFactory">Optional factory; if null, production services are used.</param>
    public static RootCommand BuildRootCommand(
        Func<string, string?, string?, string, PreflightMode, Task<IServiceProvider>>? serviceProviderFactory = null)
    {
        serviceProviderFactory ??= BuildProductionServices;

        var rootCommand = new RootCommand("Arius — content-addressable archival to Azure Blob Storage");
        rootCommand.Subcommands.Add(ArchiveVerb.Build(serviceProviderFactory));
        rootCommand.Subcommands.Add(RestoreVerb.Build(serviceProviderFactory));
        rootCommand.Subcommands.Add(LsVerb.Build(serviceProviderFactory));
        rootCommand.Subcommands.Add(UpdateVerb.Build());
        return rootCommand;
    }

    // ── Resolution helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Resolves account name: CLI flag > ARIUS_ACCOUNT env var.
    /// </summary>
    public static string? ResolveAccount(string? cliAccount)
    {
        if (!string.IsNullOrWhiteSpace(cliAccount))
            return cliAccount;

        var env = Environment.GetEnvironmentVariable("ARIUS_ACCOUNT");
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }

    /// <summary>
    /// Resolves account key: CLI flag > ARIUS_KEY env var > user secrets.
    /// </summary>
    public static string? ResolveKey(string? cliKey, string accountName)
    {
        if (!string.IsNullOrWhiteSpace(cliKey))
            return cliKey;

        var env = Environment.GetEnvironmentVariable("ARIUS_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        var config = new ConfigurationBuilder()
            .AddUserSecrets<Arius.Cli.AssemblyMarker>(optional: true)
            .Build();

        return config[$"arius:{accountName}:key"] ?? config["arius:key"];
    }

    // ── Production DI ─────────────────────────────────────────────────────────

    private static async Task<IServiceProvider> BuildProductionServices(
        string        accountName,
        string?       accountKey,
        string?       passphrase,
        string        containerName,
        PreflightMode preflightMode)
    {
        // ── Credential resolution ─────────────────────────────────────────────
        // Key sources (flag → env var → user secrets) win over AzureCliCredential.
        object credential = accountKey is not null
            ? BlobServiceFactory.CreateSharedKeyCredential(accountName, accountKey)
            : BlobServiceFactory.CreateAzureCliCredential();

        // ── Preflight check + service construction (delegated to factory) ─────
        var blobStorage = await BlobServiceFactory.CreateAsync(
            credential,
            accountName,
            containerName,
            preflightMode).ConfigureAwait(false);

        // ── Build DI container ────────────────────────────────────────────────
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: true));
        services.AddSingleton<ProgressState>();

        // AddMediator() is called here (not in AddArius) so the source generator runs
        // in the CLI assembly and discovers INotificationHandler<T> implementations in
        // both Arius.Core and Arius.Cli.
        services.AddMediator();

        services.AddArius(
            blobStorage,
            passphrase,
            accountName,
            containerName);

        return services.BuildServiceProvider();
    }

    // ── Audit logging ─────────────────────────────────────────────────────────

    /// <summary>
    /// Configures the global Serilog logger for one CLI invocation.
    /// Console sink: Warning+.  File sink: Information+.
    /// </summary>
    public static string ConfigureAuditLogging(string accountName, string containerName, string commandName)
    {
        var home    = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var logDir  = Path.Combine(home, ".arius",
            ChunkIndexService.GetRepoDirectoryName(accountName, containerName), "logs");
        Directory.CreateDirectory(logDir);

        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var logFile   = Path.Combine(logDir, $"{timestamp}_{commandName}.txt");

        const string outputTemplate = "[{Timestamp:HH:mm:ss.fff}] [{Level:u3}] [T:{ThreadId}] {Message}{NewLine}{Exception}";

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithThreadId()
            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Warning)
            .WriteTo.File(logFile, outputTemplate: outputTemplate, restrictedToMinimumLevel: LogEventLevel.Information)
            .CreateLogger();

        return logFile;
    }

    internal static string FormatUnhandledExceptionMessage(Exception ex)
    {
        var message = string.IsNullOrWhiteSpace(ex.Message)
            ? ex.GetType().Name
            : ex.Message;

        return $"[red]Error:[/] {Markup.Escape(message)}";
    }

    /// <summary>
    /// Logs the captured console output to the audit log, then closes and flushes the logger.
    /// </summary>
    public static void FlushAuditLog(Recorder recorder)
    {
        var consoleText = recorder.ExportText();
        if (!string.IsNullOrWhiteSpace(consoleText))
            Log.Information("--- Console Output ---\n{Output}", consoleText);

        Log.CloseAndFlush();
    }
}
