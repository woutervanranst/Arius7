using System.CommandLine;
using Arius.Core.Application.Stats;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Arius.Cli.Commands;

internal static class StatsCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("stats", "Display repository statistics");

        var repoOpt         = new Option<string?>("--repo", "-r")     { Description = "Azure connection string" };
        var containerOpt    = new Option<string?>("--container", "-c") { Description = "Container name" };
        var passwordFileOpt = new Option<string?>("--password-file")   { Description = "Password file path" };
        var jsonOpt         = new Option<bool>("--json")               { Description = "JSON output" };

        cmd.Options.Add(repoOpt);
        cmd.Options.Add(containerOpt);
        cmd.Options.Add(passwordFileOpt);
        cmd.Options.Add(jsonOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var repo = GlobalOptions.ResolveRepo(parseResult.GetValue(repoOpt));
            if (string.IsNullOrEmpty(repo)) { AnsiConsole.MarkupLine("[red]Error:[/] No --repo specified."); return; }
            var container = parseResult.GetValue(containerOpt) ?? Environment.GetEnvironmentVariable("ARIUS_CONTAINER");
            if (string.IsNullOrEmpty(container)) { AnsiConsole.MarkupLine("[red]Error:[/] No --container specified."); return; }

            var passphrase = GlobalOptions.ResolvePassphrase(parseResult.GetValue(passwordFileOpt));
            var asJson     = parseResult.GetValue(jsonOpt);

            var handler = services.GetRequiredService<StatsHandler>();
            var result  = await handler.Handle(new StatsRequest(repo, container, passphrase), ct);

            if (asJson)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return;
            }

            var table = new Table().Title("Repository Statistics").BorderColor(Color.Grey);
            table.AddColumn("Metric").AddColumn("Value", c => c.RightAligned());

            table.AddRow("Snapshots",          result.SnapshotCount.ToString());
            table.AddRow("Packs",              result.PackCount.ToString());
            table.AddRow("Total stored",       FormatSize(result.TotalPackBytes));
            table.AddRow("Unique blobs",       result.UniqueBlobCount.ToString());
            table.AddRow("Unique data",        FormatSize(result.UniqueBlobBytes));
            table.AddRow("Dedup ratio",        $"{result.DeduplicationRatio:F2}x");

            AnsiConsole.Write(table);
        });

        return cmd;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024               => $"{bytes} B",
        < 1024 * 1024        => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _                    => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };
}
