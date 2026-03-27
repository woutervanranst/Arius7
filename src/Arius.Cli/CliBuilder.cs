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
using Serilog;
using Serilog.Events;
using Spectre.Console;
using Spectre.Console.Rendering;
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
    /// <summary>
    /// Creates the "archive" subcommand that archives a local directory to Azure Blob Storage.
    /// </summary>
    /// <param name="serviceProviderFactory">Factory that produces an <see cref="IServiceProvider"/> for the resolved account name, account key, optional passphrase, and container name.</param>
    /// <summary>
    /// Constructs the "archive" CLI command that uploads a local directory to Azure Blob Storage, wiring options, validation, progress rendering, and command execution.
    /// </summary>
    /// <returns>The configured <see cref="Command"/> for the archive operation.</returns>

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
            var account     = parseResult.GetValue(accountOption);
            var key         = parseResult.GetValue(keyOption);
            var passphrase  = parseResult.GetValue(passphraseOption);
            var container   = parseResult.GetValue(containerOption)!;
            var path        = parseResult.GetValue(pathArgument)!;
            var tier        = parseResult.GetValue(tierOption);
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

            // ── Audit logging + Recorder setup ────────────────────────────────
            ConfigureAuditLogging(resolvedAccount, container, "archive");
            var recorder = AnsiConsole.Console.CreateRecorder();
            var savedConsole = AnsiConsole.Console;
            AnsiConsole.Console = recorder;

            try
            {
                var services = serviceProviderFactory(resolvedAccount, resolvedKey, passphrase, container);
                var mediator = services.GetRequiredService<IMediator>();
                var progressState = services.GetRequiredService<ProgressState>();

                var opts = new ArchiveOptions
                {
                    RootDirectory      = Path.GetFullPath(path),
                    UploadTier         = tier,
                    RemoveLocal        = removeLocal,
                    NoPointers         = noPointers,
                    SmallFileThreshold = 1024 * 1024L,
                    TarTargetSize      = 64L * 1024 * 1024,

                    // Wire byte-level progress callbacks into ProgressState (task 5.1 / 5.2).
                    // FileHashingHandler (via Mediator) has already added the TrackedFile entry
                    // to TrackedFiles before CreateHashProgress is called; just look it up.
                    CreateHashProgress = (relativePath, fileSize) =>
                    {
                        if (progressState.TrackedFiles.TryGetValue(relativePath, out var file))
                            return new Progress<long>(bytes => file.SetBytesProcessed(bytes));
                        return new Progress<long>();
                    },

                    // ChunkUploadingHandler has already transitioned the file to Uploading state.
                    // Reset BytesProcessed so the upload bar starts from 0, then stream updates.
                    CreateUploadProgress = (contentHash, size) =>
                    {
                        if (progressState.ContentHashToPath.TryGetValue(contentHash, out var relPath) &&
                            progressState.TrackedFiles.TryGetValue(relPath, out var file))
                        {
                            file.SetBytesProcessed(0);
                            return new Progress<long>(bytes => file.SetBytesProcessed(bytes));
                        }
                        return new Progress<long>();
                    },
                };

                ArchiveResult? result = null;
                if (AnsiConsole.Console.Profile.Capabilities.Interactive)
                {
                    // ── 4.1-4.2: Live display with overflow handling ──────────────────────
                    await AnsiConsole.Live(BuildArchiveDisplay(progressState))
                        .Overflow(VerticalOverflow.Crop)
                        .Cropping(VerticalOverflowCropping.Bottom)
                        .AutoClear(false)
                        .StartAsync(async ctx =>
                        {
                            // Run the archive pipeline concurrently with the display poll loop
                            var archiveTask = mediator.Send(new ArchiveCommand(opts), ct)
                                .AsTask()
                                .ContinueWith(t => { result = t.IsCompletedSuccessfully ? t.Result : null; },
                                    CancellationToken.None);

                            // ── 4.8: Responsive poll loop ─────────────────────────────────
                            while (!archiveTask.IsCompleted)
                            {
                                ctx.UpdateTarget(BuildArchiveDisplay(progressState));
                                await Task.WhenAny(archiveTask, Task.Delay(100, ct)).ConfigureAwait(false);
                            }

                            await archiveTask;
                            // ── 4.9: Final update after loop exits ────────────────────────
                            ctx.UpdateTarget(BuildArchiveDisplay(progressState));
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
            }
            finally
            {
                AnsiConsole.Console = savedConsole;
                FlushAuditLog(recorder);
            }
        });

        return cmd;
    }

    /// <summary>
    /// Creates the "restore" command that restores files from Azure Blob Storage into a local directory.
    /// </summary>
    /// <param name="serviceProviderFactory">Factory that produces an <see cref="IServiceProvider"/> for the given account name, account key, optional passphrase, and container name.</param>
    /// <summary>
    /// Creates the "restore" subcommand that restores files from an Arius Azure container into a local directory.
    /// </summary>
    /// <summary>
    /// Builds the "restore" subcommand that restores files from Azure Blob Storage into a local directory.
    /// </summary>
    /// <param name="serviceProviderFactory">
    /// Factory that creates an <see cref="IServiceProvider"/> for a given Azure account and container.
    /// Parameters: (accountName, accountKey, passphrase, containerName) => IServiceProvider.
    /// </param>
    /// <returns>The configured <see cref="Command"/> for the "restore" subcommand.</returns>

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

            // ── Audit logging + Recorder setup ────────────────────────────────
            ConfigureAuditLogging(resolvedAccount, container, "restore");
            var recorder = AnsiConsole.Console.CreateRecorder();
            var savedConsole = AnsiConsole.Console;
            AnsiConsole.Console = recorder;

            try
            {

            var services = serviceProviderFactory(resolvedAccount, resolvedKey, passphrase, container);
            var mediator = services.GetRequiredService<IMediator>();

            // ── TCS pairs for thread-safe phase coordination ──────────────────
            // The pipeline runs on a background task; its callbacks signal these TCS pairs.
            // The CLI event loop awaits them without any live Spectre component active,
            // ensuring only one thread touches the console at a time.
            var questionTcs      = new TaskCompletionSource<RestoreCostEstimate>(TaskCreationOptions.RunContinuationsAsynchronously);
            var answerTcs        = new TaskCompletionSource<RehydratePriority?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanupQuestionTcs = new TaskCompletionSource<(int count, long bytes)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanupAnswerTcs   = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var opts = new RestoreOptions
            {
                RootDirectory = Path.GetFullPath(path),
                Version       = version,
                NoPointers    = noPointers,
                Overwrite     = overwrite,

                // Callback runs on pipeline thread. Signal questionTcs and await answerTcs
                // so the CLI event loop can render prompt on clean console.
                ConfirmRehydration = async (estimate, cancellationToken) =>
                {
                    questionTcs.TrySetResult(estimate);
                    return await answerTcs.Task.ConfigureAwait(false);
                },

                // Callback runs on pipeline thread. Signal cleanupQuestionTcs and await cleanupAnswerTcs.
                ConfirmCleanup = async (count, bytes, cancellationToken) =>
                {
                    cleanupQuestionTcs.TrySetResult((count, bytes));
                    return await cleanupAnswerTcs.Task.ConfigureAwait(false);
                },
            };

            RestoreResult? result = null;
            if (AnsiConsole.Console.Profile.Capabilities.Interactive)
            {
                var restoreProgress = services.GetRequiredService<ProgressState>();

                // ── Phase 1 + 3: Download progress display ────────────────────
                // Wrap the entire download flow in a Progress display so files being
                // restored are shown in both the "no rehydration needed" and the
                // "post-confirmation download" paths.
                // When a rehydration question arrives (questionTcs fires), the display
                // loop exits cleanly so the prompt can render on a clean console.
                var pipelineTask = mediator.Send(new RestoreCommand(opts), ct)
                    .AsTask()
                    .ContinueWith(t => { result = t.IsCompletedSuccessfully ? t.Result : null; },
                        CancellationToken.None);

                await AnsiConsole.Live(new Markup(""))
                    .Overflow(VerticalOverflow.Crop)
                    .Cropping(VerticalOverflowCropping.Bottom)
                    .AutoClear(false)
                    .StartAsync(async ctx =>
                    {
                        // Poll until either pipeline completes or a rehydration question arrives.
                        while (!pipelineTask.IsCompleted && !questionTcs.Task.IsCompleted)
                        {
                            ctx.UpdateTarget(BuildRestoreDisplay(restoreProgress));
                            await Task.WhenAny(pipelineTask, questionTcs.Task, Task.Delay(100, ct)).ConfigureAwait(false);
                        }

                        // Final update before exiting.
                        ctx.UpdateTarget(BuildRestoreDisplay(restoreProgress));
                    });

                if (!pipelineTask.IsCompleted)
                {
                    // ── Phase 2: Rehydration question — render prompt on clean console ─
                    var estimate = await questionTcs.Task.ConfigureAwait(false);

                    // ── Chunk availability summary table ──────────────────────
                    var summaryTable = new Table().Title("[yellow]Rehydration Cost Estimate[/]");
                    summaryTable.AddColumn("Category");
                    summaryTable.AddColumn(new TableColumn("Chunks").RightAligned());
                    summaryTable.AddColumn(new TableColumn("Size").RightAligned());

                    summaryTable.AddRow("Available (Hot/Cool)",
                        estimate.ChunksAvailable.ToString(),
                        estimate.DownloadBytes.Bytes().Humanize());
                    summaryTable.AddRow("Already rehydrated",
                        estimate.ChunksAlreadyRehydrated.ToString(),
                        "-");
                    summaryTable.AddRow("[yellow]Needs rehydration[/]",
                        estimate.ChunksNeedingRehydration.ToString(),
                        estimate.RehydrationBytes.Bytes().Humanize());
                    summaryTable.AddRow("[dim]Rehydration pending[/]",
                        estimate.ChunksPendingRehydration.ToString(),
                        "-");
                    AnsiConsole.Write(summaryTable);

                    RehydratePriority? priority;
                    if (estimate.ChunksNeedingRehydration == 0 && estimate.ChunksPendingRehydration == 0)
                    {
                        priority = RehydratePriority.Standard;
                    }
                    else
                    {
                        // ── Per-component cost breakdown table ────────────────
                        var costTable = new Table();
                        costTable.AddColumn("Cost Component");
                        costTable.AddColumn(new TableColumn("Standard").RightAligned());
                        costTable.AddColumn(new TableColumn("High Priority").RightAligned());

                        costTable.AddRow("Data retrieval",
                            $"\u20ac {estimate.RetrievalCostStandard:F4}",
                            $"\u20ac {estimate.RetrievalCostHigh:F4}");
                        costTable.AddRow("Read operations",
                            $"\u20ac {estimate.ReadOpsCostStandard:F4}",
                            $"\u20ac {estimate.ReadOpsCostHigh:F4}");
                        costTable.AddRow("Write operations",
                            $"\u20ac {estimate.WriteOpsCost:F4}",
                            $"\u20ac {estimate.WriteOpsCost:F4}");
                        costTable.AddRow("Storage (1 month)",
                            $"\u20ac {estimate.StorageCost:F4}",
                            $"\u20ac {estimate.StorageCost:F4}");
                        costTable.AddEmptyRow();
                        costTable.AddRow("[bold]Total[/]",
                            $"[bold]\u20ac {estimate.TotalStandard:F4}[/]",
                            $"[bold]\u20ac {estimate.TotalHigh:F4}[/]");
                        AnsiConsole.Write(costTable);

                        var choice = AnsiConsole.Prompt(
                            new SelectionPrompt<string>()
                                .Title("Select rehydration priority (or cancel):")
                                .AddChoices("Standard (~15h)", "High (~1h)", "Cancel"));

                        priority = choice switch
                        {
                            "Standard (~15h)" => RehydratePriority.Standard,
                            "High (~1h)"      => RehydratePriority.High,
                            _                 => (RehydratePriority?)null,
                        };
                    }

                    // Unblock the pipeline callback with the user's answer
                    answerTcs.TrySetResult(priority);

                    if (priority is null)
                    {
                        // User cancelled; wait for pipeline to finish without showing progress
                        await pipelineTask.ConfigureAwait(false);
                    }
                    else
                    {
                        // ── Phase 3: Download — show progress display ─────────
                        await AnsiConsole.Live(new Markup(""))
                            .Overflow(VerticalOverflow.Crop)
                            .Cropping(VerticalOverflowCropping.Bottom)
                            .AutoClear(false)
                            .StartAsync(async ctx =>
                            {
                                while (!pipelineTask.IsCompleted)
                                {
                                    ctx.UpdateTarget(BuildRestoreDisplay(restoreProgress));
                                    await Task.WhenAny(pipelineTask, Task.Delay(100, ct)).ConfigureAwait(false);
                                }

                                await pipelineTask;
                                ctx.UpdateTarget(BuildRestoreDisplay(restoreProgress));
                            });

                        // ── Phase 4: Cleanup question — after progress clears ─
                        // pipelineTask is done at this point. If ConfirmCleanup was called,
                        // cleanupQuestionTcs is already set. If not, it was never set — skip.
                        if (cleanupQuestionTcs.Task.IsCompleted)
                        {
                            var (count, bytes) = await cleanupQuestionTcs.Task.ConfigureAwait(false);
                            var doCleanup = AnsiConsole.Confirm(
                                $"Delete {count} rehydrated chunk(s) ({bytes.Bytes().Humanize()}) from Azure?");
                            cleanupAnswerTcs.TrySetResult(doCleanup);
                        }
                    }
                }
                else
                {
                    // Pipeline completed without rehydration question (phase 1 progress already shown).
                    await pipelineTask.ConfigureAwait(false);
                }
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
                $"{result.FilesRestored} files restored, {result.FilesSkipped} files skipped, " +
                $"{result.ChunksPendingRehydration} files pending rehydration");

            if (result.ChunksPendingRehydration > 0)
            {
                AnsiConsole.MarkupLine($"[yellow]{result.ChunksPendingRehydration} chunk(s) are pending rehydration.[/] " +
                    "Re-run this command in ~15 hours to complete the restore.");
            }
            return 0;
            }
            finally
            {
                AnsiConsole.Console = savedConsole;
                FlushAuditLog(recorder);
            }
        });

        return cmd;
    }

    /// <summary>
    /// Creates and configures the "ls" subcommand which lists files in a snapshot.
    /// </summary>
    /// <param name="serviceProviderFactory">Factory that produces an <see cref="IServiceProvider"/> for the given account name, account key, optional passphrase, and container name.</param>
    /// <summary>
    /// Creates the "ls" command which lists files in a snapshot.
    /// </summary>
    /// <param name="serviceProviderFactory">A factory that, given account name, account key, optional passphrase, and container name, returns an <see cref="IServiceProvider"/> configured for command execution.</param>
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

            // ── Audit logging + Recorder setup ────────────────────────────────
            ConfigureAuditLogging(resolvedAccount, container, "ls");
            var recorder = AnsiConsole.Console.CreateRecorder();
            var savedConsole = AnsiConsole.Console;
            AnsiConsole.Console = recorder;

            try
            {

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
            }
            finally
            {
                AnsiConsole.Console = savedConsole;
                FlushAuditLog(recorder);
            }
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

    // ── Progress display helpers ──────────────────────────────────────────────

    /// <summary>
    /// Builds the archive display renderable as a pure function of <see cref="ProgressState"/>.
    /// Returns a <see cref="Rows"/> containing stage header lines followed by per-file lines.
    /// Called on every 100ms poll tick by the Live display loop.
    /// <summary>
    /// Builds a Spectre renderable that visualizes archive progress, including stage summaries (scanning, hashing, uploading) and a per-file progress tail.
    /// </summary>
    /// <param name="state">Current archive progress and tracked file information used to render stage counts and per-file progress bars.</param>
    /// <returns>An <see cref="IRenderable"/> that renders the archive progress display.</returns>
    internal static IRenderable BuildArchiveDisplay(ProgressState state)
    {
        var lines = new List<IRenderable>();

        // ── Stage headers ─────────────────────────────────────────────────────

        var totalFiles  = state.TotalFiles;
        var filesHashed = state.FilesHashed;

        // Scanning
        if (totalFiles.HasValue)
            lines.Add(new Markup($"  [green]●[/] Scanning   [dim]{totalFiles.Value} files[/]"));
        else
            lines.Add(new Markup("  [yellow]○[/] Scanning   [dim]...[/]"));

        // Hashing
        if (totalFiles.HasValue && filesHashed >= totalFiles.Value)
            lines.Add(new Markup($"  [green]●[/] Hashing    [dim]{filesHashed}/{totalFiles.Value}[/]"));
        else if (totalFiles.HasValue)
            lines.Add(new Markup($"  [yellow]○[/] Hashing    [dim]{filesHashed}/{totalFiles.Value}[/]"));
        else
            lines.Add(new Markup($"  [yellow]○[/] Hashing    [dim]{filesHashed}...[/]"));

        // Uploading
        var chunksUploaded = state.ChunksUploaded;
        var totalChunks    = state.TotalChunks;
        var tarsUploaded   = state.TarsUploaded;
        if (totalChunks.HasValue && chunksUploaded >= totalChunks.Value && chunksUploaded > 0)
            lines.Add(new Markup($"  [green]●[/] Uploading  [dim]{chunksUploaded}/{totalChunks.Value} chunks[/]"));
        else if (totalChunks.HasValue)
            lines.Add(new Markup($"  [yellow]○[/] Uploading  [dim]{chunksUploaded}/{totalChunks.Value} chunks[/]"));
        else if (chunksUploaded > 0 || tarsUploaded > 0)
            lines.Add(new Markup($"  [yellow]○[/] Uploading  [dim]{chunksUploaded} chunks...[/]"));
        else
            lines.Add(new Markup("  [grey]  Uploading[/]"));

        // ── Per-file lines (blank separator + one line per active TrackedFile) ─

        var trackedFiles = state.TrackedFiles.Values.ToList();
        if (trackedFiles.Count > 0)
        {
            lines.Add(new Markup(""));  // blank separator
            foreach (var file in trackedFiles)
            {
                var displayName = Markup.Escape(TruncateAndLeftJustify(file.RelativePath, 30));

                var stateStr = file.State switch
                {
                    FileState.Hashing      => "Hashing      ",
                    FileState.QueuedInTar  => "Queued in TAR",
                    FileState.UploadingTar => "Uploading TAR",
                    FileState.Uploading    => "Uploading    ",
                    FileState.Done         => "Done         ",
                    _                      => "             ",
                };

                string line;
                if (file.State is FileState.Hashing or FileState.Uploading)
                {
                    var pct = file.TotalBytes > 0
                        ? (double)file.BytesProcessed / file.TotalBytes
                        : 0.0;
                    var bar    = RenderProgressBar(pct, 12);
                    var pctStr = $"{pct * 100:F0}%".PadLeft(4);
                    var sizeStr = $"{file.BytesProcessed.Bytes().LargestWholeNumberValue:0.##} / {file.TotalBytes.Bytes().Humanize()}";
                    line = $"  [dim]{displayName}[/]  {bar}  [dim]{stateStr} {pctStr}  {Markup.Escape(sizeStr)}[/]";
                }
                else
                {
                    // QueuedInTar / UploadingTar: no progress bar, show total size only
                    var sizeStr = file.TotalBytes.Bytes().Humanize();
                    line = $"  [dim]{displayName}[/]  {"",-12}  [dim]{stateStr}  {Markup.Escape(sizeStr)}[/]";
                }
                lines.Add(new Markup(line));
            }
        }

        return new Rows(lines);
    }

    /// <summary>
    /// Renders a progress bar as a Markup string with the given fill ratio and character width.
    /// Filled characters use [green]█[/] and empty characters use [dim]░[/].
    /// </summary>
    /// <param name="fraction">Fill ratio in [0.0, 1.0].</param>
    /// <summary>
    /// Render a horizontal progress bar as a markup string using filled and empty block characters.
    /// </summary>
    /// <param name="width">Total bar width in characters.</param>
    /// <returns>
    /// A markup string of length `width` composed of green filled block characters for the completed fraction and dim empty block characters for the remainder; `fraction` values outside 0.0–1.0 are clamped to that range.
    /// </returns>
    internal static string RenderProgressBar(double fraction, int width)
    {
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        var filled = (int)Math.Round(fraction * width);
        filled = Math.Clamp(filled, 0, width);
        var empty  = width - filled;
        return $"[green]{new string('█', filled)}[/][dim]{new string('░', empty)}[/]";
    }

    /// <summary>
    /// Truncates <paramref name="input"/> to <paramref name="width"/> characters and left-justifies it.
    /// If the input is longer than <paramref name="width"/>, the result is
    /// <c>"..." + input[last (width-3) chars]</c> — preserving the deepest part of the path.
    /// The result is always exactly <paramref name="width"/> characters wide.
    /// The caller is responsible for applying <see cref="Markup.Escape"/> before embedding in Markup.
    /// <summary>
    /// Truncates the input to a fixed width and left-justifies the result.
    /// </summary>
    /// <param name="input">The string to truncate and pad.</param>
    /// <param name="width">The desired output width in characters. Must be at least 3 to allow an ellipsis when truncation occurs.</param>
    /// <returns>The formatted string exactly <paramref name="width"/> characters long: if <paramref name="input"/> is longer than <paramref name="width"/>, returns "..." followed by the last <c>width - 3</c> characters; otherwise returns <paramref name="input"/> padded on the right.</returns>
    internal static string TruncateAndLeftJustify(string input, int width)
    {
        if (input.Length <= width)
            return input.PadRight(width);
        return ("..." + input[^(width - 3)..]).PadRight(width);
    }

    /// <summary>
    /// Builds the restore display renderable as a pure function of <see cref="ProgressState"/>.
    /// Returns a <see cref="Rows"/> containing a stage header (4 lines) followed by a tail of
    /// up to 10 recent file events. When all files are done the tail is omitted.
    /// <summary>
    /// Builds a Spectre.Console renderable that visualizes restore progress and recent restore events.
    /// </summary>
    /// <param name="state">The current restore progress state containing totals, counts, byte summaries, rehydration info, and recent events.</param>
    /// <returns>An <see cref="IRenderable"/> that displays stage headers (Restored/Skipped/Rehydrating) and, while not complete, a tail of recent file restore events.</returns>
    internal static IRenderable BuildRestoreDisplay(ProgressState state)
    {
        var lines = new List<IRenderable>();

        var total    = state.RestoreTotalFiles;
        var restored = state.FilesRestored;
        var skipped  = state.FilesSkipped;
        var done     = restored + skipped;
        var complete = total > 0 && done >= total;

        // ── Stage header ──────────────────────────────────────────────────────

        var symbol = complete ? "[green]●[/]" : "[yellow]○[/]";
        var countStr = total > 0 ? $"{done}/{total}" : $"{done}...";
        lines.Add(new Markup($"  {symbol} Restoring  {countStr} files"));

        lines.Add(new Markup($"    Restored:    {restored}  ({state.BytesRestored.Bytes().Humanize()})"));
        lines.Add(new Markup($"    Skipped:     {skipped}  ({state.BytesSkipped.Bytes().Humanize()})"));

        if (state.RehydrationChunkCount > 0)
            lines.Add(new Markup($"    Rehydrating: {state.RehydrationChunkCount} chunks ({state.RehydrationTotalBytes.Bytes().Humanize()})"));

        // ── Per-file tail (omitted on completion) ─────────────────────────────

        if (!complete)
        {
            var recent = state.RecentRestoreEvents.ToArray();
            if (recent.Length > 0)
            {
                lines.Add(new Markup(""));  // blank separator
                foreach (var ev in recent)
                {
                    var sym      = ev.Skipped ? "[dim]○[/]" : "[green]●[/]";
                    var path     = Markup.Escape(TruncateAndLeftJustify(ev.RelativePath, 40));
                    var sizeStr  = Markup.Escape(ev.FileSize.Bytes().Humanize());
                    lines.Add(new Markup($"  {sym} [dim]{path}[/]  ({sizeStr})"));
                }
            }
        }

        return new Rows(lines);
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
        if (!string.IsNullOrWhiteSpace(cliAccount)) 
            return cliAccount;

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
        if (!string.IsNullOrWhiteSpace(cliKey))
            return cliKey;

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
    /// <summary>
    /// Create a service provider configured for production use with Azure Blob Storage and Arius services for the specified storage account and container.
    /// </summary>
    /// <param name="accountName">Azure Storage account name to connect to.</param>
    /// <param name="accountKey">Key for the Azure Storage account.</param>
    /// <param name="passphrase">Optional encryption passphrase; pass <c>null</c> to disable encryption.</param>
    /// <param name="containerName">Blob container name used by Arius.</param>
    /// <summary>
    /// Builds a production IServiceProvider configured for the specified Azure storage account and container.
    /// </summary>
    /// <param name="accountName">The Azure Storage account name to target.</param>
    /// <param name="accountKey">The account key used to authenticate against the storage account.</param>
    /// <param name="passphrase">Optional encryption passphrase; provide null to disable encryption-related services.</param>
    /// <param name="containerName">The blob container name to use for storage operations.</param>
    /// <returns>An <see cref="IServiceProvider"/> containing production-ready services wired to the specified storage account and container.</returns>

    private static IServiceProvider BuildProductionServices(
        string  accountName,
        string  accountKey,
        string? passphrase,
        string  containerName)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: true));
        services.AddSingleton<ProgressState>();

        // AddMediator() is called here (not in AddArius) so the source generator runs
        // in the CLI assembly and discovers INotificationHandler<T> implementations in
        // both Arius.Core and Arius.Cli.
        services.AddMediator();

        var credential          = new StorageSharedKeyCredential(accountName, accountKey);
        var serviceUri          = new Uri($"https://{accountName}.blob.core.windows.net");
        var blobServiceClient   = new BlobServiceClient(serviceUri, credential);
        var blobContainerClient = blobServiceClient.GetBlobContainerClient(containerName);
        var blobStorage         = new AzureBlobStorageService(blobContainerClient);

        services.AddArius(
            blobStorage,
            passphrase,
            accountName,
            containerName);

        return services.BuildServiceProvider();
    }

    // ── Audit logging setup ───────────────────────────────────────────────────

    /// <summary>
    /// Configures the global Serilog logger for one CLI invocation.
    /// Console sink: Warning+.  File sink: Information+.
    /// Must be called before <see cref="BuildProductionServices"/>.
    /// <summary>
    /// Initializes per-command audit logging and returns the path to the created log file.
    /// </summary>
    /// <remarks>
    /// Configures the global Serilog logger, creating a timestamped log file under the user's ~/.arius/{repo}/logs directory and restricting console output to warnings and above while recording information-level entries to the file.
    /// </remarks>
    /// <param name="accountName">Azure Storage account name used to compute the repository-specific log directory.</param>
    /// <param name="containerName">Azure Blob container name used to compute the repository-specific log directory.</param>
    /// <param name="commandName">Label for the command; included in the log file name.</param>
    /// <returns>The full path to the log file created for this command.</returns>
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
    /// Logs the captured console output (from a Spectre.Console <see cref="Recorder"/>)
    /// to the current Serilog logger, then closes and flushes the logger.
    /// <summary>
    /// Flushes captured console output from the provided Spectre.Console Recorder into the audit log and then closes the global logger.
    /// </summary>
    /// <param name="recorder">The Spectre.Console <see cref="Recorder"/> that contains captured console output to be exported and logged.</param>
    public static void FlushAuditLog(Recorder recorder)
    {
        var consoleText = recorder.ExportText();
        if (!string.IsNullOrWhiteSpace(consoleText))
            Log.Information("--- Console Output ---\n{Output}", consoleText);

        Log.CloseAndFlush();
    }
}
