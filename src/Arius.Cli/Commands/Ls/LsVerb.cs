using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;

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

        var localPathArg = new Argument<string?>("path")
        {
            Description = "Local directory to overlay (shows local presence per file)",
            Arity       = ArgumentArity.ZeroOrOne,
        };
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
        cmd.Arguments.Add(localPathArg);
        cmd.Options.Add(lsVersionOption);
        cmd.Options.Add(prefixOption);
        cmd.Options.Add(filterOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var account    = parseResult.GetValue(accountOption);
            var key        = parseResult.GetValue(keyOption);
            var passphrase = parseResult.GetValue(passphraseOption);
            var container  = parseResult.GetValue(containerOption)!;
            var localPath  = parseResult.GetValue(localPathArg);
            var version    = parseResult.GetValue(lsVersionOption);
            var prefix     = parseResult.GetValue(prefixOption);
            var filter     = parseResult.GetValue(filterOption);

            if (localPath is not null && !Directory.Exists(localPath))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Local directory not found: {Markup.Escape(localPath)}");
                return 1;
            }

            var resolvedAccount = CliBuilder.ResolveAccount(account);
            if (resolvedAccount is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No account provided. Use --account / -a or set ARIUS_ACCOUNT.");
                return 1;
            }

            var resolvedKey = CliBuilder.ResolveKey(key, resolvedAccount);

            // No console recorder here: ls streams entries and must stay bounded in memory,
            // so the listing itself is not captured into the audit log — only the summary is.
            CliBuilder.ConfigureAuditLogging(resolvedAccount, container, "ls");

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

                RelativePath? parsedPrefix = null;
                if (prefix is not null)
                {
                    try
                    {
                        var normalizedPrefix = prefix.TrimEnd('/', '\\');
                        parsedPrefix = normalizedPrefix.Length == 0
                            ? RelativePath.Root
                            : RelativePath.Parse(normalizedPrefix);
                    }
                    catch (FormatException ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Error:[/] Invalid --prefix value: {Markup.Escape(ex.Message)}");
                        return 1;
                    }
                }

                var opts = new ListQueryOptions
                {
                    Version   = version,
                    Prefix    = parsedPrefix,
                    Filter    = filter,
                    LocalPath = localPath,
                };

                // Rows are written as they stream in (no table materialization) so the listing
                // is responsive and memory-bounded even for repositories with millions of entries.
                var fileCount = 0;
                try
                {
                    AnsiConsole.MarkupLine($"[bold]State  {"Size",12}  {"Created",-16}  {"Modified",-16}  Path[/]");
                    AnsiConsole.MarkupLine("[dim]P=local pointer  B=local binary  R=in repository  H=hydrated  A=archived  ~=rehydrating  ?=tier unknown[/]");

                    await foreach (var entry in mediator.CreateStream(new ListQuery(opts), ct))
                    {
                        if (entry is not RepositoryFileEntry file)
                        {
                            continue;
                        }

                        var size = file.OriginalSize.HasValue
                            ? file.OriginalSize.Value.Bytes().Humanize()
                            : "?";
                        var created  = file.Created?.ToString("yyyy-MM-dd HH:mm") ?? "-";
                        var modified = file.Modified?.ToString("yyyy-MM-dd HH:mm") ?? "-";
                        AnsiConsole.MarkupLine(
                            $"{LsStateFormatter.ToMarkup(file.State)}   {Markup.Escape(size),12}  {created,-16}  {modified,-16}  {Markup.Escape(file.RelativePath.ToString())}");
                        fileCount++;
                    }
                }
                catch (RepositoryEncryptionException ex)
                {
                    Log.Error(ex, "ls failed: repository passphrase/encryption mismatch");
                    AnsiConsole.MarkupLine(CliBuilder.FormatRepositoryEncryptionError(ex));
                    return 1;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "ls failed");
                    AnsiConsole.MarkupLine($"[red]Ls failed:[/] {Markup.Escape(ex.Message)}");
                    return 1;
                }

                AnsiConsole.MarkupLine($"[dim]{fileCount} file(s)[/]");
                Log.Information("ls completed: {FileCount} file(s)", fileCount);
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
