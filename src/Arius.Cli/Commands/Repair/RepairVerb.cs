using Arius.Core.Features.RepairChunkIndexCommand;
using Arius.Core.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Cli.Commands.Repair;

internal static class RepairVerb
{
    internal static Command Build(
        Func<string, string?, string?, string, PreflightMode, Task<IServiceProvider>> serviceProviderFactory)
    {
        var accountOption    = CliBuilder.AccountOption();
        var keyOption        = CliBuilder.KeyOption();
        var passphraseOption = CliBuilder.PassphraseOption();
        var containerOption  = CliBuilder.ContainerOption();

        var cmd = new Command("repair-index", "Rebuild the chunk index from committed chunks");
        cmd.Options.Add(accountOption);
        cmd.Options.Add(keyOption);
        cmd.Options.Add(passphraseOption);
        cmd.Options.Add(containerOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var account = parseResult.GetValue(accountOption);
            var key = parseResult.GetValue(keyOption);
            var passphrase = parseResult.GetValue(passphraseOption);
            var container = parseResult.GetValue(containerOption)!;

            var resolvedAccount = CliBuilder.ResolveAccount(account);
            if (resolvedAccount is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No account provided. Use --account / -a or set ARIUS_ACCOUNT.");
                return 1;
            }

            var resolvedKey = CliBuilder.ResolveKey(key, resolvedAccount);

            CliBuilder.ConfigureAuditLogging(resolvedAccount, container, "repair-index");
            var recorder = AnsiConsole.Console.CreateRecorder();
            var savedConsole = AnsiConsole.Console;
            AnsiConsole.Console = recorder;

            try
            {
                var services = await serviceProviderFactory(resolvedAccount, resolvedKey, passphrase, container, PreflightMode.ReadWrite).ConfigureAwait(false);
                var mediator = services.GetRequiredService<IMediator>();

                AnsiConsole.MarkupLine("Repairing chunk index from committed chunks...");
                var result = await mediator.Send(new RepairChunkIndexCommand(), ct);
                if (!result.Success)
                {
                    AnsiConsole.MarkupLine($"[red]Repair failed:[/] {Markup.Escape(result.ErrorMessage ?? "Unknown repair failure.")}");
                    AnsiConsole.MarkupLine("Rerun the repair command after fixing the reported problem.");
                    return 1;
                }

                var repair = result.Repair ?? throw new InvalidOperationException("Repair command completed without repair details.");
                AnsiConsole.MarkupLine($"[green]Repair complete.[/] Listed {repair.ListedChunkCount} chunk(s), rebuilt {repair.RebuiltEntryCount} entries across {repair.RebuiltShardCount} shard(s), uploaded {repair.UploadedShardCount}, deleted {repair.DeletedStaleShardCount} stale shard(s).");
                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Repair failed:[/] {Markup.Escape(ex.Message)}");
                AnsiConsole.MarkupLine("Rerun the repair command after fixing the reported problem.");
                return 1;
            }
            finally
            {
                AnsiConsole.Console = savedConsole;
                CliBuilder.FlushAuditLog(recorder);
            }
        });

        return cmd;
    }
}
