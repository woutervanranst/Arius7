using Arius.Core.Features.SnapshotsListQuery;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Spectre.Console;

namespace Arius.Cli.Tests.Commands.Snapshot;

[NotInParallel("AnsiConsoleRecorder")]
public class SnapshotListVerbTests
{
    [Test]
    public async Task SnapshotList_NumbersOldestToNewestAndReturnsZero()
    {
        var t1 = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var mediator = Substitute.For<IMediator>();
        mediator.CreateStream(Arg.Any<SnapshotsListQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => new[] { new SnapshotInfo("2024-01-01T000000.000Z", t1, 10), new SnapshotInfo("2024-02-01T000000.000Z", t2, 20) }.ToAsyncEnumerable());

        var rootCommand = CliBuilder.BuildRootCommand(serviceProviderFactory: (_, _, _, _, _) =>
        {
            var services = new ServiceCollection();
            services.AddSingleton(mediator);
            return Task.FromResult<IServiceProvider>(services.BuildServiceProvider());
        });

        var (exitCode, output) = await CaptureOutputAsync(() => rootCommand.Parse("snapshot list -a acct -k key -c ctr").InvokeAsync());

        exitCode.ShouldBe(0);
        output.ShouldContain("1");
        output.ShouldContain("2024-01-01T000000.000Z");
        output.ShouldContain("2");
        output.ShouldContain("2024-02-01T000000.000Z");
        output.ShouldContain("2 snapshot(s)");
    }

    [Test]
    public async Task SnapshotList_MissingAccount_ReturnsFailure()
    {
        var rootCommand = CliBuilder.BuildRootCommand(serviceProviderFactory: (_, _, _, _, _) =>
            Task.FromResult<IServiceProvider>(new ServiceCollection().BuildServiceProvider()));

        var (exitCode, output) = await CaptureOutputAsync(() => rootCommand.Parse("snapshot list -k key -c ctr").InvokeAsync());

        exitCode.ShouldBe(1);
        output.ShouldContain("No account provided");
    }

    private static async Task<(int ExitCode, string Output)> CaptureOutputAsync(Func<Task<int>> invokeAsync)
    {
        var recorder = AnsiConsole.Console.CreateRecorder();
        var savedConsole = AnsiConsole.Console;
        AnsiConsole.Console = recorder;
        try { return (await invokeAsync(), recorder.ExportText()); }
        finally { AnsiConsole.Console = savedConsole; }
    }
}
