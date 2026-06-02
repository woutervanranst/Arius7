using System.CommandLine;
using Arius.Core.Features.RepairChunkIndexCommand;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Spectre.Console;

namespace Arius.Cli.Tests.Commands.Repair;

[NotInParallel("AnsiConsoleRecorder")]
public class RepairCommandTests
{
    [Test]
    public async Task RepairIndex_RunsRepairAndReturnsSuccess()
    {
        var mediator = Substitute.For<IMediator>();
        mediator
            .Send(Arg.Any<RepairChunkIndexCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<RepairChunkIndexResult>(new RepairChunkIndexResult
            {
                Success = true,
                Repair = new(1, 1, 1, 1, 0),
            }));
        var rootCommand = CliBuilder.BuildRootCommand(serviceProviderFactory: (_, _, _, _, _) =>
        {
            var services = new ServiceCollection();
            services.AddSingleton(mediator);
            return Task.FromResult<IServiceProvider>(services.BuildServiceProvider());
        });

        var (exitCode, output) = await CaptureOutputAsync(async () => await rootCommand.Parse("repair-index -a acct -k key -c ctr").InvokeAsync());

        exitCode.ShouldBe(0);
        output.ShouldContain("Repairing chunk index from committed chunks...");
        output.ShouldContain("Repair complete. Listed 1 chunk(s), rebuilt 1 entries");
        output.ShouldContain("uploaded 1, deleted 0 stale shard(s).");
        await mediator.Received(1).Send(Arg.Any<RepairChunkIndexCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RepairIndex_HandlerFailure_ReturnsFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator
            .Send(Arg.Any<RepairChunkIndexCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<RepairChunkIndexResult>(new RepairChunkIndexResult { Success = false, ErrorMessage = "repair failed" }));
        var rootCommand = CliBuilder.BuildRootCommand(serviceProviderFactory: (_, _, _, _, _) =>
        {
            var services = new ServiceCollection();
            services.AddSingleton(mediator);
            return Task.FromResult<IServiceProvider>(services.BuildServiceProvider());
        });

        var (exitCode, output) = await CaptureOutputAsync(async () => await rootCommand.Parse("repair-index -a acct -k key -c ctr").InvokeAsync());

        exitCode.ShouldBe(1);
        output.ShouldContain("Repair failed: repair failed");
        output.ShouldContain("Rerun the repair command after fixing the reported problem.");
    }

    [Test]
    public async Task RepairIndex_MissingAccount_ReturnsFailure()
    {
        var rootCommand = CliBuilder.BuildRootCommand(serviceProviderFactory: (_, _, _, _, _) =>
            Task.FromResult<IServiceProvider>(new ServiceCollection().BuildServiceProvider()));

        var (exitCode, output) = await CaptureOutputAsync(async () => await rootCommand.Parse("repair-index -k key -c ctr").InvokeAsync());

        exitCode.ShouldBe(1);
        output.ShouldContain("Error: No account provided. Use --account / -a or set ARIUS_ACCOUNT.");
    }

    private static async Task<(int ExitCode, string Output)> CaptureOutputAsync(Func<Task<int>> invokeAsync)
    {
        var recorder = AnsiConsole.Console.CreateRecorder();
        var savedConsole = AnsiConsole.Console;
        AnsiConsole.Console = recorder;

        try
        {
            var exitCode = await invokeAsync();
            return (exitCode, recorder.ExportText());
        }
        finally
        {
            AnsiConsole.Console = savedConsole;
        }
    }
}
