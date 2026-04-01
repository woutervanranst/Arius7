using Arius.AzureBlob;
using Arius.Cli.Commands.Archive;
using Arius.Cli.Commands.Ls;
using Arius.Cli.Commands.Restore;
using Arius.Cli.Commands.Update;
using Arius.Core;
using Arius.Core.ChunkIndex;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Spectre.Console;
using System.CommandLine;

namespace Arius.Cli;

/// <summary>
/// Controls which preflight probe is executed against Azure Storage before the
/// DI container is built.  Verbs pass this to the service-provider factory.
/// </summary>
public enum PreflightMode
{
    /// <summary>
    /// Calls <c>container.ExistsAsync()</c>.  Used by restore and ls.
    /// </summary>
    ReadOnly,

    /// <summary>
    /// Uploads and deletes a probe blob.  Used by archive.
    /// </summary>
    ReadWrite,
}

/// <summary>
/// Thrown by <see cref="CliBuilder.BuildProductionServices"/> when a known
/// connectivity or auth failure is detected during the preflight check.
/// The <see cref="Exception.Message"/> is user-friendly (no stack trace);
/// the inner exception carries the original SDK exception for logging.
/// </summary>
public sealed class PreflightException : Exception
{
    public PreflightException(string userMessage, Exception? inner = null)
        : base(userMessage, inner) { }
}

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
            .AddUserSecrets<AssemblyMarker>(optional: true)
            .Build();

        return config[$"arius:{accountName}:key"] ?? config["arius:key"];
    }

    // ── Production DI ─────────────────────────────────────────────────────────

    private const string PreflightProbeBlobName = ".arius-preflight-probe";

    private static async Task<IServiceProvider> BuildProductionServices(
        string        accountName,
        string?       accountKey,
        string?       passphrase,
        string        containerName,
        PreflightMode preflightMode)
    {
        var serviceUri = new Uri($"https://{accountName}.blob.core.windows.net");

        // ── Credential resolution ─────────────────────────────────────────────
        // Key sources (flag → env var → user secrets) win over AzureCliCredential.
        BlobServiceClient blobServiceClient;
        bool usingKey;

        if (accountKey is not null)
        {
            var credential = new StorageSharedKeyCredential(accountName, accountKey);
            blobServiceClient = new BlobServiceClient(serviceUri, credential);
            usingKey = true;
        }
        else
        {
            var credential = new AzureCliCredential();
            blobServiceClient = new BlobServiceClient(serviceUri, credential);
            usingKey = false;
        }

        var blobContainerClient = blobServiceClient.GetBlobContainerClient(containerName);

        // ── Preflight check ───────────────────────────────────────────────────
        try
        {
            if (preflightMode == PreflightMode.ReadWrite)
            {
                var probeBlob = blobContainerClient.GetBlobClient(PreflightProbeBlobName);
                using var emptyStream = new MemoryStream();
                await probeBlob.UploadAsync(emptyStream, overwrite: true).ConfigureAwait(false);
                await probeBlob.DeleteAsync().ConfigureAwait(false);
            }
            else
            {
                var exists = await blobContainerClient.ExistsAsync().ConfigureAwait(false);
                if (!exists.Value)
                    throw new PreflightException(
                        $"Container '{containerName}' not found on storage account '{accountName}'.");
            }
        }
        catch (CredentialUnavailableException ex)
        {
            throw new PreflightException(
                $"No account key found and Azure CLI is not logged in.\n\n" +
                $"Provide a key via:\n" +
                $"  --key / -k\n" +
                $"  ARIUS_KEY environment variable\n" +
                $"  dotnet user-secrets\n\n" +
                $"Or log in via Azure CLI:\n" +
                $"  az login",
                ex);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new PreflightException(
                $"Container '{containerName}' not found on storage account '{accountName}'.",
                ex);
        }
        catch (RequestFailedException ex) when (ex.Status == 403)
        {
            if (usingKey)
            {
                throw new PreflightException(
                    $"Access denied. Verify the account key is correct for storage account '{accountName}'.",
                    ex);
            }
            else
            {
                var role = preflightMode == PreflightMode.ReadWrite
                    ? "Storage Blob Data Contributor"
                    : "Storage Blob Data Reader";
                throw new PreflightException(
                    $"Authenticated via Azure CLI but access was denied on storage account '{accountName}'.\n\n" +
                    $"Assign the required RBAC role:\n" +
                    $"  {role}\n\n" +
                    $"  az role assignment create --assignee <your-email> --role \"{role}\" " +
                    $"--scope /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Storage/storageAccounts/{accountName}",
                    ex);
            }
        }
        catch (RequestFailedException ex)
        {
            throw new PreflightException(
                $"Could not connect to storage account '{accountName}': {ex.Message}",
                ex);
        }
        catch (PreflightException)
        {
            throw; // don't re-wrap our own exceptions
        }

        // ── Build DI container ────────────────────────────────────────────────
        var blobStorage = new AzureBlobStorageService(blobContainerClient);

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

        const string outputTemplate = "[{Timestamp:HH:mm:ss.fff}] [{Level:u3}] [T:{ThreadId}] {Message}{NewLine}";

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithThreadId()
            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Warning)
            .WriteTo.File(logFile, outputTemplate: outputTemplate, restrictedToMinimumLevel: LogEventLevel.Information)
            .CreateLogger();

        return logFile;
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
