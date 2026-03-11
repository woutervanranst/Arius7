using System.CommandLine;
using Arius.Azure;
using Arius.Cli.Commands;
using Arius.Core.Application.Backup;
using Arius.Core.Application.Check;
using Arius.Core.Application.CostEstimate;
using Arius.Core.Application.Diff;
using Arius.Core.Application.Find;
using Arius.Core.Application.Forget;
using Arius.Core.Application.Init;
using Arius.Core.Application.Key;
using Arius.Core.Application.Ls;
using Arius.Core.Application.Prune;
using Arius.Core.Application.Repair;
using Arius.Core.Application.Restore;
using Arius.Core.Application.Snapshots;
using Arius.Core.Application.Stats;
using Arius.Core.Application.Tag;
using Arius.Core.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ─── Configuration ────────────────────────────────────────────────────────────
// Sources (highest → lowest priority): environment variables, user secrets.
// User secrets are only loaded when the project assembly is present (i.e. dev builds);
// they are never present in a published/release binary.

var config = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddUserSecrets<Program>(optional: true)
    .Build();

GlobalOptions.Configuration = config;

// Build DI container
var services = new ServiceCollection()
    // Factory: (connectionString, containerName) → AzureRepository
    .AddSingleton<Func<string, string, AzureRepository>>(
        _ => (connStr, container) =>
            new AzureRepository(new AzureBlobStorageProvider(connStr, container)))
    .AddSingleton<InitHandler>()
    .AddSingleton<BackupHandler>()
    .AddSingleton<RestoreHandler>()
    .AddSingleton<SnapshotsHandler>()
    .AddSingleton<LsHandler>()
    .AddSingleton<FindHandler>()
    .AddSingleton<ForgetHandler>()
    .AddSingleton<PruneHandler>()
    .AddSingleton<CheckHandler>()
    .AddSingleton<DiffHandler>()
    .AddSingleton<StatsHandler>()
    .AddSingleton<TagHandler>()
    .AddSingleton<KeyHandler>()
    .AddSingleton<RepairHandler>()
    .AddSingleton<CostEstimateHandler>()
    .BuildServiceProvider();

// Build root command
var rootCommand = new RootCommand("Arius — deduplicated encrypted backup for Azure Blob Storage");

rootCommand.Subcommands.Add(InitCommand.Build(services));
rootCommand.Subcommands.Add(BackupCommand.Build(services));
rootCommand.Subcommands.Add(RestoreCommand.Build(services));
rootCommand.Subcommands.Add(SnapshotsCommand.Build(services));
rootCommand.Subcommands.Add(LsCommand.Build(services));
rootCommand.Subcommands.Add(FindCommand.Build(services));
rootCommand.Subcommands.Add(ForgetCommand.Build(services));
rootCommand.Subcommands.Add(PruneCommand.Build(services));
rootCommand.Subcommands.Add(CheckCommand.Build(services));
rootCommand.Subcommands.Add(DiffCommand.Build(services));
rootCommand.Subcommands.Add(StatsCommand.Build(services));
rootCommand.Subcommands.Add(TagCommand.Build(services));
rootCommand.Subcommands.Add(KeyCommand.Build(services));
rootCommand.Subcommands.Add(RepairCommand.Build(services));
rootCommand.Subcommands.Add(VersionCommand.Build());
rootCommand.Subcommands.Add(CatCommand.Build(services));

return await rootCommand.Parse(args).InvokeAsync();
