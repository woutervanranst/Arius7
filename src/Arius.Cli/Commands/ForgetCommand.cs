using System.CommandLine;
using Arius.Core.Application.Forget;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Arius.Cli.Commands;

internal static class ForgetCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("forget", "Remove snapshots by retention policy");

        var repoOpt         = new Option<string?>("--repo", "-r")      { Description = "Azure connection string" };
        var containerOpt    = new Option<string?>("--container", "-c")  { Description = "Container name" };
        var passwordFileOpt = new Option<string?>("--password-file")    { Description = "Password file path" };
        var keepLastOpt     = new Option<int?>("--keep-last")           { Description = "Keep N most recent snapshots" };
        var keepHourlyOpt   = new Option<int?>("--keep-hourly")         { Description = "Keep N hourly snapshots" };
        var keepDailyOpt    = new Option<int?>("--keep-daily")          { Description = "Keep N daily snapshots" };
        var keepWeeklyOpt   = new Option<int?>("--keep-weekly")         { Description = "Keep N weekly snapshots" };
        var keepMonthlyOpt  = new Option<int?>("--keep-monthly")        { Description = "Keep N monthly snapshots" };
        var keepYearlyOpt   = new Option<int?>("--keep-yearly")         { Description = "Keep N yearly snapshots" };
        var keepWithinOpt   = new Option<string?>("--keep-within")      { Description = "Keep snapshots within duration (e.g. 30d)" };
        var keepTagOpt      = new Option<string[]?>("--keep-tag")       { Description = "Keep snapshots with tag(s)", Arity = ArgumentArity.ZeroOrMore };
        var dryRunOpt       = new Option<bool>("--dry-run")             { Description = "Show what would be removed without removing" };
        var yesOpt          = new Option<bool>("--yes", "-y")           { Description = "Skip confirmation prompt" };

        foreach (var o in new Option[] { repoOpt, containerOpt, passwordFileOpt, keepLastOpt,
            keepHourlyOpt, keepDailyOpt, keepWeeklyOpt, keepMonthlyOpt, keepYearlyOpt,
            keepWithinOpt, keepTagOpt, dryRunOpt, yesOpt })
            cmd.Options.Add(o);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var repo = GlobalOptions.ResolveRepo(parseResult.GetValue(repoOpt));
            if (string.IsNullOrEmpty(repo)) { AnsiConsole.MarkupLine("[red]Error:[/] No --repo specified."); return; }
            var container = parseResult.GetValue(containerOpt) ?? Environment.GetEnvironmentVariable("ARIUS_CONTAINER");
            if (string.IsNullOrEmpty(container)) { AnsiConsole.MarkupLine("[red]Error:[/] No --container specified."); return; }

            var passphrase = GlobalOptions.ResolvePassphrase(parseResult.GetValue(passwordFileOpt));
            var dryRun     = parseResult.GetValue(dryRunOpt);
            var yes        = parseResult.GetValue(yesOpt);

            var policy = new RetentionPolicy(
                KeepLast:    parseResult.GetValue(keepLastOpt),
                KeepHourly:  parseResult.GetValue(keepHourlyOpt),
                KeepDaily:   parseResult.GetValue(keepDailyOpt),
                KeepWeekly:  parseResult.GetValue(keepWeeklyOpt),
                KeepMonthly: parseResult.GetValue(keepMonthlyOpt),
                KeepYearly:  parseResult.GetValue(keepYearlyOpt),
                KeepWithin:  parseResult.GetValue(keepWithinOpt),
                KeepTags:    parseResult.GetValue(keepTagOpt));

            var handler = services.GetRequiredService<ForgetHandler>();
            var request = new ForgetRequest(repo, container, passphrase, policy, DryRun: true); // preview first

            // Show preview
            var toRemove = new List<string>();
            await foreach (var ev in handler.Handle(request, ct))
            {
                var color = ev.Decision == ForgetDecision.Keep ? "green" : "red";
                var label = ev.Decision == ForgetDecision.Keep ? "keep  " : "remove";
                AnsiConsole.MarkupLine($"[{color}]{label}[/] {ev.SnapshotId[..8]}  {ev.SnapshotTime:yyyy-MM-dd HH:mm}  {ev.Reason}");
                if (ev.Decision == ForgetDecision.Remove)
                    toRemove.Add(ev.SnapshotId);
            }

            if (toRemove.Count == 0)
            {
                AnsiConsole.MarkupLine("[green]Nothing to remove.[/]");
                return;
            }

            if (!dryRun)
            {
                if (!yes && !AnsiConsole.Confirm($"Remove {toRemove.Count} snapshot(s)?"))
                    return;

                // Execute for real
                var realRequest = request with { DryRun = false };
                await foreach (var ev in handler.Handle(realRequest, ct))
                {
                    if (ev.Decision == ForgetDecision.Remove)
                        AnsiConsole.MarkupLine($"[red]Removed[/] {ev.SnapshotId[..8]}");
                }
                AnsiConsole.MarkupLine($"[green]Done.[/] Removed {toRemove.Count} snapshot(s).");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Dry run — no changes made.[/]");
            }
        });

        return cmd;
    }
}
