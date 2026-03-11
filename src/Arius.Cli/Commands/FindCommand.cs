using System.CommandLine;
using System.Text.Json;
using Arius.Core.Application.Find;
using Arius.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Arius.Cli.Commands;

internal static class FindCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("find", "Search for files across snapshots");

        var repoOpt = new Option<string?>("--repo", "-r") { Description = "Azure connection string" };
        var containerOpt = new Option<string?>("--container", "-c") { Description = "Container name" };
        var passwordFileOpt = new Option<string?>("--password-file") { Description = "Password file path" };
        var patternArg = new Argument<string>("pattern") { Description = "Glob pattern (e.g. *.pdf)" };
        var snapshotOpt = new Option<string?>("--snapshot", "-s") { Description = "Limit search to snapshot ID" };
        var pathOpt = new Option<string?>("--path") { Description = "Limit to path prefix" };
        var jsonOpt = new Option<bool>("--json") { Description = "JSON output" };

        cmd.Options.Add(repoOpt);
        cmd.Options.Add(containerOpt);
        cmd.Options.Add(passwordFileOpt);
        cmd.Options.Add(snapshotOpt);
        cmd.Options.Add(pathOpt);
        cmd.Options.Add(jsonOpt);
        cmd.Arguments.Add(patternArg);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var repo = GlobalOptions.ResolveRepo(parseResult.GetValue(repoOpt));
            if (string.IsNullOrEmpty(repo)) { AnsiConsole.MarkupLine("[red]Error:[/] No --repo specified."); return; }
            var container = parseResult.GetValue(containerOpt) ?? Environment.GetEnvironmentVariable("ARIUS_CONTAINER");
            if (string.IsNullOrEmpty(container)) { AnsiConsole.MarkupLine("[red]Error:[/] No --container specified."); return; }

            var passphrase = GlobalOptions.ResolvePassphrase(parseResult.GetValue(passwordFileOpt));
            var pattern    = parseResult.GetValue(patternArg)!;
            var snapshot   = parseResult.GetValue(snapshotOpt);
            var pathPrefix = parseResult.GetValue(pathOpt);
            var asJson     = parseResult.GetValue(jsonOpt);

            var handler = services.GetRequiredService<FindHandler>();
            var request = new FindRequest(repo, container, passphrase, pattern, snapshot, pathPrefix);

            if (asJson)
            {
                var results = new List<object>();
                await foreach (var r in handler.Handle(request, ct))
                    results.Add(new { snapshotId = r.SnapshotId, path = r.Path, name = r.Name, type = r.Type.ToString().ToLowerInvariant(), size = r.Size });
                Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }

            var table = new Table();
            table.AddColumn("Snapshot");
            table.AddColumn("Path");
            table.AddColumn("Type");
            table.AddColumn("Size", c => c.RightAligned());

            var count = 0;
            await foreach (var r in handler.Handle(request, ct))
            {
                count++;
                table.AddRow(r.SnapshotId[..8], Markup.Escape(r.Path),
                    r.Type.ToString().ToLowerInvariant(), FormatSize(r.Size));
            }

            if (count == 0) AnsiConsole.MarkupLine("[yellow]No matches found.[/]");
            else AnsiConsole.Write(table);
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
