using System.CommandLine;
using System.Text.Json;
using Arius.Core.Application.Ls;
using Arius.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Arius.Cli.Commands;

internal static class LsCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("ls", "List files in a snapshot");

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
        var snapshotArg = new Argument<string>("snapshot-id")
        {
            Description = "Snapshot ID (full or prefix)"
        };
        var pathArg = new Argument<string>("path")
        {
            Description = "Sub-path to list (default: /)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var recursiveOpt = new Option<bool>("--recursive", "-R")
        {
            Description = "Recursively list all entries"
        };
        var jsonOpt = new Option<bool>("--json")
        {
            Description = "Output result as JSON"
        };

        cmd.Options.Add(repoOpt);
        cmd.Options.Add(containerOpt);
        cmd.Options.Add(passwordFileOpt);
        cmd.Options.Add(recursiveOpt);
        cmd.Options.Add(jsonOpt);
        cmd.Arguments.Add(snapshotArg);
        cmd.Arguments.Add(pathArg);

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
            var snapshotId  = parseResult.GetValue(snapshotArg)!;
            var subPath     = parseResult.GetValue(pathArg) ?? "/";
            var recursive   = parseResult.GetValue(recursiveOpt);
            var asJson      = parseResult.GetValue(jsonOpt);

            var handler = services.GetRequiredService<LsHandler>();
            var request = new LsRequest(repo, container, passphrase, snapshotId, subPath, recursive);

            if (asJson)
            {
                var entries = new List<object>();
                await foreach (var entry in handler.Handle(request, ct))
                {
                    entries.Add(new
                    {
                        path  = entry.Path,
                        name  = entry.Name,
                        type  = entry.Type.ToString().ToLowerInvariant(),
                        size  = entry.Size,
                        mtime = entry.MTime,
                        mode  = entry.Mode
                    });
                }
                Console.WriteLine(JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
                return;
            }

            var table = new Table();
            table.AddColumn("Mode");
            table.AddColumn("Type");
            table.AddColumn("Size", c => c.RightAligned());
            table.AddColumn("Modified");
            table.AddColumn("Name");

            var count = 0;
            await foreach (var entry in handler.Handle(request, ct))
            {
                count++;
                var typeLabel = entry.Type switch
                {
                    TreeNodeType.Directory => "[blue]dir[/]",
                    TreeNodeType.Symlink   => "[cyan]lnk[/]",
                    _                      => "file"
                };
                table.AddRow(
                    entry.Mode,
                    typeLabel,
                    FormatSize(entry.Size),
                    entry.MTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    Markup.Escape(entry.Name));
            }

            if (count == 0)
                AnsiConsole.MarkupLine("[yellow]No entries found at the specified path.[/]");
            else
                AnsiConsole.Write(table);
        });

        return cmd;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024              => $"{bytes} B",
        < 1024 * 1024       => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _                   => $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
    };
}
