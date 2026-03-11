using System.CommandLine;
using Arius.Core.Application.Repair;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Arius.Cli.Commands;

internal static class RepairCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("repair", "Repair repository metadata");

        cmd.Subcommands.Add(BuildSub("index",     services, RepairTarget.Index));
        cmd.Subcommands.Add(BuildSub("snapshots", services, RepairTarget.Snapshots));

        return cmd;
    }

    private static Command BuildSub(string name, IServiceProvider services, RepairTarget target)
    {
        var sub = new Command(name, $"Repair {name}");

        var repoOpt         = new Option<string?>("--repo", "-r")     { Description = "Azure connection string" };
        var containerOpt    = new Option<string?>("--container", "-c") { Description = "Container name" };
        var passwordFileOpt = new Option<string?>("--password-file")   { Description = "Password file path" };

        sub.Options.Add(repoOpt);
        sub.Options.Add(containerOpt);
        sub.Options.Add(passwordFileOpt);

        sub.SetAction(async (parseResult, ct) =>
        {
            var repo = GlobalOptions.ResolveRepo(parseResult.GetValue(repoOpt));
            if (string.IsNullOrEmpty(repo)) { AnsiConsole.MarkupLine("[red]Error:[/] No --repo specified."); return; }
            var container = parseResult.GetValue(containerOpt) ?? Environment.GetEnvironmentVariable("ARIUS_CONTAINER");
            if (string.IsNullOrEmpty(container)) { AnsiConsole.MarkupLine("[red]Error:[/] No --container specified."); return; }

            var passphrase = GlobalOptions.ResolvePassphrase(parseResult.GetValue(passwordFileOpt));
            var handler    = services.GetRequiredService<RepairHandler>();
            var result     = await handler.Handle(
                new RepairRequest(repo, container, passphrase, target), ct);

            AnsiConsole.MarkupLine(result.Success
                ? $"[green]{Markup.Escape(result.Message)}[/]"
                : $"[red]{Markup.Escape(result.Message)}[/]");
        });

        return sub;
    }
}
