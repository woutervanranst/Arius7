using System.CommandLine;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;
using Humanizer;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Arius.Cli.Commands.Restore;

internal static class RestoreVerb
{
    internal static Command Build(
        Func<string, string?, string?, string, PreflightMode, Task<IServiceProvider>> serviceProviderFactory)
    {
        var accountOption    = CliBuilder.AccountOption();
        var keyOption        = CliBuilder.KeyOption();
        var passphraseOption = CliBuilder.PassphraseOption();
        var containerOption  = CliBuilder.ContainerOption();

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

            var resolvedAccount = CliBuilder.ResolveAccount(account);
            if (resolvedAccount is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No account provided. Use --account / -a or set ARIUS_ACCOUNT.");
                return 1;
            }

            var resolvedKey = CliBuilder.ResolveKey(key, resolvedAccount);

            CliBuilder.ConfigureAuditLogging(resolvedAccount, container, "restore");
            var recorder = AnsiConsole.Console.CreateRecorder();
            var savedConsole = AnsiConsole.Console;
            AnsiConsole.Console = recorder;

            try
            {
                IServiceProvider services;
                try
                {
                    services = await serviceProviderFactory(resolvedAccount, resolvedKey, passphrase, container, PreflightMode.ReadOnly).ConfigureAwait(false);
                }
                catch (PreflightException ex)
                {
                    Log.Error(ex, "Preflight check failed");
                    var msg = ex.ErrorKind switch
                    {
                        PreflightErrorKind.ContainerNotFound =>
                            $"Container [bold]{Markup.Escape(ex.ContainerName)}[/] not found on storage account [bold]{Markup.Escape(ex.AccountName)}[/].",
                        PreflightErrorKind.AccessDenied when ex.AuthMode == "key" =>
                            $"Access denied. Verify the account key is correct for storage account [bold]{Markup.Escape(ex.AccountName)}[/].",
                        PreflightErrorKind.AccessDenied =>
                            $"Authenticated via Azure CLI but access was denied on storage account [bold]{Markup.Escape(ex.AccountName)}[/].\n\n" +
                            $"Assign the required RBAC role:\n" +
                            $"  Storage Blob Data Reader\n\n" +
                            $"  az role assignment create --assignee <your-email> --role \"Storage Blob Data Reader\" " +
                            $"--scope /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Storage/storageAccounts/{Markup.Escape(ex.AccountName)}",
                        PreflightErrorKind.CredentialUnavailable =>
                            $"No account key found and Azure CLI is not logged in.\n\n" +
                            $"Provide a key via:\n" +
                            $"  --key / -k\n" +
                            $"  ARIUS_KEY environment variable\n" +
                            $"  dotnet user-secrets\n\n" +
                            $"Or log in via Azure CLI:\n" +
                            $"  az login",
                        _ =>
                            $"Could not connect to storage account [bold]{Markup.Escape(ex.AccountName)}[/]: {Markup.Escape(ex.InnerException?.Message ?? ex.Message)}",
                    };
                    AnsiConsole.MarkupLine($"[red]Error:[/] {msg}");
                    return 1;
                }

                var mediator        = services.GetRequiredService<IMediator>();
                var restoreProgress = services.GetRequiredService<ProgressState>();

                // TCS pairs for thread-safe phase coordination between pipeline and CLI prompt threads
                var questionTcs        = new TaskCompletionSource<RestoreCostEstimate>(TaskCreationOptions.RunContinuationsAsynchronously);
                var answerTcs          = new TaskCompletionSource<RehydratePriority?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var cleanupQuestionTcs = new TaskCompletionSource<(int count, long bytes)>(TaskCreationOptions.RunContinuationsAsynchronously);
                var cleanupAnswerTcs   = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                var opts = new RestoreOptions
                {
                    RootDirectory = Path.GetFullPath(path),
                    Version       = version,
                    NoPointers    = noPointers,
                    Overwrite     = overwrite,

                    ConfirmRehydration = async (estimate, cancellationToken) =>
                    {
                        questionTcs.TrySetResult(estimate);
                        using var reg = cancellationToken.Register(
                            () => answerTcs.TrySetCanceled(cancellationToken));
                        return await answerTcs.Task.ConfigureAwait(false);
                    },

                    ConfirmCleanup = async (count, bytes, cancellationToken) =>
                    {
                        cleanupQuestionTcs.TrySetResult((count, bytes));
                        using var reg = cancellationToken.Register(
                            () => cleanupAnswerTcs.TrySetCanceled(cancellationToken));
                        return await cleanupAnswerTcs.Task.ConfigureAwait(false);
                    },

                    CreateDownloadProgress = (identifier, compressedSize, kind) =>
                    {
                        string displayName;
                        long originalSize = 0;

                        if (kind == DownloadKind.TarBundle)
                        {
                            if (ChunkHash.TryParse(identifier, out var typedChunkHash)
                                && restoreProgress.TarBundleMetadata.TryGetValue(typedChunkHash, out var meta))
                            {
                                displayName  = $"TAR bundle ({meta.FileCount} files, {meta.OriginalSize.Bytes().Humanize()})";
                                originalSize = meta.OriginalSize;
                            }
                            else
                            {
                                displayName = $"TAR bundle ({identifier[..8]}...)";
                            }
                        }
                        else
                        {
                            displayName = identifier;
                        }

                        var tracked = new TrackedDownload(identifier, kind, displayName, compressedSize, originalSize);
                        restoreProgress.TrackedDownloads[identifier] = tracked;

                        return new Progress<long>(bytes => tracked.SetBytesDownloaded(bytes));
                    },
                };

                RestoreResult? result = null;
                if (AnsiConsole.Console.Profile.Capabilities.Interactive)
                {
                    var pipelineTask = mediator.Send(new RestoreCommand(opts), ct).AsTask();

                    await AnsiConsole.Live(new Markup(""))
                        .Overflow(VerticalOverflow.Crop)
                        .Cropping(VerticalOverflowCropping.Bottom)
                        .AutoClear(false)
                        .StartAsync(async ctx =>
                        {
                            while (!pipelineTask.IsCompleted && !questionTcs.Task.IsCompleted && !cleanupQuestionTcs.Task.IsCompleted)
                            {
                                ctx.UpdateTarget(BuildDisplay(restoreProgress));
                                await Task.WhenAny(pipelineTask, questionTcs.Task, cleanupQuestionTcs.Task, Task.Delay(100, ct)).ConfigureAwait(false);
                            }

                            ctx.UpdateTarget(BuildDisplay(restoreProgress));
                        });

                    if (!pipelineTask.IsCompleted)
                    {
                        if (!questionTcs.Task.IsCompleted && cleanupQuestionTcs.Task.IsCompleted)
                        {
                            var (count, bytes) = await cleanupQuestionTcs.Task.ConfigureAwait(false);
                            var doCleanup = AnsiConsole.Confirm(
                                $"Delete {count} rehydrated chunk(s) ({bytes.Bytes().Humanize()}) from Azure?");
                            cleanupAnswerTcs.TrySetResult(doCleanup);
                            result = await pipelineTask.ConfigureAwait(false);
                        }
                        else if (questionTcs.Task.IsCompleted)
                        {
                            // ── Phase 2: Rehydration question ─────────────────────────────────
                            var estimate = await questionTcs.Task.ConfigureAwait(false);

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

                            answerTcs.TrySetResult(priority);

                            if (priority is null)
                            {
                                result = await pipelineTask.ConfigureAwait(false);
                            }
                            else
                            {
                                // ── Phase 3: Download ──────────────────────────────────────────
                                await AnsiConsole.Live(new Markup(""))
                                    .Overflow(VerticalOverflow.Crop)
                                    .Cropping(VerticalOverflowCropping.Bottom)
                                    .AutoClear(false)
                                    .StartAsync(async ctx =>
                                    {
                                        while (!pipelineTask.IsCompleted && !cleanupQuestionTcs.Task.IsCompleted)
                                        {
                                            ctx.UpdateTarget(BuildDisplay(restoreProgress));
                                            await Task.WhenAny(pipelineTask, cleanupQuestionTcs.Task, Task.Delay(100, ct)).ConfigureAwait(false);
                                        }

                                        ctx.UpdateTarget(BuildDisplay(restoreProgress));
                                    });

                                // ── Phase 4: Cleanup question ──────────────────────────────────
                                if (!pipelineTask.IsCompleted && cleanupQuestionTcs.Task.IsCompleted)
                                {
                                    var (count, bytes) = await cleanupQuestionTcs.Task.ConfigureAwait(false);
                                    var doCleanup = AnsiConsole.Confirm(
                                        $"Delete {count} rehydrated chunk(s) ({bytes.Bytes().Humanize()}) from Azure?");
                                    cleanupAnswerTcs.TrySetResult(doCleanup);
                                }
                                result = await pipelineTask.ConfigureAwait(false);
                            }
                        }
                    }
                    else
                    {
                        if (cleanupQuestionTcs.Task.IsCompleted)
                        {
                            var (count, bytes) = await cleanupQuestionTcs.Task.ConfigureAwait(false);
                            var doCleanup = AnsiConsole.Confirm(
                                $"Delete {count} rehydrated chunk(s) ({bytes.Bytes().Humanize()}) from Azure?");
                            cleanupAnswerTcs.TrySetResult(doCleanup);
                        }
                        result = await pipelineTask.ConfigureAwait(false);
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
                CliBuilder.FlushAuditLog(recorder);
            }
        });

        return cmd;
    }

    /// <summary>
    /// Builds the restore display renderable as a pure function of <see cref="ProgressState"/>.
    /// </summary>
    internal static IRenderable BuildDisplay(ProgressState state)
    {
        var lines = new List<IRenderable>();

        var total    = state.RestoreTotalFiles;
        var restored = state.FilesRestored;
        var skipped  = state.FilesSkipped;
        var done     = restored + skipped;
        var allDone  = state.TreeTraversalComplete && done >= total;

        // ── Stage 1: Resolving / Resolved ─────────────────────────────────────
        {
            var treeComplete = state.TreeTraversalComplete;
            if (treeComplete)
            {
                var ts       = state.SnapshotTimestamp;
                var tsStr    = ts.HasValue ? ts.Value.ToString("o") : "?";
                var fileCount = total;
                var sizeTotal = state.RestoreTotalOriginalSize;
                var detail = sizeTotal > 0
                    ? $"{tsStr} ({fileCount:N0} files, {sizeTotal.Bytes().Humanize()})"
                    : $"{tsStr} ({fileCount:N0} files)";
                lines.Add(new Markup($"  [green]●[/] Resolved     {Markup.Escape(detail)}"));
            }
            else
            {
                var discovered = state.RestoreFilesDiscovered;
                var detail = discovered > 0 ? $"{discovered:N0} files..." : string.Empty;
                lines.Add(new Markup($"  [dim]○[/] Resolving    {Markup.Escape(detail)}"));
            }
        }

        // ── Stage 2: Checking / Checked ───────────────────────────────────────
        {
            var dispTotal = state.DispositionNew + state.DispositionSkipIdentical
                            + state.DispositionOverwrite + state.DispositionKeepLocalDiffers;
            var dispositionStarted = dispTotal > 0;
            var checkedComplete = state.ChunkGroups > 0 || done > 0 || (state.TreeTraversalComplete && total == 0);

            string checkedSymbol;
            if (checkedComplete && (dispositionStarted || total == 0))
                checkedSymbol = "[green]●[/]";
            else if (dispositionStarted)
                checkedSymbol = "[yellow]○[/]";
            else
                checkedSymbol = "[dim]○[/]";

            lines.Add(new Markup($"  {checkedSymbol} Checked      {state.DispositionNew:N0} new, {state.DispositionSkipIdentical:N0} identical, {state.DispositionOverwrite:N0} overwrite, {state.DispositionKeepLocalDiffers:N0} kept"));
        }

        // ── Stage 3: Restoring ────────────────────────────────────────────────
        {
            string restoringSymbol;
            if (allDone)
                restoringSymbol = "[green]●[/]";
            else if (done > 0 || state.RestoreBytesDownloaded > 0)
                restoringSymbol = "[yellow]○[/]";
            else
                restoringSymbol = "[dim]○[/]";

            var countStr = state.TreeTraversalComplete ? $"{done:N0}/{total:N0} files" : $"{done:N0} files";

            var totalCompressed = state.RestoreTotalCompressedBytes;
            var bytesDownloaded = state.RestoreBytesDownloaded;
            var totalOriginal   = state.RestoreTotalOriginalSize;

            if (totalCompressed > 0)
            {
                var fraction = (double)bytesDownloaded / totalCompressed;
                var pct      = (int)Math.Round(fraction * 100);
                var bar      = DisplayHelpers.RenderProgressBar(fraction, 16);

                lines.Add(new Markup($"  {restoringSymbol} Restoring    {countStr}  {bar}  {pct}%"));

                var (dlCur, dlTot, dlUnit) = DisplayHelpers.SplitSizePair(bytesDownloaded, totalCompressed);
                var origStr = totalOriginal.Bytes().Humanize();

                lines.Add(new Markup($"                 [dim]({dlCur} / {dlTot} {dlUnit} download, {origStr} original)[/]"));
            }
            else
            {
                lines.Add(new Markup($"  {restoringSymbol} Restoring    {countStr}"));
            }
        }

        // ── Active download table ─────────────────────────────────────────────
        if (!allDone)
        {
            var activeDownloads = state.TrackedDownloads.Values.ToArray();
            if (activeDownloads.Length > 0)
            {
                lines.Add(new Markup(""));

                var rowData = new List<(string name, string bar, string pct, string cur, string tot, string unit)>();

                foreach (var dl in activeDownloads)
                {
                    var fraction = dl.CompressedSize > 0
                        ? (double)dl.BytesDownloaded / dl.CompressedSize
                        : 0.0;
                    var pctVal = (int)Math.Round(fraction * 100);
                    var bar    = DisplayHelpers.RenderProgressBar(fraction, 12);
                    var name   = DisplayHelpers.TruncateAndLeftJustify(dl.DisplayName, 35);
                    var (cur, tot, unit) = DisplayHelpers.SplitSizePair(dl.BytesDownloaded, dl.CompressedSize);

                    rowData.Add((name, bar, pctVal + "%", cur, tot, unit));
                }

                var maxPct = rowData.Max(r => r.pct.Length);
                var maxCur = rowData.Max(r => r.cur.Length);
                var maxTot = rowData.Max(r => r.tot.Length);

                var table = new Table()
                    .NoBorder()
                    .HideHeaders()
                    .AddColumn(new TableColumn("").NoWrap().LeftAligned())
                    .AddColumn(new TableColumn("").NoWrap().LeftAligned())
                    .AddColumn(new TableColumn("").NoWrap().RightAligned())
                    .AddColumn(new TableColumn("").NoWrap().LeftAligned());

                foreach (var (name, bar, pct, cur, tot, unit) in rowData)
                {
                    var paddedPct = pct.PadLeft(maxPct);
                    var paddedCur = cur.PadLeft(maxCur);
                    var paddedTot = tot.PadLeft(maxTot);
                    var sizeStr   = $"{paddedCur} / {paddedTot} {unit}";

                    table.AddRow(
                        new Markup("[dim]" + Markup.Escape(name) + "[/]"),
                        new Markup(bar),
                        new Markup("[dim]" + paddedPct + "[/]"),
                        new Markup("[dim]" + sizeStr + "[/]"));
                }

                lines.Add(table);
            }
            else
            {
                var recent = state.RecentRestoreEvents.ToArray();
                if (recent.Length > 0)
                {
                    lines.Add(new Markup(""));
                    foreach (var ev in recent)
                    {
                        var sym     = ev.Skipped ? "[dim]○[/]" : "[green]●[/]";
                        var path    = Markup.Escape(DisplayHelpers.TruncateAndLeftJustify(ev.RelativePath, 40));
                        var sizeStr = Markup.Escape(ev.FileSize.Bytes().Humanize());
                        lines.Add(new Markup($"  {sym} [dim]{path}[/]  ({sizeStr})"));
                    }
                }
            }
        }

        return new Rows(lines);
    }
}
