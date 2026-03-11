using System.CommandLine;
using Arius.Core.Application.Key;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Arius.Cli.Commands;

internal static class KeyCommand
{
    public static Command Build(IServiceProvider services)
    {
        var cmd = new Command("key", "Manage repository keys");

        cmd.Subcommands.Add(BuildSub("list",   services, KeyOperation.List));
        cmd.Subcommands.Add(BuildSub("add",    services, KeyOperation.Add));
        cmd.Subcommands.Add(BuildSub("remove", services, KeyOperation.Remove));
        cmd.Subcommands.Add(BuildSub("passwd", services, KeyOperation.ChangePassword));

        return cmd;
    }

    private static Command BuildSub(string name, IServiceProvider services, KeyOperation operation)
    {
        var sub = new Command(name, $"{char.ToUpper(name[0])}{name[1..]} a repository key");

        var repoOpt         = new Option<string?>("--repo", "-r")     { Description = "Azure connection string" };
        var containerOpt    = new Option<string?>("--container", "-c") { Description = "Container name" };
        var passwordFileOpt = new Option<string?>("--password-file")   { Description = "Password file path" };
        var newPasswordOpt  = new Option<string?>("--new-password")    { Description = "New passphrase (for add/passwd)" };
        var keyIdOpt        = new Option<string?>("--key-id")          { Description = "Key ID (for remove/passwd)" };

        sub.Options.Add(repoOpt);
        sub.Options.Add(containerOpt);
        sub.Options.Add(passwordFileOpt);
        if (operation != KeyOperation.List) sub.Options.Add(newPasswordOpt);
        if (operation is KeyOperation.Remove or KeyOperation.ChangePassword) sub.Options.Add(keyIdOpt);

        sub.SetAction(async (parseResult, ct) =>
        {
            var repo = GlobalOptions.ResolveRepo(parseResult.GetValue(repoOpt));
            if (string.IsNullOrEmpty(repo)) { AnsiConsole.MarkupLine("[red]Error:[/] No --repo specified."); return; }
            var container = parseResult.GetValue(containerOpt) ?? Environment.GetEnvironmentVariable("ARIUS_CONTAINER");
            if (string.IsNullOrEmpty(container)) { AnsiConsole.MarkupLine("[red]Error:[/] No --container specified."); return; }

            var passphrase    = GlobalOptions.ResolvePassphrase(parseResult.GetValue(passwordFileOpt));
            var newPassword   = parseResult.GetValue(newPasswordOpt);
            var keyId         = parseResult.GetValue(keyIdOpt);

            var handler = services.GetRequiredService<KeyHandler>();
            var result  = await handler.Handle(
                new KeyRequest(repo, container, passphrase, operation, newPassword, keyId), ct);

            AnsiConsole.MarkupLine(result.Success
                ? $"[green]{Markup.Escape(result.Message)}[/]"
                : $"[red]{Markup.Escape(result.Message)}[/]");

            if (operation == KeyOperation.List && result.Keys.Count > 0)
                foreach (var k in result.Keys)
                    AnsiConsole.MarkupLine($"  {k}");
        });

        return sub;
    }
}
