using Arius.Core.Features.SnapshotsListQuery;
using Arius.Core.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Cli.Commands.Snapshot;

internal static class SnapshotListVerb
{
    internal static Command Build(
        Func<string, string?, string?, string, PreflightMode, Task<IServiceProvider>> serviceProviderFactory)
    {
        var accountOption    = CliBuilder.AccountOption();
        var keyOption        = CliBuilder.KeyOption();
        var passphraseOption = CliBuilder.PassphraseOption();
        var containerOption  = CliBuilder.ContainerOption();

        var cmd = new Command("list", "List all snapshots, oldest first");
        cmd.Options.Add(accountOption);
        cmd.Options.Add(keyOption);
        cmd.Options.Add(passphraseOption);
        cmd.Options.Add(containerOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var account    = parseResult.GetValue(accountOption);
            var key        = parseResult.GetValue(keyOption);
            var passphrase = parseResult.GetValue(passphraseOption);
            var container  = parseResult.GetValue(containerOption)!;

            var resolvedAccount = CliBuilder.ResolveAccount(account);
            if (resolvedAccount is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No account provided. Use --account / -a or set ARIUS_ACCOUNT.");
                return 1;
            }
            var resolvedKey = CliBuilder.ResolveKey(key, resolvedAccount);

            CliBuilder.ConfigureAuditLogging(resolvedAccount, container, "snapshot-list");

            try
            {
                IServiceProvider services;
                try
                {
                    services = await serviceProviderFactory(resolvedAccount, resolvedKey, passphrase, container, PreflightMode.ReadOnly).ConfigureAwait(false);
                }
                catch (PreflightException ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                    return 1;
                }

                var mediator = services.GetRequiredService<IMediator>();

                var index = 0;
                AnsiConsole.MarkupLine($"[bold]{"#",4}  {"Version",-24}  {"Created",-19}  Files[/]");
                await foreach (var snapshot in mediator.CreateStream(new SnapshotsListQuery(), ct))
                {
                    index++;
                    var created = snapshot.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                    AnsiConsole.MarkupLine($"{index,4}  {Markup.Escape(snapshot.Version),-24}  {created,-19}  {snapshot.FileCount}");
                }

                AnsiConsole.MarkupLine(index == 0 ? "[dim]No snapshots found.[/]" : $"[dim]{index} snapshot(s)[/]");
                return 0;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        });

        return cmd;
    }
}
