using Arius.Core.Shared.ChunkIndex;
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
                Log.Information("[repair] Start: account={Account} container={Container}", resolvedAccount, container);
                var services = await serviceProviderFactory(resolvedAccount, resolvedKey, passphrase, container, PreflightMode.ReadWrite).ConfigureAwait(false);
                var index = services.GetRequiredService<ChunkIndexService>();

                AnsiConsole.MarkupLine("Repairing chunk index from committed chunks...");
                Log.Information("[phase] repair-index");
                var result = await index.RepairAsync(ct);
                Log.Information("[repair] Done: listed={ListedChunks} rebuiltEntries={RebuiltEntries} rebuiltShards={RebuiltShards} uploadedShards={UploadedShards} deletedStaleShards={DeletedStaleShards}", result.ListedChunkCount, result.RebuiltEntryCount, result.RebuiltShardCount, result.UploadedShardCount, result.DeletedStaleShardCount);
                AnsiConsole.MarkupLine($"[green]Repair complete.[/] Listed {result.ListedChunkCount} chunk(s), rebuilt {result.RebuiltEntryCount} entries across {result.RebuiltShardCount} shard(s), uploaded {result.UploadedShardCount}, deleted {result.DeletedStaleShardCount} stale shard(s).");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[repair] Failure");
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
