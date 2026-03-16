using System.CommandLine;
using System.Text.Json;
using Arius.Core.Application.Restore;
using Arius.Core.Models;
using Humanizer;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Arius.Cli.Commands;

internal static class RestoreCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("restore", "Restore files from a snapshot");

        var repoOpt = new Option<string?>("--repo", "-r")
        {
            Description = "Azure connection string (or set ARIUS_REPOSITORY env var)"
        };
        var containerOpt = new Option<string?>("--container", "-c")
        {
            Description = "Azure Blob Storage container name (or set ARIUS_CONTAINER env var)"
        };
        var passwordFileOpt = new Option<string?>("--password-file")
        {
            Description = "File containing the repository passphrase"
        };
        var targetOpt = new Option<string>("--target")
        {
            Description = "Target directory to restore files to",
            Required = true
        };
        var includeOpt = new Option<string?>("--include")
        {
            Description = "Only restore files matching this pattern"
        };
        var jsonOpt = new Option<bool>("--json")
        {
            Description = "Output result as JSON"
        };
        var yesOpt = new Option<bool>("--yes", "-y")
        {
            Description = "Skip cost confirmation prompt"
        };
        var parallelismOpt = new Option<int>("--parallelism")
        {
            Description = "Maximum number of parallel downloaders/assemblers (0 = auto)",
            DefaultValueFactory = _ => 0
        };
        var snapshotArg = new Argument<string>("snapshot-id")
        {
            Description = "Snapshot ID (or prefix) to restore from"
        };

        cmd.Options.Add(repoOpt);
        cmd.Options.Add(containerOpt);
        cmd.Options.Add(passwordFileOpt);
        cmd.Options.Add(targetOpt);
        cmd.Options.Add(includeOpt);
        cmd.Options.Add(jsonOpt);
        cmd.Options.Add(yesOpt);
        cmd.Options.Add(parallelismOpt);
        cmd.Arguments.Add(snapshotArg);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var repo = GlobalOptions.ResolveRepo(parseResult.GetValue(repoOpt));
            if (string.IsNullOrEmpty(repo))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No connection string specified. Use --repo or set ARIUS_REPOSITORY.");
                return;
            }

            var container = parseResult.GetValue(containerOpt)
                ?? Environment.GetEnvironmentVariable("ARIUS_CONTAINER");
            if (string.IsNullOrEmpty(container))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No container name specified. Use --container or set ARIUS_CONTAINER.");
                return;
            }

            var passphrase  = GlobalOptions.ResolvePassphrase(parseResult.GetValue(passwordFileOpt));
            var snapshotId  = parseResult.GetValue(snapshotArg) ?? string.Empty;
            var target      = parseResult.GetValue(targetOpt) ?? string.Empty;
            var include     = parseResult.GetValue(includeOpt);
            var asJson      = parseResult.GetValue(jsonOpt);
            var yes         = parseResult.GetValue(yesOpt);
            var parallelism = parseResult.GetValue(parallelismOpt);

            ParallelismOptions? parallelismOpts = parallelism > 0
                ? new ParallelismOptions(0, 0, 0, parallelism, parallelism)
                : null;

            var handler = services.GetRequiredService<RestoreHandler>();
            var request = new RestoreRequest(repo, container, passphrase, snapshotId, target, include, parallelismOpts);

            if (asJson)
            {
                await foreach (var evt in handler.Handle(request, ct))
                {
                    Console.WriteLine(JsonSerializer.Serialize(evt, evt.GetType()));
                }
                return;
            }

            int restoredFiles = 0;
            long restoredBytes = 0;
            int packsDownloaded = 0;
            int failedFiles = 0;

            await AnsiConsole.Live(new Markup("Starting restore..."))
                .StartAsync(async ctx =>
                {
                    await foreach (var evt in handler.Handle(request, ct))
                    {
                        switch (evt)
                        {
                            case RestorePlanReady plan:
                                if (!yes)
                                {
                                    ctx.UpdateTarget(new Markup(
                                        $"[yellow]Plan:[/] {"file".ToQuantity(plan.TotalFiles)}, " +
                                        $"{plan.TotalBytes.Bytes().Humanize()}, " +
                                        $"{"pack".ToQuantity(plan.PacksToDownload)} to download"));
                                }
                                break;

                            case RestorePackFetched pack:
                                packsDownloaded++;
                                ctx.UpdateTarget(new Markup(
                                    $"Fetching packs... {packsDownloaded} — [dim]{pack.PackId[..8]}[/] ({pack.BlobCount} blobs)"));
                                break;

                            case RestoreFileRestored file:
                                restoredFiles++;
                                restoredBytes += file.Size;
                                ctx.UpdateTarget(new Markup(
                                    $"Restoring... {"file".ToQuantity(restoredFiles)} — [dim]{Path.GetFileName(file.Path)}[/]"));
                                break;

                            case RestoreFileError err:
                                failedFiles++;
                                AnsiConsole.MarkupLine($"[red]Error:[/] {err.Path.EscapeMarkup()} — {err.Error.EscapeMarkup()}");
                                break;

                            case RestoreCompleted completed:
                                ctx.UpdateTarget(new Markup(
                                    $"[green]Done:[/] {"file".ToQuantity(completed.RestoredFiles)} restored " +
                                    $"({completed.RestoredBytes.Bytes().Humanize()})" +
                                    (completed.Failed > 0 ? $" [red]{completed.Failed} failed[/]" : "")));
                                restoredFiles = completed.RestoredFiles;
                                restoredBytes = completed.RestoredBytes;
                                failedFiles   = completed.Failed;
                                break;
                        }
                    }
                });

            if (failedFiles > 0)
                AnsiConsole.MarkupLine($"\n[yellow]Warning:[/] {"file".ToQuantity(failedFiles)} failed to restore.");

            AnsiConsole.MarkupLine(
                $"\n[green]Restore complete![/] " +
                $"{"file".ToQuantity(restoredFiles)} restored to [dim]{target}[/] " +
                $"({restoredBytes.Bytes().Humanize()})");
        });

        return cmd;
    }
}
