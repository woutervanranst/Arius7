using System.CommandLine;
using Arius.AzureBlob;
using Arius.Core.Archive;
using Arius.Core.ChunkIndex;
using Arius.Core.Encryption;
using Arius.Core.Ls;
using Arius.Core.Restore;
using Arius.Core.Storage;
using Arius.Cli;
using Azure.Storage;
using Azure.Storage.Blobs;
using Humanizer;
using Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

// ── 12.2: Common options ──────────────────────────────────────────────────────

var accountOption = new Option<string>("--account")
{
    Description = "Azure Storage account name",
    Required    = true,
};
var keyOption = new Option<string?>("--key")
{
    Description = "Azure Storage account key (or omit to use user secrets)",
};
var passphraseOption = new Option<string?>("--passphrase")
{
    Description = "Encryption passphrase (omit for no encryption)",
};
var containerOption = new Option<string>("--container")
{
    Description = "Azure Blob container name",
    Required    = true,
};

// ── 12.1: Archive verb ────────────────────────────────────────────────────────

var archiveCommand = new Command("archive", "Archive a local directory to Azure Blob Storage");
archiveCommand.Options.Add(accountOption);
archiveCommand.Options.Add(keyOption);
archiveCommand.Options.Add(passphraseOption);
archiveCommand.Options.Add(containerOption);

var pathArgument = new Argument<string>("path")
{
    Description = "Local directory to archive",
};
var tierOption = new Option<BlobTier>("--tier")
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
var smallFileThreshold = new Option<long>("--small-file-threshold")
{
    Description         = "Small file threshold in bytes",
    DefaultValueFactory = _ => 1024 * 1024L,
};
var tarTargetSize = new Option<long>("--tar-target-size")
{
    Description         = "Tar target size in bytes",
    DefaultValueFactory = _ => 64L * 1024 * 1024,
};
var dedupCacheMb = new Option<int>("--dedup-cache-mb")
{
    Description         = "LRU dedup cache size in MB",
    DefaultValueFactory = _ => 512,
};

archiveCommand.Arguments.Add(pathArgument);
archiveCommand.Options.Add(tierOption);
archiveCommand.Options.Add(removeLocalOption);
archiveCommand.Options.Add(noPointersOption);
archiveCommand.Options.Add(smallFileThreshold);
archiveCommand.Options.Add(tarTargetSize);
archiveCommand.Options.Add(dedupCacheMb);

archiveCommand.SetAction(async (parseResult, ct) =>
{
    var account     = parseResult.GetValue(accountOption)!;
    var key         = parseResult.GetValue(keyOption);
    var passphrase  = parseResult.GetValue(passphraseOption);
    var container   = parseResult.GetValue(containerOption)!;
    var path        = parseResult.GetValue(pathArgument)!;
    var tier        = parseResult.GetValue(tierOption);
    var removeLocal = parseResult.GetValue(removeLocalOption);
    var noPointers  = parseResult.GetValue(noPointersOption);
    var threshold   = parseResult.GetValue(smallFileThreshold);
    var tarSize     = parseResult.GetValue(tarTargetSize);
    var cacheMb     = parseResult.GetValue(dedupCacheMb);

    // 12.6: Reject --remove-local + --no-pointers
    if (removeLocal && noPointers)
    {
        AnsiConsole.MarkupLine("[red]Error:[/] --remove-local cannot be combined with --no-pointers");
        return 1;
    }

    // 12.7: Resolve account key
    var resolvedKey = ResolveAccountKey(key, account);
    if (resolvedKey is null)
    {
        AnsiConsole.MarkupLine("[red]Error:[/] No account key provided. Use --key or configure user secrets.");
        return 1;
    }

    // 12.11: DI setup
    var services = BuildServices(account, resolvedKey, passphrase, container, cacheMb * 1024L * 1024L);
    var mediator = services.GetRequiredService<IMediator>();

    var opts = new ArchiveOptions
    {
        RootDirectory      = Path.GetFullPath(path),
        UploadTier         = tier,
        RemoveLocal        = removeLocal,
        NoPointers         = noPointers,
        SmallFileThreshold = threshold,
        TarTargetSize      = tarSize,
    };

    // 12.8: Live progress display
    ArchiveResult? result = null;
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

// ── 12.1: Restore verb ────────────────────────────────────────────────────────

var restoreCommand = new Command("restore", "Restore files from Azure Blob Storage to a local directory");
restoreCommand.Options.Add(accountOption);
restoreCommand.Options.Add(keyOption);
restoreCommand.Options.Add(passphraseOption);
restoreCommand.Options.Add(containerOption);

var restorePathArg = new Argument<string>("path")
{
    Description = "Local directory to restore into",
};
var versionOption = new Option<string?>("-v")
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

restoreCommand.Arguments.Add(restorePathArg);
restoreCommand.Options.Add(versionOption);
restoreCommand.Options.Add(noPointersRestore);
restoreCommand.Options.Add(overwriteOption);

restoreCommand.SetAction(async (parseResult, ct) =>
{
    var account    = parseResult.GetValue(accountOption)!;
    var key        = parseResult.GetValue(keyOption);
    var passphrase = parseResult.GetValue(passphraseOption);
    var container  = parseResult.GetValue(containerOption)!;
    var path       = parseResult.GetValue(restorePathArg)!;
    var version    = parseResult.GetValue(versionOption);
    var noPointers = parseResult.GetValue(noPointersRestore);
    var overwrite  = parseResult.GetValue(overwriteOption);

    var resolvedKey = ResolveAccountKey(key, account);
    if (resolvedKey is null)
    {
        AnsiConsole.MarkupLine("[red]Error:[/] No account key provided. Use --key or configure user secrets.");
        return 1;
    }

    var services = BuildServices(account, resolvedKey, passphrase, container);
    var mediator = services.GetRequiredService<IMediator>();

    var opts = new RestoreOptions
    {
        RootDirectory = Path.GetFullPath(path),
        Version       = version,
        NoPointers    = noPointers,
        Overwrite     = overwrite,
    };

    // 12.9: Progress display
    RestoreResult? result = null;
    await AnsiConsole.Status()
        .StartAsync("Restoring...", async ctx =>
        {
            ctx.Spinner(Spinner.Known.Dots);
            result = await mediator.Send(new RestoreCommand(opts), ct);
        });

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

// ── 12.1: Ls verb ─────────────────────────────────────────────────────────────

var lsCommand = new Command("ls", "List files in a snapshot");
lsCommand.Options.Add(accountOption);
lsCommand.Options.Add(keyOption);
lsCommand.Options.Add(passphraseOption);
lsCommand.Options.Add(containerOption);

var lsVersionOption = new Option<string?>("-v")
{
    Description = "Snapshot version (partial timestamp, default latest)",
};
var prefixOption = new Option<string?>("--prefix")
{
    Description = "Path prefix filter",
};
var filterOption = new Option<string?>("--filter")
{
    Description = "Filename substring filter (case-insensitive)",
};

lsCommand.Options.Add(lsVersionOption);
lsCommand.Options.Add(prefixOption);
lsCommand.Options.Add(filterOption);

lsCommand.SetAction(async (parseResult, ct) =>
{
    var account    = parseResult.GetValue(accountOption)!;
    var key        = parseResult.GetValue(keyOption);
    var passphrase = parseResult.GetValue(passphraseOption);
    var container  = parseResult.GetValue(containerOption)!;
    var version    = parseResult.GetValue(lsVersionOption);
    var prefix     = parseResult.GetValue(prefixOption);
    var filter     = parseResult.GetValue(filterOption);

    var resolvedKey = ResolveAccountKey(key, account);
    if (resolvedKey is null)
    {
        AnsiConsole.MarkupLine("[red]Error:[/] No account key provided. Use --key or configure user secrets.");
        return 1;
    }

    var services = BuildServices(account, resolvedKey, passphrase, container);
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

    // 12.10: Table output
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

// ── Update verb ───────────────────────────────────────────────────────────────

var updateCommand = new Command("update", "Check for updates and apply them");

updateCommand.SetAction(async (parseResult, ct) =>
{
    const string repoOwner = "woutervanranst";
    const string repoName  = "Arius7";

    try
    {
        var currentVersion = typeof(Arius.Cli.AssemblyMarker).Assembly
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

        // Determine platform asset name
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        string assetName;
        if (rid.Contains("win"))       assetName = "arius-win-x64.exe";
        else if (rid.Contains("osx"))  assetName = "arius-osx-arm64";
        else                           assetName = "arius-linux-x64";

        // Find the download URL
        var assetKey = $"\"name\":\"{assetName}\"";
        var assetIdx = json.IndexOf(assetKey, StringComparison.Ordinal);
        if (assetIdx < 0)
        {
            AnsiConsole.MarkupLine($"[red]Asset '{assetName}' not found in release.[/]");
            return 1;
        }

        // Find browser_download_url near this asset
        var urlKey = "\"browser_download_url\":\"";
        var urlIdx = json.IndexOf(urlKey, assetIdx, StringComparison.Ordinal);
        if (urlIdx < 0)
        {
            AnsiConsole.MarkupLine("[red]Could not find download URL.[/]");
            return 1;
        }
        urlIdx += urlKey.Length;
        var urlEnd     = json.IndexOf('"', urlIdx);
        var downloadUrl = json[urlIdx..urlEnd];

        // Download
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
                var buffer  = new byte[81920];
                long downloaded = 0;
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (totalBytes > 0) task.Value = downloaded;
                }
                task.Value = task.MaxValue;
            });

        // Replace the current binary
        var currentExe = Environment.ProcessPath!;
        File.Move(tempFile, currentExe, true);

        // Make executable on Unix
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(currentExe,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        // Cleanup
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

// ── Root command ──────────────────────────────────────────────────────────────

var rootCommand = new RootCommand("Arius — content-addressable archival to Azure Blob Storage");
rootCommand.Subcommands.Add(archiveCommand);
rootCommand.Subcommands.Add(restoreCommand);
rootCommand.Subcommands.Add(lsCommand);
rootCommand.Subcommands.Add(updateCommand);

return await rootCommand.Parse(args).InvokeAsync();

// ── Helper: 12.7 Resolve account key ─────────────────────────────────────────

static string? ResolveAccountKey(string? cliKey, string accountName)
{
    if (!string.IsNullOrWhiteSpace(cliKey)) return cliKey;

    var config = new ConfigurationBuilder()
        .AddUserSecrets<Arius.Cli.AssemblyMarker>(optional: true)
        .Build();

    return config[$"arius:{accountName}:key"] ?? config["arius:key"];
}

// ── Helper: 12.11 Build DI services ──────────────────────────────────────────

static IServiceProvider BuildServices(
    string  accountName,
    string  accountKey,
    string? passphrase,
    string  containerName,
    long    cacheBudgetBytes = ChunkIndexService.DefaultCacheBudgetBytes)
{
    var services = new ServiceCollection();

    services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
    services.AddMediator();

    // Azure Blob Storage
    var credential       = new StorageSharedKeyCredential(accountName, accountKey);
    var serviceUri       = new Uri($"https://{accountName}.blob.core.windows.net");
    var blobServiceClient   = new BlobServiceClient(serviceUri, credential);
    var blobContainerClient = blobServiceClient.GetBlobContainerClient(containerName);
    services.AddSingleton<IBlobStorageService>(
        new AzureBlobStorageService(blobContainerClient));

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

    // Handlers
    services.AddTransient(sp =>
        new ArchivePipelineHandler(
            sp.GetRequiredService<IBlobStorageService>(),
            sp.GetRequiredService<IEncryptionService>(),
            sp.GetRequiredService<ChunkIndexService>(),
            sp.GetRequiredService<IMediator>(),
            sp.GetRequiredService<ILogger<ArchivePipelineHandler>>(),
            accountName,
            containerName));

    services.AddTransient(sp =>
        new RestorePipelineHandler(
            sp.GetRequiredService<IBlobStorageService>(),
            sp.GetRequiredService<IEncryptionService>(),
            sp.GetRequiredService<ChunkIndexService>(),
            sp.GetRequiredService<IMediator>(),
            sp.GetRequiredService<ILogger<RestorePipelineHandler>>(),
            accountName,
            containerName));

    services.AddTransient(sp =>
        new LsHandler(
            sp.GetRequiredService<IBlobStorageService>(),
            sp.GetRequiredService<IEncryptionService>(),
            sp.GetRequiredService<ChunkIndexService>(),
            sp.GetRequiredService<ILogger<LsHandler>>(),
            accountName,
            containerName));

    return services.BuildServiceProvider();
}
