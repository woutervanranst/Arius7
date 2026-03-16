using System.CommandLine;
using System.Text.Json;
using Arius.Core.Application.Snapshots;
using Humanizer;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Arius.Cli.Commands;

internal static class SnapshotsCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("snapshots", "List available snapshots");

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

        cmd.Options.Add(repoOpt);
        cmd.Options.Add(containerOpt);
        cmd.Options.Add(passwordFileOpt);
        cmd.Options.Add(jsonOpt);

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

            var passphrase = GlobalOptions.ResolvePassphrase(parseResult.GetValue(passwordFileOpt));
            var asJson = parseResult.GetValue(jsonOpt);

            var handler = services.GetRequiredService<SnapshotsHandler>();
            var request = new ListSnapshotsRequest(repo, container, passphrase);

            if (asJson)
            {
                var snapshots = new System.Collections.Generic.List<object>();
                await foreach (var snapshot in handler.Handle(request, ct))
                {
                    snapshots.Add(new
                    {
                        id = snapshot.Id.Value,
                        time = snapshot.Time,
                        paths = snapshot.Paths,
                        hostname = snapshot.Hostname,
                        username = snapshot.Username,
                        tags = snapshot.Tags
                    });
                }
                Console.WriteLine(JsonSerializer.Serialize(snapshots, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }

            var table = new Table();
            table.AddColumn("ID");
            table.AddColumn("Time");
            table.AddColumn("Age");
            table.AddColumn("Paths");
            table.AddColumn("Host");
            table.AddColumn("Tags");

            await foreach (var snapshot in handler.Handle(request, ct))
            {
                var localTime = snapshot.Time.ToLocalTime();
                table.AddRow(
                    snapshot.Id.Value[..8],
                    localTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    snapshot.Time.Humanize(),
                    string.Join(", ", snapshot.Paths),
                    snapshot.Hostname,
                    string.Join(", ", snapshot.Tags));
            }

            if (table.Rows.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No snapshots found.[/]");
            }
            else
            {
                AnsiConsole.Write(table);
            }
        });

        return cmd;
    }
}
