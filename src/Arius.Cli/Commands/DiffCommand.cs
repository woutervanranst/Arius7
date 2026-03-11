using System.CommandLine;
using Arius.Core.Application.Diff;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Arius.Cli.Commands;

internal static class DiffCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("diff", "Show differences between two snapshots");

        var repoOpt         = new Option<string?>("--repo", "-r")     { Description = "Azure connection string" };
        var containerOpt    = new Option<string?>("--container", "-c") { Description = "Container name" };
        var passwordFileOpt = new Option<string?>("--password-file")   { Description = "Password file path" };
        var snap1Arg        = new Argument<string>("snapshot1")        { Description = "First snapshot ID" };
        var snap2Arg        = new Argument<string>("snapshot2")        { Description = "Second snapshot ID" };

        cmd.Options.Add(repoOpt);
        cmd.Options.Add(containerOpt);
        cmd.Options.Add(passwordFileOpt);
        cmd.Arguments.Add(snap1Arg);
        cmd.Arguments.Add(snap2Arg);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var repo = GlobalOptions.ResolveRepo(parseResult.GetValue(repoOpt));
            if (string.IsNullOrEmpty(repo)) { AnsiConsole.MarkupLine("[red]Error:[/] No --repo specified."); return; }
            var container = parseResult.GetValue(containerOpt) ?? Environment.GetEnvironmentVariable("ARIUS_CONTAINER");
            if (string.IsNullOrEmpty(container)) { AnsiConsole.MarkupLine("[red]Error:[/] No --container specified."); return; }

            var passphrase = GlobalOptions.ResolvePassphrase(parseResult.GetValue(passwordFileOpt));
            var snap1      = parseResult.GetValue(snap1Arg)!;
            var snap2      = parseResult.GetValue(snap2Arg)!;

            var handler = services.GetRequiredService<DiffHandler>();
            var request = new DiffRequest(repo, container, passphrase, snap1, snap2);
            var count   = 0;

            await foreach (var entry in handler.Handle(request, ct))
            {
                count++;
                var (symbol, color) = entry.Status switch
                {
                    DiffStatus.Added       => ("+", "green"),
                    DiffStatus.Removed     => ("-", "red"),
                    DiffStatus.Modified    => ("M", "yellow"),
                    DiffStatus.TypeChanged => ("T", "cyan"),
                    _                      => ("?", "grey")
                };
                AnsiConsole.MarkupLine($"[{color}]{symbol}[/] {Markup.Escape(entry.Path)}");
            }

            if (count == 0)
                AnsiConsole.MarkupLine("[green]No differences.[/]");
        });

        return cmd;
    }
}
