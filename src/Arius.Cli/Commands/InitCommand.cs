using System.CommandLine;
using System.Text.Json;
using Arius.Core.Application.Init;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Arius.Cli.Commands;

internal static class InitCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("init", "Initialize a new Arius repository");

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
        var packSizeOpt = new Option<long>("--pack-size")
        {
            Description = "Pack size in bytes (default: 10 MB)",
            DefaultValueFactory = _ => 10L * 1024 * 1024
        };

        cmd.Options.Add(repoOpt);
        cmd.Options.Add(passwordFileOpt);
        cmd.Options.Add(jsonOpt);
        cmd.Options.Add(packSizeOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var repo = GlobalOptions.ResolveRepo(parseResult.GetValue(repoOpt));
            if (string.IsNullOrEmpty(repo))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No repository path specified. Use --repo or set ARIUS_REPOSITORY.");
                return;
            }

            var passphrase = GlobalOptions.ResolvePassphrase(parseResult.GetValue(passwordFileOpt));
            var packSize = parseResult.GetValue(packSizeOpt);
            var asJson = parseResult.GetValue(jsonOpt);

            var handler = services.GetRequiredService<InitHandler>();
            var result = await handler.Handle(new InitRequest(repo, passphrase, packSize), ct);

            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    repoId = result.RepoId.Value,
                    configPath = result.ConfigPath,
                    keyPath = result.KeyPath
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                AnsiConsole.MarkupLine($"[green]Repository initialized![/]");
                AnsiConsole.MarkupLine($"  Repo ID : [cyan]{result.RepoId.Value}[/]");
                AnsiConsole.MarkupLine($"  Config  : [dim]{result.ConfigPath}[/]");
                AnsiConsole.MarkupLine($"  Key     : [dim]{result.KeyPath}[/]");
            }
        });

        return cmd;
    }
}
