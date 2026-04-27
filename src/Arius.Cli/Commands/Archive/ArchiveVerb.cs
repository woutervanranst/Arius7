using System.CommandLine;
using System.Globalization;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;
using Humanizer;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Arius.Cli.Commands.Archive;

internal static class ArchiveVerb
{
    internal static Command Build(
        Func<string, string?, string?, string, PreflightMode, Task<IServiceProvider>> serviceProviderFactory)
    {
        var accountOption    = CliBuilder.AccountOption();
        var keyOption        = CliBuilder.KeyOption();
        var passphraseOption = CliBuilder.PassphraseOption();
        var containerOption  = CliBuilder.ContainerOption();

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

            var resolvedAccount = CliBuilder.ResolveAccount(account);
            if (resolvedAccount is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No account provided. Use --account / -a or set ARIUS_ACCOUNT.");
                return 1;
            }

            var resolvedKey = CliBuilder.ResolveKey(key, resolvedAccount);

            CliBuilder.ConfigureAuditLogging(resolvedAccount, container, "archive");
            var recorder = AnsiConsole.Console.CreateRecorder();
            var savedConsole = AnsiConsole.Console;
            AnsiConsole.Console = recorder;

            try
            {
                IServiceProvider services;
                try
                {
                    services = await serviceProviderFactory(resolvedAccount, resolvedKey, passphrase, container, PreflightMode.ReadWrite).ConfigureAwait(false);
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
                            $"  Storage Blob Data Contributor\n\n" +
                            $"  az role assignment create --assignee <your-email> --role \"Storage Blob Data Contributor\" " +
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

                var mediator      = services.GetRequiredService<IMediator>();
                var progressState = services.GetRequiredService<ProgressState>();

                var opts = new ArchiveCommandOptions
                {
                    RootDirectory      = Path.GetFullPath(path),
                    UploadTier         = tier,
                    RemoveLocal        = removeLocal,
                    NoPointers         = noPointers,
                    SmallFileThreshold = 1024 * 1024L,
                    TarTargetSize      = 64L * 1024 * 1024,

                    CreateHashProgress = (relativePath, fileSize) =>
                    {
                        if (progressState.TrackedFiles.TryGetValue(relativePath, out var file))
                            return new Progress<long>(bytes => file.SetBytesProcessed(bytes));
                        return new Progress<long>();
                    },

                    CreateUploadProgress = (chunkHash, size) =>
                    {
                        if (progressState.ContentHashToPath.TryGetValue(ContentHash.Parse(chunkHash), out var paths))
                        {
                            var files = paths
                                .Select(p => progressState.TrackedFiles.TryGetValue(p, out var f) ? f : null)
                                .Where(f => f != null)
                                .ToList();
                            if (files.Count > 0)
                            {
                                foreach (var f in files) f!.SetBytesProcessed(0);
                                return new Progress<long>(bytes => { foreach (var f in files) f!.SetBytesProcessed(bytes); });
                            }
                        }

                        var tar = progressState.TrackedTars.Values.FirstOrDefault(t => t.TarHash == chunkHash);
                        if (tar != null)
                        {
                            tar.SetBytesUploaded(0);
                            return new Progress<long>(bytes => tar.SetBytesUploaded(bytes));
                        }

                        return new Progress<long>();
                    },

                    OnHashQueueReady   = getter => progressState.HashQueueDepth   = getter,
                    OnUploadQueueReady = getter => progressState.UploadQueueDepth = getter,
                };

                progressState.TarTargetSize = opts.TarTargetSize;

                ArchiveResult? result = null;
                if (AnsiConsole.Console.Profile.Capabilities.Interactive)
                {
                    await AnsiConsole.Live(BuildDisplay(progressState))
                        .Overflow(VerticalOverflow.Crop)
                        .Cropping(VerticalOverflowCropping.Bottom)
                        .AutoClear(false)
                        .StartAsync(async ctx =>
                        {
                            var archiveTask = mediator.Send(new ArchiveCommand(opts), ct).AsTask();

                            while (!archiveTask.IsCompleted)
                            {
                                ctx.UpdateTarget(BuildDisplay(progressState));
                                await Task.WhenAny(archiveTask, Task.Delay(100, ct)).ConfigureAwait(false);
                            }

                            result = await archiveTask;
                            ctx.UpdateTarget(BuildDisplay(progressState));
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
                CliBuilder.FlushAuditLog(recorder);
            }
        });

        return cmd;
    }

    /// <summary>
    /// Builds the archive display renderable as a pure function of <see cref="ProgressState"/>.
    /// </summary>
    internal static IRenderable BuildDisplay(ProgressState state)
    {
        var lines = new List<IRenderable>();

        // ── Scanning header ───────────────────────────────────────────────────
        var displayCount = state.ScanComplete
            ? (state.TotalFiles ?? state.FilesScanned)
            : state.FilesScanned;
        if (state.ScanComplete)
            lines.Add(new Markup($"  [green]●[/] Scanning   [dim]{displayCount:N0} files[/]"));
        else
            lines.Add(new Markup($"  [yellow]○[/] Scanning   [dim]{displayCount:N0} files...[/]"));

        // ── Hashing header ────────────────────────────────────────────────────
        {
            var filesHashed  = state.FilesHashed;
            var filesScanned = state.FilesScanned;
            var filesUnique  = state.FilesUnique;
            var queueDepth   = state.HashQueueDepth?.Invoke() ?? 0;

            var countPart  = filesScanned > 0 ? $"{filesHashed:N0} / {filesScanned:N0} files" : $"{filesHashed:N0} files";
            var uniquePart = filesUnique  > 0 ? $" ({filesUnique:N0} unique)" : string.Empty;
            var queuePart  = queueDepth   > 0 ? $"  [dim][[{queueDepth} pending]][/]" : string.Empty;

            var done   = state.ScanComplete && filesHashed >= filesScanned && filesScanned > 0;
            var symbol = done ? "[green]●[/]" : "[yellow]○[/]";
            lines.Add(new Markup($"  {symbol} Hashing    [dim]{countPart}{uniquePart}[/]{queuePart}"));
        }

        // ── Uploading header ──────────────────────────────────────────────────
        {
            var chunksUploaded = state.ChunksUploaded;
            var tarsUploaded   = state.TarsUploaded;
            var queueDepth     = state.UploadQueueDepth?.Invoke() ?? 0;

            var uploadActive = chunksUploaded > 0 || tarsUploaded > 0 || queueDepth > 0 || !state.SnapshotComplete && state.ScanComplete;
            if (uploadActive)
            {
                var queuePart    = queueDepth > 0 ? $"  [dim][[{queueDepth} pending]][/]" : string.Empty;
                var uploadDone   = state.SnapshotComplete;
                var uploadSymbol = uploadDone ? "[green]●[/]" : "[yellow]○[/]";
                lines.Add(new Markup($"  {uploadSymbol} Uploading  [dim]{chunksUploaded:N0} unique chunks[/]{queuePart}"));
            }
            else
            {
                lines.Add(new Markup("  [grey]  Uploading[/]"));
            }
        }

        // ── Per-file and TAR bundle rows ──────────────────────────────────────
        var activeFiles = state.TrackedFiles.Values
            .Where(f => f.State is FileState.Hashing or FileState.Uploading)
            .ToList();
        var trackedTars = state.TrackedTars.Values.OrderBy(t => t.BundleNumber).ToList();

        if (activeFiles.Count > 0 || trackedTars.Count > 0)
        {
            lines.Add(new Markup(""));

            var rowData = new List<(string name, string bar, string stateLabel, string pct, string cur, string tot, string unit)>();

            foreach (var file in activeFiles)
            {
                var pct = file.TotalBytes > 0 ? (double)file.BytesProcessed / file.TotalBytes : 0.0;
                var (cur, tot, unit) = DisplayHelpers.SplitSizePair(file.BytesProcessed, file.TotalBytes);
                rowData.Add((
                    DisplayHelpers.TruncateAndLeftJustify(file.RelativePath, 30),
                    DisplayHelpers.RenderProgressBar(pct, 12),
                    file.State == FileState.Hashing ? "Hashing" : "Uploading",
                    Math.Min(pct * 100, 100).ToString("F0", CultureInfo.InvariantCulture) + "%",
                    cur, tot, unit));
            }

            foreach (var tar in trackedTars)
            {
                var label = $"TAR #{tar.BundleNumber} ({tar.FileCount} files, {tar.AccumulatedBytes.Bytes().Humanize()})";
                string stateText, bar, pctText, cur, tot, unit;

                switch (tar.State)
                {
                    case TarState.Accumulating:
                    {
                        var pct = tar.TargetSize > 0 ? (double)tar.AccumulatedBytes / tar.TargetSize : 0.0;
                        bar       = DisplayHelpers.RenderProgressBar(pct, 12);
                        stateText = "Accumulating";
                        pctText   = Math.Min(pct * 100, 100).ToString("F0", CultureInfo.InvariantCulture) + "%";
                        (cur, tot, unit) = DisplayHelpers.SplitSizePair(tar.AccumulatedBytes, tar.TargetSize);
                        break;
                    }
                    case TarState.Sealing:
                    {
                        var pct = tar.TargetSize > 0 ? (double)tar.AccumulatedBytes / tar.TargetSize : 1.0;
                        bar       = DisplayHelpers.RenderProgressBar(pct, 12);
                        stateText = "Sealing";
                        pctText   = Math.Min(pct * 100, 100).ToString("F0", CultureInfo.InvariantCulture) + "%";
                        (cur, tot, unit) = DisplayHelpers.SplitSizePair(tar.AccumulatedBytes, tar.TargetSize);
                        break;
                    }
                    default: // Uploading
                    {
                        var totalBytes = tar.TotalBytes > 0 ? tar.TotalBytes : tar.AccumulatedBytes;
                        var pct        = totalBytes > 0 ? (double)tar.BytesUploaded / totalBytes : 0.0;
                        bar       = DisplayHelpers.RenderProgressBar(pct, 12);
                        stateText = "Uploading";
                        pctText   = Math.Min(pct * 100, 100).ToString("F0", CultureInfo.InvariantCulture) + "%";
                        (cur, tot, unit) = DisplayHelpers.SplitSizePair(tar.BytesUploaded, totalBytes);
                        break;
                    }
                }

                rowData.Add((DisplayHelpers.TruncateAndLeftJustify(label, 30), bar, stateText, pctText, cur, tot, unit));
            }

            var maxPct   = rowData.Max(r => r.pct.Length);
            var maxCur   = rowData.Max(r => r.cur.Length);
            var maxTot   = rowData.Max(r => r.tot.Length);
            var maxState = rowData.Max(r => r.stateLabel.Length);

            var table = new Table()
                .NoBorder()
                .HideHeaders()
                .AddColumn(new TableColumn("").NoWrap().LeftAligned())
                .AddColumn(new TableColumn("").NoWrap().LeftAligned())
                .AddColumn(new TableColumn("").NoWrap().LeftAligned())
                .AddColumn(new TableColumn("").NoWrap().RightAligned())
                .AddColumn(new TableColumn("").NoWrap().LeftAligned());

            foreach (var (name, bar, stateLabel, pct, cur, tot, unit) in rowData)
            {
                var paddedState = stateLabel.PadRight(maxState);
                var paddedPct   = pct.PadLeft(maxPct);
                var paddedCur   = cur.PadLeft(maxCur);
                var paddedTot   = tot.PadLeft(maxTot);
                var sizeStr     = $"{paddedCur} / {paddedTot} {unit}";

                table.AddRow(
                    new Markup("[dim]" + Markup.Escape(name) + "[/]"),
                    new Markup(bar),
                    new Markup("[dim]" + paddedState + "[/]"),
                    new Markup("[dim]" + paddedPct + "[/]"),
                    new Markup("[dim]" + sizeStr + "[/]"));
            }

            lines.Add(table);
        }

        return new Rows(lines);
    }
}
