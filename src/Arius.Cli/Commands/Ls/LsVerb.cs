using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.Storage;
using Humanizer;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Spectre.Console;
using System.CommandLine;

namespace Arius.Cli.Commands.Ls;

internal static class LsVerb
{
    internal static Command Build(
        Func<string, string?, string?, string, PreflightMode, Task<IServiceProvider>> serviceProviderFactory)
    {
        var accountOption    = CliBuilder.AccountOption();
        var keyOption        = CliBuilder.KeyOption();
        var passphraseOption = CliBuilder.PassphraseOption();
        var containerOption  = CliBuilder.ContainerOption();

        var lsVersionOption = new Option<string?>("-v", "--version")
        {
            Description = "Snapshot version (partial timestamp, default latest)",
        };
        var prefixOption = new Option<string?>("--prefix")
        {
            Description = "Path prefix filter",
        };
        var filterOption = new Option<string?>("--filter", "-f")
        {
            Description = "Filename substring filter (case-insensitive)",
        };

        var cmd = new Command("ls", "List files in a snapshot");
        cmd.Options.Add(accountOption);
        cmd.Options.Add(keyOption);
        cmd.Options.Add(passphraseOption);
        cmd.Options.Add(containerOption);
        cmd.Options.Add(lsVersionOption);
        cmd.Options.Add(prefixOption);
        cmd.Options.Add(filterOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var account    = parseResult.GetValue(accountOption);
            var key        = parseResult.GetValue(keyOption);
            var passphrase = parseResult.GetValue(passphraseOption);
            var container  = parseResult.GetValue(containerOption)!;
            var version    = parseResult.GetValue(lsVersionOption);
            var prefix     = parseResult.GetValue(prefixOption);
            var filter     = parseResult.GetValue(filterOption);

            var resolvedAccount = CliBuilder.ResolveAccount(account);
            if (resolvedAccount is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No account provided. Use --account / -a or set ARIUS_ACCOUNT.");
                return 1;
            }

            var resolvedKey = CliBuilder.ResolveKey(key, resolvedAccount);

            CliBuilder.ConfigureAuditLogging(resolvedAccount, container, "ls");
            var recorder = AnsiConsole.Console.CreateRecorder();
            var savedConsole = AnsiConsole.Console;
            AnsiConsole.Console = recorder;

            try
            {
                IServiceProvider services;
                try
                {
                    services = await serviceProviderFactory(resolvedAccount, resolvedKey, passphrase, container, PreflightMode.ReadOnly).ConfigureAwait(false);
                }
                catch (PreflightException ex)
                {
                    Log.Error(ex, "Preflight check failed");
                    var msg = ex.ErrorKind switch
                    {
                        PreflightErrorKind.ContainerNotFound =>
                            $"Container [bold]{Markup.Escape(ex.ContainerName)}[/] not found on storage account [bold]{Markup.Escape(ex.AccountName)}[/].",
                        PreflightErrorKind.AccessDenied when ex.AuthMode == "key" =>
                            $"Access denied. Verify the account key is correct for storage account [bold]{Markup.Escape(ex.AccountName)}[/].",
                        PreflightErrorKind.AccessDenied =>
                            $"Authenticated via Azure CLI but access was denied on storage account [bold]{Markup.Escape(ex.AccountName)}[/].\n\n" +
                            $"Assign the required RBAC role:\n" +
                            $"  Storage Blob Data Reader\n\n" +
                            $"  az role assignment create --assignee <your-email> --role \"Storage Blob Data Reader\" " +
                            $"--scope /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Storage/storageAccounts/{Markup.Escape(ex.AccountName)}",
                        PreflightErrorKind.CredentialUnavailable =>
                            $"No account key found and Azure CLI is not logged in.\n\n" +
                            $"Provide a key via:\n" +
                            $"  --key / -k\n" +
                            $"  ARIUS_KEY environment variable\n" +
                            $"  dotnet user-secrets\n\n" +
                            $"Or log in via Azure CLI:\n" +
                            $"  az login",
                        _ =>
                            $"Could not connect to storage account [bold]{Markup.Escape(ex.AccountName)}[/]: {Markup.Escape(ex.InnerException?.Message ?? ex.Message)}",
                    };
                    AnsiConsole.MarkupLine($"[red]Error:[/] {msg}");
                    return 1;
                }

                var mediator = services.GetRequiredService<IMediator>();

                var opts = new ListQueryOptions
                {
                    Version = version,
                    Prefix  = prefix,
                    Filter  = filter,
                };

                var table = new Table();
                table.AddColumn("Path");
                table.AddColumn(new TableColumn("Size").RightAligned());
                table.AddColumn("Created");
                table.AddColumn("Modified");

                var fileCount = 0;
                try
                {
                    await foreach (var entry in mediator.CreateStream(new ListQuery(opts), ct))
                    {
                        if (entry is not RepositoryFileEntry file)
                        {
                            continue;
                        }

                        var size = file.OriginalSize.HasValue
                            ? file.OriginalSize.Value.Bytes().Humanize()
                            : "?";
                        table.AddRow(
                            Markup.Escape(file.RelativePath),
                            size,
                            file.Created?.ToString("yyyy-MM-dd HH:mm") ?? "-",
                            file.Modified?.ToString("yyyy-MM-dd HH:mm") ?? "-");
                        fileCount++;
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Ls failed:[/] {Markup.Escape(ex.Message)}");
                    return 1;
                }

                AnsiConsole.Write(table);
                AnsiConsole.MarkupLine($"[dim]{fileCount} file(s)[/]");
                return 0;
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
