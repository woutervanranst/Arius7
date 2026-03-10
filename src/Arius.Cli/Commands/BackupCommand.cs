using System.CommandLine;
using System.Text.Json;
using Arius.Core.Application.Backup;
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
            Description = "Repository path (or set ARIUS_REPOSITORY env var)"
        };
        var passwordFileOpt = new Option<string?>("--password-file")
        {
            Description = "File containing the repository passphrase"
        };
        var jsonOpt = new Option<bool>("--json")
        {
            Description = "Output result as JSON"
        };
        var pathsArg = new Argument<string[]>("paths")
        {
            Description = "Files or directories to back up",
            Arity = ArgumentArity.OneOrMore
        };

        cmd.Options.Add(repoOpt);
        cmd.Options.Add(passwordFileOpt);
        cmd.Options.Add(jsonOpt);
        cmd.Arguments.Add(pathsArg);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var repo = GlobalOptions.ResolveRepo(parseResult.GetValue(repoOpt));
            if (string.IsNullOrEmpty(repo))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No repository path specified. Use --repo or set ARIUS_REPOSITORY.");
                return;
            }

            var passphrase = GlobalOptions.ResolvePassphrase(parseResult.GetValue(passwordFileOpt));
            var paths = parseResult.GetValue(pathsArg) ?? [];
            var asJson = parseResult.GetValue(jsonOpt);

            var handler = services.GetRequiredService<BackupHandler>();
            var request = new BackupRequest(repo, passphrase, paths);

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
                                task.Description = $"Backing up {totalFiles} file(s)...";
                                break;

                            case BackupFileProcessed file:
                                processed++;
                                if (file.IsDeduplicated) deduplicated++;
                                task.Value = processed;
                                task.Description = $"[{processed}/{totalFiles}] {Path.GetFileName(file.Path)}";
                                break;

                            case BackupCompleted completed:
                                task.Value = task.MaxValue;
                                task.Description = $"Done — snapshot [cyan]{completed.Snapshot.Id.Value[..8]}[/]";
                                break;
                        }
                    }
                });

            AnsiConsole.MarkupLine($"\n[green]Backup complete![/] {processed} files processed, {deduplicated} deduplicated.");
        });

        return cmd;
    }
}
