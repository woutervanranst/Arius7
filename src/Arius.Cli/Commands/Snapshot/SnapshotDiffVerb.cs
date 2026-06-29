using Arius.Core.Features.SnapshotDiffQuery;
using Arius.Core.Features.SnapshotsListQuery;
using Arius.Core.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Cli.Commands.Snapshot;

internal static class SnapshotDiffVerb
{
    internal static Command Build(
        Func<string, string?, string?, string, PreflightMode, Task<IServiceProvider>> serviceProviderFactory)
    {
        var accountOption    = CliBuilder.AccountOption();
        var keyOption        = CliBuilder.KeyOption();
        var passphraseOption = CliBuilder.PassphraseOption();
        var containerOption  = CliBuilder.ContainerOption();

        var fromArg = new Argument<string>("from") { Description = "Older snapshot: an index from `snapshot list`, or a version/timestamp prefix" };
        var toArg   = new Argument<string>("to")   { Description = "Newer snapshot: an index from `snapshot list`, or a version/timestamp prefix" };

        var cmd = new Command("diff", "Show what changed between two snapshots");
        cmd.Options.Add(accountOption);
        cmd.Options.Add(keyOption);
        cmd.Options.Add(passphraseOption);
        cmd.Options.Add(containerOption);
        cmd.Arguments.Add(fromArg);
        cmd.Arguments.Add(toArg);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var account    = parseResult.GetValue(accountOption);
            var key        = parseResult.GetValue(keyOption);
            var passphrase = parseResult.GetValue(passphraseOption);
            var container  = parseResult.GetValue(containerOption)!;
            var fromValue  = parseResult.GetValue(fromArg)!;
            var toValue    = parseResult.GetValue(toArg)!;

            var resolvedAccount = CliBuilder.ResolveAccount(account);
            if (resolvedAccount is null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No account provided. Use --account / -a or set ARIUS_ACCOUNT.");
                return 1;
            }
            var resolvedKey = CliBuilder.ResolveKey(key, resolvedAccount);

            CliBuilder.ConfigureAuditLogging(resolvedAccount, container, "snapshot-diff");

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
                    Log.Error(ex, "Preflight check failed");
                    return 1;
                }

                var mediator = services.GetRequiredService<IMediator>();

                string fromVersion, toVersion;
                try
                {
                    // Only fetch the snapshot list when an index argument is actually used.
                    IReadOnlyList<SnapshotInfo> snapshots = (IsIndex(fromValue) || IsIndex(toValue))
                        ? await mediator.CreateStream(new SnapshotsListQuery(), ct).ToListAsync(ct)
                        : [];
                    fromVersion = SnapshotArgumentResolver.Resolve(fromValue, snapshots);
                    toVersion   = SnapshotArgumentResolver.Resolve(toValue, snapshots);
                }
                catch (ArgumentException ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                    Log.Error(ex, "snapshot diff argument resolution failed");
                    return 1;
                }

                var counts = new Dictionary<ChangeType, int>();
                try
                {
                    await foreach (var entry in mediator.CreateStream(new SnapshotDiffQuery(fromVersion, toVersion), ct))
                    {
                        counts[entry.Change] = counts.GetValueOrDefault(entry.Change) + 1;
                        AnsiConsole.MarkupLine($"{Glyph(entry.Change)}  {Markup.Escape(entry.Path.ToString())}");
                    }
                }
                catch (InvalidOperationException ex)
                {
                    AnsiConsole.MarkupLine($"[red]Diff failed:[/] {Markup.Escape(ex.Message)}");
                    Log.Error(ex, "snapshot diff failed");
                    return 1;
                }

                AnsiConsole.MarkupLine(
                    $"[dim]{counts.GetValueOrDefault(ChangeType.Added)} added, " +
                    $"{counts.GetValueOrDefault(ChangeType.Removed)} removed, " +
                    $"{counts.GetValueOrDefault(ChangeType.Modified)} modified, " +
                    $"{counts.GetValueOrDefault(ChangeType.TimestampChanged)} timestamp-only[/]");
                Log.Information("snapshot diff completed: {Added} added, {Removed} removed, {Modified} modified, {TimestampChanged} timestamp-only",
                    counts.GetValueOrDefault(ChangeType.Added),
                    counts.GetValueOrDefault(ChangeType.Removed),
                    counts.GetValueOrDefault(ChangeType.Modified),
                    counts.GetValueOrDefault(ChangeType.TimestampChanged));
                return 0;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        });

        return cmd;

        static bool IsIndex(string arg) => int.TryParse(arg, out _);

        static string Glyph(ChangeType change) => change switch
        {
            ChangeType.Added            => "[green]A[/]",
            ChangeType.Removed          => "[red]D[/]",
            ChangeType.Modified         => "[yellow]M[/]",
            ChangeType.TimestampChanged => "[blue]T[/]",
            _                           => "?",
        };
    }
}
