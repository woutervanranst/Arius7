using Arius.AzureBlob;
using Arius.Core;
using Arius.Core.Archive;
using Arius.Core.ChunkIndex;
using Arius.Core.Ls;
using Arius.Core.Restore;
using Arius.Core.Storage;
using Azure.Storage;
using Azure.Storage.Blobs;
using Humanizer;
using Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    /// <returns>An Option&lt;string?&gt; configured for the account name with description "Azure Storage account name".</returns>

    public static Option<string?> AccountOption() => new("--account", "-a")
    {
        Description = "Azure Storage account name",
    };

    /// <summary>
    /// Creates a command-line option for specifying the Azure Storage account key.
    /// </summary>
    /// <returns>An <see cref="Option{T}"/> configured as <c>--key</c> / <c>-k</c> that yields the account key string or <c>null</c> when not provided.</returns>
    public static Option<string?> KeyOption() => new Option<string?>("--key", "-k")
    {
        Description = "Azure Storage account key",
    };

    /// <summary>
    /// Creates the --passphrase (-p) option used to provide an optional encryption passphrase.
    /// </summary>
    /// <returns>An Option&lt;string?&gt; that accepts an encryption passphrase; a null value indicates no passphrase (no encryption).</returns>
    public static Option<string?> PassphraseOption() => new Option<string?>("--passphrase", "-p")
    {
        Description = "Encryption passphrase (omit for no encryption)",
    };

    /// <summary>
    /// Creates the required CLI option for specifying the Azure Blob container name (`--container` / `-c`).
    /// </summary>
    /// <returns>An Option&lt;string&gt; configured for the required container name.</returns>
    public static Option<string> ContainerOption() => new Option<string>("--container", "-c")
    {
        Description = "Azure Blob container name",
        Required    = true,
    };

    // ── Root command ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the root command. Optionally accepts a service-provider factory so tests
    /// can inject mock handlers instead of real Azure-backed ones.
    /// <summary>
    /// Create the top-level CLI root command with all built-in subcommands and dependency-injection wiring.
    /// </summary>
    /// <param name="serviceProviderFactory">Optional factory to create an <see cref="IServiceProvider"/> for the given account name, account key, optional passphrase, and container name; if null, production services are used.</param>
    /// <returns>The configured <see cref="RootCommand"/> containing the archive, restore, ls, and update subcommands.</returns>
    public static RootCommand BuildRootCommand(
        Func<string, string, string?, string, IServiceProvider>? serviceProviderFactory = null)
    {
        serviceProviderFactory ??= BuildProductionServices;

        var rootCommand = new RootCommand("Arius — content-addressable archival to Azure Blob Storage");
        rootCommand.Subcommands.Add(BuildArchiveCommand(serviceProviderFactory));
        rootCommand.Subcommands.Add(BuildRestoreCommand(serviceProviderFactory));
        rootCommand.Subcommands.Add(BuildLsCommand(serviceProviderFactory));
        rootCommand.Subcommands.Add(BuildUpdateCommand());
        return rootCommand;
    }

    /// <summary>
    /// Builds the "archive" subcommand that uploads a local directory to Azure Blob Storage.
    /// </summary>
    /// <param name="serviceProviderFactory">Factory that creates an <see cref="IServiceProvider"/> given account name, account key, optional passphrase, and container name.</param>
    /// <returns>A configured <see cref="Command"/> for the archive operation.</returns>

    private static Command BuildArchiveCommand(
        Func<string, string, string?, string, IServiceProvider> serviceProviderFactory)
    {
        var accountOption    = AccountOption();
        var keyOption        = KeyOption();
        var passphraseOption = PassphraseOption();
        var containerOption  = ContainerOption();

        var pathArgument = new Argument<string>("path")
        {
            Description = "Local directory to archive",
        };
        var tierOption = new Option<BlobTier>("--tier", "-t")
        {
            Description         = "Upload tier (Hot/Cool/Cold/Archive)",
            DefaultValueFactory = _ => BlobTier.Archive,
        };
        var removeLocalOption = new Option<bool>("--remove-local")
        {
            Description = "Delete local binaries after snapshot",
        };
        var noPointersOption = new Option<bool>("--no-pointers")
        {
            Description = "Skip pointer file creation",
        };

        var cmd = new Command("archive", "Archive a local directory to Azure Blob Storage");
        cmd.Options.Add(accountOption);
        cmd.Options.Add(keyOption);
        cmd.Options.Add(passphraseOption);
        cmd.Options.Add(containerOption);
        cmd.Arguments.Add(pathArgument);
        cmd.Options.Add(tierOption);
        cmd.Options.Add(removeLocalOption);
        cmd.Options.Add(noPointersOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var account    = parseResult.GetValue(accountOption);
            var key        = parseResult.GetValue(keyOption);
            var passphrase = parseResult.GetValue(passphraseOption);
            var container  = parseResult.GetValue(containerOption)!;
            var path       = parseResult.GetValue(pathArgument)!;
            var tier       = parseResult.GetValue(tierOption);
            var removeLocal = parseResult.GetValue(removeLocalOption);
            var noPointers  = parseResult.GetValue(noPointersOption);

            // Reject --remove-local + --no-pointers
            if (removeLocal && noPointers)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] --remove-local cannot be combined with --no-pointers");
                return 1;
            }

            var resolvedAccount = ResolveAccount(account);
            if (resolvedAccount is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No account provided. Use --account / -a or set ARIUS_ACCOUNT.");
                return 1;
            }

            var resolvedKey = ResolveKey(key, resolvedAccount);
            if (resolvedKey is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No account key provided. Use --key / -k or set ARIUS_KEY.");
                return 1;
            }

            var services = serviceProviderFactory(resolvedAccount, resolvedKey, passphrase, container);
            var mediator = services.GetRequiredService<IMediator>();

            var opts = new ArchiveOptions
            {
                RootDirectory      = Path.GetFullPath(path),
                UploadTier         = tier,
                RemoveLocal        = removeLocal,
                NoPointers         = noPointers,
                SmallFileThreshold = 1024 * 1024L,
                TarTargetSize      = 64L * 1024 * 1024,
            };

            ArchiveResult? result = null;
            if (AnsiConsole.Console.Profile.Capabilities.Interactive)
            {
                await AnsiConsole.Progress()
                    .AutoClear(false)
                    .Columns(
                        new TaskDescriptionColumn(),
                        new ProgressBarColumn(),
                        new PercentageColumn(),
                        new SpinnerColumn())
                    .StartAsync(async ctx =>
                    {
                        var overallTask = ctx.AddTask("[green]Archiving[/]");
                        overallTask.IsIndeterminate = true;
                        result = await mediator.Send(new ArchiveCommand(opts), ct);
                        overallTask.Value = overallTask.MaxValue;
                    });
            }
            else
            {
                result = await mediator.Send(new ArchiveCommand(opts), ct);
            }

            if (result is null || !result.Success)
            {
                AnsiConsole.MarkupLine($"[red]Archive failed:[/] {result?.ErrorMessage}");
                return 1;
            }

            AnsiConsole.MarkupLine($"[green]Archive complete.[/] " +
                $"Scanned: {result.FilesScanned}, " +
                $"Uploaded: {result.FilesUploaded}, " +
                $"Deduped: {result.FilesDeduped}, " +
                $"Size: {result.TotalSize.Bytes().Humanize()}, " +
                $"Snapshot: {result.SnapshotTime:yyyy-MM-ddTHHmmss.fffZ}");
            return 0;
        });

        return cmd;
    }

    /// <summary>
    /// Creates the "restore" command that restores files from Azure Blob Storage into a local directory.
    /// </summary>
    /// <param name="serviceProviderFactory">Factory that produces an <see cref="IServiceProvider"/> for the given account name, account key, optional passphrase, and container name.</param>
    /// <returns>A configured <see cref="Command"/> representing the "restore" subcommand.</returns>

    private static Command BuildRestoreCommand(
        Func<string, string, string?, string, IServiceProvider> serviceProviderFactory)
    {
        var accountOption    = AccountOption();
        var keyOption        = KeyOption();
        var passphraseOption = PassphraseOption();
        var containerOption  = ContainerOption();

        var restorePathArg = new Argument<string>("path")
        {
            Description = "Local directory to restore into",
        };
        var versionOption = new Option<string?>("-v", "--version")
        {
            Description = "Snapshot version (partial timestamp, default latest)",
        };
        var noPointersRestore = new Option<bool>("--no-pointers")
        {
            Description = "Skip pointer file creation during restore",
        };
        var overwriteOption = new Option<bool>("--overwrite")
        {
            Description = "Overwrite local files without prompting",
        };

        var cmd = new Command("restore", "Restore files from Azure Blob Storage to a local directory");
        cmd.Options.Add(accountOption);
        cmd.Options.Add(keyOption);
        cmd.Options.Add(passphraseOption);
        cmd.Options.Add(containerOption);
        cmd.Arguments.Add(restorePathArg);
        cmd.Options.Add(versionOption);
        cmd.Options.Add(noPointersRestore);
        cmd.Options.Add(overwriteOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var account    = parseResult.GetValue(accountOption);
            var key        = parseResult.GetValue(keyOption);
            var passphrase = parseResult.GetValue(passphraseOption);
            var container  = parseResult.GetValue(containerOption)!;
            var path       = parseResult.GetValue(restorePathArg)!;
            var version    = parseResult.GetValue(versionOption);
            var noPointers = parseResult.GetValue(noPointersRestore);
            var overwrite  = parseResult.GetValue(overwriteOption);

            var resolvedAccount = ResolveAccount(account);
            if (resolvedAccount is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No account provided. Use --account / -a or set ARIUS_ACCOUNT.");
                return 1;
            }

            var resolvedKey = ResolveKey(key, resolvedAccount);
            if (resolvedKey is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No account key provided. Use --key / -k or set ARIUS_KEY.");
                return 1;
            }

            var services = serviceProviderFactory(resolvedAccount, resolvedKey, passphrase, container);
            var mediator = services.GetRequiredService<IMediator>();

            var opts = new RestoreOptions
            {
                RootDirectory = Path.GetFullPath(path),
                Version       = version,
                NoPointers    = noPointers,
                Overwrite     = overwrite,

                ConfirmRehydration = async (estimate, ct) =>
                {
                    var table = new Table().Title("[yellow]Rehydration Cost Estimate[/]");
                    table.AddColumn("Category");
                    table.AddColumn(new TableColumn("Chunks").RightAligned());
                    table.AddColumn(new TableColumn("Size").RightAligned());

                    table.AddRow("Available (Hot/Cool)",
                        estimate.ChunksAvailable.ToString(),
                        estimate.DownloadBytes.Bytes().Humanize());
                    table.AddRow("Already rehydrated",
                        estimate.ChunksAlreadyRehydrated.ToString(),
                        "-");
                    table.AddRow("[yellow]Needs rehydration[/]",
                        estimate.ChunksNeedingRehydration.ToString(),
                        estimate.RehydrationBytes.Bytes().Humanize());
                    table.AddRow("[dim]Rehydration pending[/]",
                        estimate.ChunksPendingRehydration.ToString(),
                        "-");
                    AnsiConsole.Write(table);

                    if (estimate.ChunksNeedingRehydration == 0 && estimate.ChunksPendingRehydration == 0)
                        return RehydratePriority.Standard;

                    AnsiConsole.MarkupLine($"Estimated rehydration cost: " +
                        $"[cyan]Standard ${estimate.EstimatedCostStandardUsd:F4}[/] / " +
                        $"[yellow]High ${estimate.EstimatedCostHighUsd:F4}[/]");

                    var choice = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Select rehydration priority (or cancel):")
                            .AddChoices("Standard (~15h)", "High (~1h)", "Cancel"));

                    return choice switch
                    {
                        "Standard (~15h)" => RehydratePriority.Standard,
                        "High (~1h)"      => RehydratePriority.High,
                        _                 => (RehydratePriority?)null,
                    };
                },

                ConfirmCleanup = async (count, bytes, ct) =>
                {
                    return AnsiConsole.Confirm(
                        $"Delete {count} rehydrated chunk(s) ({bytes.Bytes().Humanize()}) from Azure?");
                },
            };

            RestoreResult? result = null;
            if (AnsiConsole.Console.Profile.Capabilities.Interactive)
            {
                await AnsiConsole.Status()
                    .StartAsync("Restoring...", async ctx =>
                    {
                        ctx.Spinner(Spinner.Known.Dots);
                        result = await mediator.Send(new RestoreCommand(opts), ct);
                    });
            }
            else
            {
                result = await mediator.Send(new RestoreCommand(opts), ct);
            }

            if (result is null || !result.Success)
            {
                AnsiConsole.MarkupLine($"[red]Restore failed:[/] {result?.ErrorMessage}");
                return 1;
            }

            AnsiConsole.MarkupLine($"[green]Restore complete.[/] " +
                $"Restored: {result.FilesRestored}, Skipped: {result.FilesSkipped}");

            if (result.ChunksPendingRehydration > 0)
            {
                AnsiConsole.MarkupLine($"[yellow]{result.ChunksPendingRehydration} chunk(s) are pending rehydration.[/] " +
                    "Re-run this command in ~15 hours to complete the restore.");
            }
            return 0;
        });

        return cmd;
    }

    /// <summary>
    /// Creates and configures the "ls" subcommand which lists files in a snapshot.
    /// </summary>
    /// <param name="serviceProviderFactory">Factory that produces an <see cref="IServiceProvider"/> for the given account name, account key, optional passphrase, and container name.</param>
    /// <returns>The configured <see cref="Command"/> for the "ls" verb.</returns>

    private static Command BuildLsCommand(
        Func<string, string, string?, string, IServiceProvider> serviceProviderFactory)
    {
        var accountOption    = AccountOption();
        var keyOption        = KeyOption();
        var passphraseOption = PassphraseOption();
        var containerOption  = ContainerOption();

        var lsVersionOption = new Option<string?>("-v", "--version")
        {
            Description = "Snapshot version (partial timestamp, default latest)",
        };
        var prefixOption = new Option<string?>("--prefix")
        {
            Description = "Path prefix filter",
        };
        var filterOption = new Option<string?>("--filter", "-f")
        {
            Description = "Filename substring filter (case-insensitive)",
        };

        var cmd = new Command("ls", "List files in a snapshot");
        cmd.Options.Add(accountOption);
        cmd.Options.Add(keyOption);
        cmd.Options.Add(passphraseOption);
        cmd.Options.Add(containerOption);
        cmd.Options.Add(lsVersionOption);
        cmd.Options.Add(prefixOption);
        cmd.Options.Add(filterOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var account    = parseResult.GetValue(accountOption);
            var key        = parseResult.GetValue(keyOption);
            var passphrase = parseResult.GetValue(passphraseOption);
            var container  = parseResult.GetValue(containerOption)!;
            var version    = parseResult.GetValue(lsVersionOption);
            var prefix     = parseResult.GetValue(prefixOption);
            var filter     = parseResult.GetValue(filterOption);

            var resolvedAccount = ResolveAccount(account);
            if (resolvedAccount is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No account provided. Use --account / -a or set ARIUS_ACCOUNT.");
                return 1;
            }

            var resolvedKey = ResolveKey(key, resolvedAccount);
            if (resolvedKey is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No account key provided. Use --key / -k or set ARIUS_KEY.");
                return 1;
            }

            var services = serviceProviderFactory(resolvedAccount, resolvedKey, passphrase, container);
            var mediator = services.GetRequiredService<IMediator>();

            var opts = new LsOptions
            {
                Version = version,
                Prefix  = prefix,
                Filter  = filter,
            };

            var result = await mediator.Send(new LsCommand(opts), ct);

            if (!result.Success)
            {
                AnsiConsole.MarkupLine($"[red]Ls failed:[/] {result.ErrorMessage}");
                return 1;
            }

            var table = new Table();
            table.AddColumn("Path");
            table.AddColumn(new TableColumn("Size").RightAligned());
            table.AddColumn("Created");
            table.AddColumn("Modified");

            foreach (var entry in result.Entries)
            {
                var size = entry.OriginalSize.HasValue
                    ? entry.OriginalSize.Value.Bytes().Humanize()
                    : "?";
                table.AddRow(
                    Markup.Escape(entry.RelativePath),
                    size,
                    entry.Created.ToString("yyyy-MM-dd HH:mm"),
                    entry.Modified.ToString("yyyy-MM-dd HH:mm"));
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[dim]{result.Entries.Count} file(s)[/]");
            return 0;
        });

        return cmd;
    }

    /// <summary>
    /// Builds the "update" command that checks GitHub for a newer release and, if available, downloads the appropriate platform asset and replaces the running executable.
    /// </summary>
    /// <returns>Process exit code: `0` on success, `1` on failure.</returns>

    private static Command BuildUpdateCommand()
    {
        var cmd = new Command("update", "Check for updates and apply them");

        cmd.SetAction(async (parseResult, ct) =>
        {
            const string repoOwner = "woutervanranst";
            const string repoName  = "Arius7";

            try
            {
                var currentVersion = typeof(AssemblyMarker).Assembly
                    .GetName().Version ?? new Version(0, 0, 0);

                AnsiConsole.MarkupLine($"[dim]Current version: {currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}[/]");
                AnsiConsole.MarkupLine("[dim]Checking for updates...[/]");

                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Arius-CLI");

                var json = await http.GetStringAsync(
                    $"https://api.github.com/repos/{repoOwner}/{repoName}/releases/latest", ct);

                var tagStart = json.IndexOf("\"tag_name\":\"", StringComparison.Ordinal);
                if (tagStart < 0)
                {
                    AnsiConsole.MarkupLine("[red]Could not determine latest version.[/]");
                    return 1;
                }
                tagStart += "\"tag_name\":\"".Length;
                var tagEnd     = json.IndexOf('"', tagStart);
                var tag        = json[tagStart..tagEnd];
                var versionStr = tag.TrimStart('v');

                if (!Version.TryParse(versionStr, out var latestVersion))
                {
                    AnsiConsole.MarkupLine($"[red]Could not parse version from tag '{tag}'.[/]");
                    return 1;
                }

                if (latestVersion <= currentVersion)
                {
                    AnsiConsole.MarkupLine("[green]You are running the latest version.[/]");
                    return 0;
                }

                AnsiConsole.MarkupLine($"[blue]New version available: {versionStr}[/]");

                var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
                string assetName;
                if (rid.Contains("win"))       assetName = "arius-win-x64.exe";
                else if (rid.Contains("osx"))  assetName = "arius-osx-arm64";
                else                           assetName = "arius-linux-x64";

                var assetKey = $"\"name\":\"{assetName}\"";
                var assetIdx = json.IndexOf(assetKey, StringComparison.Ordinal);
                if (assetIdx < 0)
                {
                    AnsiConsole.MarkupLine($"[red]Asset '{assetName}' not found in release.[/]");
                    return 1;
                }

                var urlKey = "\"browser_download_url\":\"";
                var urlIdx = json.IndexOf(urlKey, assetIdx, StringComparison.Ordinal);
                if (urlIdx < 0)
                {
                    AnsiConsole.MarkupLine("[red]Could not find download URL.[/]");
                    return 1;
                }
                urlIdx += urlKey.Length;
                var urlEnd      = json.IndexOf('"', urlIdx);
                var downloadUrl = json[urlIdx..urlEnd];

                var tempDir  = Path.Combine(Path.GetTempPath(), $"arius-update-{versionStr}");
                var tempFile = Path.Combine(tempDir, assetName);
                Directory.CreateDirectory(tempDir);

                await AnsiConsole.Progress()
                    .AutoClear(false)
                    .Columns(
                        new TaskDescriptionColumn(),
                        new ProgressBarColumn(),
                        new PercentageColumn(),
                        new SpinnerColumn())
                    .StartAsync(async ctx =>
                    {
                        var task = ctx.AddTask("[green]Downloading update[/]");
                        using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                        response.EnsureSuccessStatusCode();
                        var totalBytes = response.Content.Headers.ContentLength ?? -1;
                        if (totalBytes > 0) task.MaxValue = totalBytes;

                        await using var stream = await response.Content.ReadAsStreamAsync(ct);
                        await using var file   = File.Create(tempFile);
                        var buffer     = new byte[81920];
                        long downloaded = 0;
                        int  bytesRead;
                        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
                        {
                            await file.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                            downloaded += bytesRead;
                            if (totalBytes > 0) task.Value = downloaded;
                        }
                        task.Value = task.MaxValue;
                    });

                var currentExe = Environment.ProcessPath!;
                File.Move(tempFile, currentExe, true);

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(currentExe,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }

                try { Directory.Delete(tempDir, true); } catch { }

                AnsiConsole.MarkupLine($"[green]Updated to {versionStr}. Please restart arius.[/]");
                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Update failed:[/] {ex.Message}");
                return 1;
            }
        });

        return cmd;
    }

    // ── Resolution helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Resolves account name: CLI flag > ARIUS_ACCOUNT env var.
    /// <summary>
    /// Determine the Azure Storage account name from the provided CLI value or the ARIUS_ACCOUNT environment variable.
    /// </summary>
    /// <param name="cliAccount">Account name supplied via the CLI; used when not null or whitespace.</param>
    /// <returns>`cliAccount` if not null or whitespace; otherwise the value of the `ARIUS_ACCOUNT` environment variable, or `null` if neither is present.</returns>
    public static string? ResolveAccount(string? cliAccount)
    {
        if (!string.IsNullOrWhiteSpace(cliAccount)) return cliAccount;
        var env = Environment.GetEnvironmentVariable("ARIUS_ACCOUNT");
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }

    /// <summary>
    /// Resolves account key: CLI flag > ARIUS_KEY env var > user secrets.
    /// <summary>
    /// Resolve the Azure Storage account key from the provided CLI value, environment, or user secrets.
    /// </summary>
    /// <param name="cliKey">The key provided via CLI option, if any.</param>
    /// <param name="accountName">The account name used to look up a per-account user secret.</param>
    /// <returns>`cliKey` if non-empty; otherwise the `ARIUS_KEY` environment variable; otherwise the user secret `arius:{accountName}:key` or `arius:key`; or `null` if no key is found.</returns>
    public static string? ResolveKey(string? cliKey, string accountName)
    {
        if (!string.IsNullOrWhiteSpace(cliKey)) return cliKey;

        var env = Environment.GetEnvironmentVariable("ARIUS_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        var config = new ConfigurationBuilder()
            .AddUserSecrets<AssemblyMarker>(optional: true)
            .Build();

        return config[$"arius:{accountName}:key"] ?? config["arius:key"];
    }

    /// <summary>
    /// Builds a production <see cref="IServiceProvider"/> configured with Azure Blob Storage clients and Arius services for the specified account and container.
    /// </summary>
    /// <param name="accountName">Azure Storage account name used to authenticate and construct storage clients.</param>
    /// <param name="accountKey">Azure Storage account key used for authentication.</param>
    /// <param name="passphrase">Optional encryption passphrase; pass <c>null</c> to disable encryption-related configuration.</param>
    /// <param name="containerName">Name of the blob container the services will target.</param>
    /// <returns>An <see cref="IServiceProvider"/> containing production-ready services wired to the specified storage account and container.</returns>

    private static IServiceProvider BuildProductionServices(
        string  accountName,
        string  accountKey,
        string? passphrase,
        string  containerName)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));

        var credential          = new StorageSharedKeyCredential(accountName, accountKey);
        var serviceUri          = new Uri($"https://{accountName}.blob.core.windows.net");
        var blobServiceClient   = new BlobServiceClient(serviceUri, credential);
        var blobContainerClient = blobServiceClient.GetBlobContainerClient(containerName);
        var blobStorage         = new AzureBlobStorageService(blobContainerClient);

        services.AddArius(
            blobStorage,
            passphrase,
            accountName,
            containerName,
            ChunkIndexService.DefaultCacheBudgetBytes);

        return services.BuildServiceProvider();
    }
}
