using Arius.Core.Ls;
using Humanizer;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using System.CommandLine;

namespace Arius.Cli.Commands.Ls;

internal static class LsVerb
{
    internal static Command Build(
        Func<string, string, string?, string, IServiceProvider> serviceProviderFactory)
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
            if (resolvedKey is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No account key provided. Use --key / -k or set ARIUS_KEY.");
                return 1;
            }

            CliBuilder.ConfigureAuditLogging(resolvedAccount, container, "ls");
            var recorder = AnsiConsole.Console.CreateRecorder();
            var savedConsole = AnsiConsole.Console;
            AnsiConsole.Console = recorder;

            try
            {
                var services = serviceProviderFactory(resolvedAccount, resolvedKey, passphrase, container);
                var mediator = services.GetRequiredService<IMediator>();

                var opts = new LsOptions
                {
                    Version = version,
                    Prefix  = prefix,
                    Filter  = filter,
                };

                var result = await mediator.Send(new LsCommand(opts), ct);

                if (!result.Success)
                {
                    AnsiConsole.MarkupLine($"[red]Ls failed:[/] {result.ErrorMessage}");
                    return 1;
                }

                var table = new Table();
                table.AddColumn("Path");
                table.AddColumn(new TableColumn("Size").RightAligned());
                table.AddColumn("Created");
                table.AddColumn("Modified");

                foreach (var entry in result.Entries)
                {
                    var size = entry.OriginalSize.HasValue
                        ? entry.OriginalSize.Value.Bytes().Humanize()
                        : "?";
                    table.AddRow(
                        Markup.Escape(entry.RelativePath),
                        size,
                        entry.Created.ToString("yyyy-MM-dd HH:mm"),
                        entry.Modified.ToString("yyyy-MM-dd HH:mm"));
                }

                AnsiConsole.Write(table);
                AnsiConsole.MarkupLine($"[dim]{result.Entries.Count} file(s)[/]");
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
