using System.CommandLine;
using Arius.Core.Application.Check;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Arius.Cli.Commands;

internal static class CheckCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("check", "Verify repository integrity");

        var repoOpt         = new Option<string?>("--repo", "-r")     { Description = "Azure connection string" };
        var containerOpt    = new Option<string?>("--container", "-c") { Description = "Container name" };
        var passwordFileOpt = new Option<string?>("--password-file")   { Description = "Password file path" };
        var readDataOpt     = new Option<bool>("--read-data")          { Description = "Also verify data pack contents (requires rehydration)" };

        cmd.Options.Add(repoOpt);
        cmd.Options.Add(containerOpt);
        cmd.Options.Add(passwordFileOpt);
        cmd.Options.Add(readDataOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var repo = GlobalOptions.ResolveRepo(parseResult.GetValue(repoOpt));
            if (string.IsNullOrEmpty(repo)) { AnsiConsole.MarkupLine("[red]Error:[/] No --repo specified."); return; }
            var container = parseResult.GetValue(containerOpt) ?? Environment.GetEnvironmentVariable("ARIUS_CONTAINER");
            if (string.IsNullOrEmpty(container)) { AnsiConsole.MarkupLine("[red]Error:[/] No --container specified."); return; }

            var passphrase = GlobalOptions.ResolvePassphrase(parseResult.GetValue(passwordFileOpt));
            var readData   = parseResult.GetValue(readDataOpt);

            if (readData)
                AnsiConsole.MarkupLine("[yellow]Warning:[/] --read-data will rehydrate archive-tier packs (incurs cost).");

            var handler = services.GetRequiredService<CheckHandler>();
            var request = new CheckRequest(repo, container, passphrase, readData);
            var hasErrors = false;

            await foreach (var result in handler.Handle(request, ct))
            {
                var color = result.Severity switch
                {
                    CheckSeverity.Error   => "red",
                    CheckSeverity.Warning => "yellow",
                    _                     => "grey"
                };
                AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(result.Message)}[/]");
                if (result.Severity == CheckSeverity.Error) hasErrors = true;
            }

            if (hasErrors)
                AnsiConsole.MarkupLine("[red]Check failed.[/]");
            else
                AnsiConsole.MarkupLine("[green]Check passed.[/]");
        });

        return cmd;
    }
}
