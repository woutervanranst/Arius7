using System.CommandLine;
using System.Text.Json;
using Arius.Core.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Arius.Cli.Commands;

/// <summary>
/// cat command — dumps raw internal objects as JSON.
/// Usage: cat (config | snapshot &lt;id&gt; | tree &lt;hash&gt; | key &lt;id&gt;)
/// </summary>
internal static class CatCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("cat", "Print raw internal repository objects as JSON");

        cmd.Subcommands.Add(BuildConfigSub(services));
        cmd.Subcommands.Add(BuildSnapshotSub(services));
        cmd.Subcommands.Add(BuildTreeSub(services));

        return cmd;
    }

    // ── config ────────────────────────────────────────────────────────────────

    private static Command BuildConfigSub(IServiceProvider services)
    {
        var sub = new Command("config", "Print repository config");
        AddCommonOpts(sub, out var repoOpt, out var containerOpt, out var passwordFileOpt);

        sub.SetAction(async (parseResult, ct) =>
        {
            if (!TryResolve(parseResult, repoOpt, containerOpt, passwordFileOpt, out var repo, out var container, out var pass)) return;
            var azureRepo = GetRepo(services, repo!, container!);
            _ = await azureRepo.UnlockAsync(pass!, ct);
            var config = await azureRepo.LoadConfigAsync(ct);
            Console.WriteLine(JsonSerializer.Serialize(config, PrettyOptions));
        });
        return sub;
    }

    // ── snapshot ──────────────────────────────────────────────────────────────

    private static Command BuildSnapshotSub(IServiceProvider services)
    {
        var sub     = new Command("snapshot", "Print snapshot document");
        var snapArg = new Argument<string>("snapshot-id") { Description = "Snapshot ID or prefix" };
        AddCommonOpts(sub, out var repoOpt, out var containerOpt, out var passwordFileOpt);
        sub.Arguments.Add(snapArg);

        sub.SetAction(async (parseResult, ct) =>
        {
            if (!TryResolve(parseResult, repoOpt, containerOpt, passwordFileOpt, out var repo, out var container, out var pass)) return;
            var id        = parseResult.GetValue(snapArg)!;
            var azureRepo = GetRepo(services, repo!, container!);
            _ = await azureRepo.UnlockAsync(pass!, ct);
            var doc = await azureRepo.LoadSnapshotDocumentAsync(id, ct);
            Console.WriteLine(JsonSerializer.Serialize(doc, PrettyOptions));
        });
        return sub;
    }

    // ── tree ──────────────────────────────────────────────────────────────────

    private static Command BuildTreeSub(IServiceProvider services)
    {
        var sub      = new Command("tree", "Print tree blob");
        var hashArg  = new Argument<string>("hash") { Description = "Tree hash" };
        AddCommonOpts(sub, out var repoOpt, out var containerOpt, out var passwordFileOpt);
        sub.Arguments.Add(hashArg);

        sub.SetAction(async (parseResult, ct) =>
        {
            if (!TryResolve(parseResult, repoOpt, containerOpt, passwordFileOpt, out var repo, out var container, out var pass)) return;
            var hash      = parseResult.GetValue(hashArg)!;
            var azureRepo = GetRepo(services, repo!, container!);
            _ = await azureRepo.UnlockAsync(pass!, ct);
            var nodes = await azureRepo.ReadTreeAsync(new Arius.Core.Models.TreeHash(hash), ct);
            Console.WriteLine(JsonSerializer.Serialize(nodes, PrettyOptions));
        });
        return sub;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AddCommonOpts(
        Command cmd,
        out Option<string?> repoOpt,
        out Option<string?> containerOpt,
        out Option<string?> passwordFileOpt)
    {
        repoOpt         = new Option<string?>("--repo", "-r")     { Description = "Azure connection string" };
        containerOpt    = new Option<string?>("--container", "-c") { Description = "Container name" };
        passwordFileOpt = new Option<string?>("--password-file")   { Description = "Password file path" };
        cmd.Options.Add(repoOpt);
        cmd.Options.Add(containerOpt);
        cmd.Options.Add(passwordFileOpt);
    }

    private static bool TryResolve(
        System.CommandLine.ParseResult parseResult,
        Option<string?> repoOpt,
        Option<string?> containerOpt,
        Option<string?> passwordFileOpt,
        out string? repo, out string? container, out string? pass)
    {
        repo      = GlobalOptions.ResolveRepo(parseResult.GetValue(repoOpt));
        container = parseResult.GetValue(containerOpt) ?? Environment.GetEnvironmentVariable("ARIUS_CONTAINER");
        pass      = GlobalOptions.ResolvePassphrase(parseResult.GetValue(passwordFileOpt));

        if (string.IsNullOrEmpty(repo))      { AnsiConsole.MarkupLine("[red]Error:[/] No --repo."); return false; }
        if (string.IsNullOrEmpty(container)) { AnsiConsole.MarkupLine("[red]Error:[/] No --container."); return false; }
        return true;
    }

    private static AzureRepository GetRepo(IServiceProvider services, string repo, string container)
    {
        var factory = services.GetRequiredService<Func<string, string, AzureRepository>>();
        return factory(repo, container);
    }

    private static readonly JsonSerializerOptions PrettyOptions =
        new() { WriteIndented = true };
}
