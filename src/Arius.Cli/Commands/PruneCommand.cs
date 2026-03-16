using System.CommandLine;
using Arius.Core.Application.Prune;
using Humanizer;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Arius.Cli.Commands;

internal static class PruneCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("prune", "Remove unreferenced data packs from storage");

        var repoOpt         = new Option<string?>("--repo", "-r")     { Description = "Azure connection string" };
        var containerOpt    = new Option<string?>("--container", "-c") { Description = "Container name" };
        var passwordFileOpt = new Option<string?>("--password-file")   { Description = "Password file path" };
        var dryRunOpt       = new Option<bool>("--dry-run")            { Description = "Show what would be pruned without making changes" };
        var yesOpt          = new Option<bool>("--yes", "-y")          { Description = "Skip confirmation" };

        cmd.Options.Add(repoOpt);
        cmd.Options.Add(containerOpt);
        cmd.Options.Add(passwordFileOpt);
        cmd.Options.Add(dryRunOpt);
        cmd.Options.Add(yesOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var repo = GlobalOptions.ResolveRepo(parseResult.GetValue(repoOpt));
            if (string.IsNullOrEmpty(repo)) { AnsiConsole.MarkupLine("[red]Error:[/] No --repo specified."); return; }
            var container = parseResult.GetValue(containerOpt) ?? Environment.GetEnvironmentVariable("ARIUS_CONTAINER");
            if (string.IsNullOrEmpty(container)) { AnsiConsole.MarkupLine("[red]Error:[/] No --container specified."); return; }

            var passphrase = GlobalOptions.ResolvePassphrase(parseResult.GetValue(passwordFileOpt));
            var dryRun     = parseResult.GetValue(dryRunOpt);
            var yes        = parseResult.GetValue(yesOpt);

            // Preview first
            var handler  = services.GetRequiredService<PruneHandler>();
            var preview  = new PruneRequest(repo, container, passphrase, DryRun: true);
            var toDelete = new List<string>();
            var toRepack = new List<string>();

            await foreach (var ev in handler.Handle(preview, ct))
            {
                if (ev.Kind == PruneEventKind.WillDelete) { toDelete.Add(ev.PackId!); AnsiConsole.MarkupLine($"[red]delete[/] pack {ev.PackId![..8]}"); }
                else if (ev.Kind == PruneEventKind.WillRepack) { toRepack.Add(ev.PackId!); AnsiConsole.MarkupLine($"[yellow]repack[/] pack {ev.PackId![..8]}"); }
                else if (ev.Kind == PruneEventKind.Analysing) AnsiConsole.MarkupLine($"[grey]{Markup.Escape(ev.Message)}[/]");
            }

            if (toDelete.Count == 0 && toRepack.Count == 0)
            {
                AnsiConsole.MarkupLine("[green]Nothing to prune.[/]");
                return;
            }

            AnsiConsole.MarkupLine($"Will delete {"pack".ToQuantity(toDelete.Count)}, repack {"pack".ToQuantity(toRepack.Count)}.");

            if (!dryRun)
            {
                if (!yes && !AnsiConsole.Confirm("Proceed?")) return;

                var request = new PruneRequest(repo, container, passphrase, DryRun: false);
                await foreach (var ev in handler.Handle(request, ct))
                {
                    var color = ev.Kind switch
                    {
                        PruneEventKind.Deleting  => "red",
                        PruneEventKind.Repacking => "yellow",
                        PruneEventKind.Done      => "green",
                        _                        => "grey"
                    };
                    AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(ev.Message)}[/]");
                }
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Dry run — no changes made.[/]");
            }
        });

        return cmd;
    }
}
