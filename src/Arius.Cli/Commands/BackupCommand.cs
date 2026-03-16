using System.CommandLine;
using System.Text.Json;
using Arius.Core.Application.Abstractions;
using Arius.Core.Application.Backup;
using Arius.Core.Models;
using Humanizer;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Arius.Cli.Commands;

internal static class BackupCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("backup", "Create a new backup snapshot");

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
        var jsonOpt = new Option<bool>("--json")
        {
            Description = "Output result as JSON"
        };
        var tierOpt = new Option<string>("--tier")
        {
            Description = "Access tier for data packs: hot, cool, cold, archive (default: archive)",
            DefaultValueFactory = _ => "archive"
        };
        var parallelismOpt = new Option<int>("--parallelism")
        {
            Description = "Maximum number of parallel file processors (0 = auto)",
            DefaultValueFactory = _ => 0
        };
        var pathsArg = new Argument<string[]>("paths")
        {
            Description = "Files or directories to back up",
            Arity = ArgumentArity.OneOrMore
        };

        cmd.Options.Add(repoOpt);
        cmd.Options.Add(containerOpt);
        cmd.Options.Add(passwordFileOpt);
        cmd.Options.Add(jsonOpt);
        cmd.Options.Add(tierOpt);
        cmd.Options.Add(parallelismOpt);
        cmd.Arguments.Add(pathsArg);

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
            var paths       = parseResult.GetValue(pathsArg) ?? [];
            var asJson      = parseResult.GetValue(jsonOpt);
            var tier        = ParseTier(parseResult.GetValue(tierOpt) ?? "archive");
            var parallelism = parseResult.GetValue(parallelismOpt);

            ParallelismOptions? parallelismOpts = parallelism > 0
                ? new ParallelismOptions(parallelism, 0, 0, 0, 0)
                : null;

            var handler = services.GetRequiredService<BackupHandler>();
            var request = new BackupRequest(repo, container, passphrase, paths, tier, parallelismOpts);

            if (asJson)
            {
                await foreach (var evt in handler.Handle(request, ct))
                {
                    Console.WriteLine(JsonSerializer.Serialize(evt, evt.GetType()));
                }
                return;
            }

            // Rich Spectre.Console progress rendering
            int totalFiles = 0;
            int processed = 0;
            int deduplicated = 0;
            var errors = new List<string>();

            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Backing up...", maxValue: 1);

                    await foreach (var evt in handler.Handle(request, ct))
                    {
                        switch (evt)
                        {
                            case BackupStarted started:
                                totalFiles = started.TotalFiles;
                                task.MaxValue = totalFiles > 0 ? totalFiles : 1;
                                task.Description = $"Backing up {"file".ToQuantity(totalFiles)}...".EscapeMarkup();
                                break;

                            case BackupFileProcessed file:
                                processed++;
                                if (file.IsDeduplicated) deduplicated++;
                                task.Value = processed;
                                task.Description = $"[{processed}/{totalFiles}] {Path.GetFileName(file.Path)}".EscapeMarkup();
                                break;

                            case BackupFileError err:
                                errors.Add(err.Path);
                                AnsiConsole.MarkupLine($"[red]Error:[/] {err.Path.EscapeMarkup()} — {err.Error.EscapeMarkup()}");
                                break;

                            case BackupCompleted completed:
                                task.Value = task.MaxValue;
                                var snapshotId = completed.Snapshot?.Id.Value[..8] ?? "none";
                                task.Description =
                                    $"Done — snapshot [cyan]{snapshotId}[/] | " +
                                    $"new: {completed.NewBytes.Bytes().Humanize()} in {"chunk".ToQuantity(completed.NewChunks)} | " +
                                    $"dedup: {completed.DeduplicatedBytes.Bytes().Humanize()}";
                                break;
                        }
                    }
                });

            if (errors.Count > 0)
                AnsiConsole.MarkupLine($"\n[yellow]Warning:[/] {"file".ToQuantity(errors.Count)} failed.");

            AnsiConsole.MarkupLine(
                $"\n[green]Backup complete![/] " +
                $"{"file".ToQuantity(processed)} processed, {deduplicated} deduplicated.");
        });

        return cmd;
    }

    private static BlobAccessTier ParseTier(string value) => value.ToLowerInvariant() switch
    {
        "hot"     => BlobAccessTier.Hot,
        "cool"    => BlobAccessTier.Cool,
        "cold"    => BlobAccessTier.Cold,
        "archive" => BlobAccessTier.Archive,
        _         => BlobAccessTier.Archive
    };
}
