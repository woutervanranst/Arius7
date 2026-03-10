using System.CommandLine;
using Arius.Cli.Commands;
using Arius.Core.Application.Backup;
using Arius.Core.Application.Init;
using Arius.Core.Application.Restore;
using Arius.Core.Application.Snapshots;
using Microsoft.Extensions.DependencyInjection;

// Build DI container
var services = new ServiceCollection()
    .AddSingleton<InitHandler>()
    .AddSingleton<BackupHandler>()
    .AddSingleton<RestoreHandler>()
    .AddSingleton<SnapshotsHandler>()
    .BuildServiceProvider();

// Build root command
var rootCommand = new RootCommand("Arius — deduplicated encrypted backup for Azure Blob Storage");

rootCommand.Subcommands.Add(InitCommand.Build(services));
rootCommand.Subcommands.Add(BackupCommand.Build(services));
rootCommand.Subcommands.Add(RestoreCommand.Build(services));
rootCommand.Subcommands.Add(SnapshotsCommand.Build(services));

return await rootCommand.Parse(args).InvokeAsync();
