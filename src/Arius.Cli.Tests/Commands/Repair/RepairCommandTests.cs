using System.CommandLine;
using Arius.Core.Features.RepairChunkIndexCommand;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Arius.Cli.Tests.Commands.Repair;

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

        var exitCode = await rootCommand.Parse("repair-index -a acct -k key -c ctr").InvokeAsync();

        exitCode.ShouldBe(0);
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

        var exitCode = await rootCommand.Parse("repair-index -a acct -k key -c ctr").InvokeAsync();

        exitCode.ShouldBe(1);
    }

    [Test]
    public async Task RepairIndex_MissingAccount_ReturnsFailure()
    {
        var rootCommand = CliBuilder.BuildRootCommand(serviceProviderFactory: (_, _, _, _, _) =>
            Task.FromResult<IServiceProvider>(new ServiceCollection().BuildServiceProvider()));

        var exitCode = await rootCommand.Parse("repair-index -k key -c ctr").InvokeAsync();

        exitCode.ShouldBe(1);
    }
}
