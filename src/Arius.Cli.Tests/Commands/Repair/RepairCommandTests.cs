using System.CommandLine;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Tests.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Cli.Tests.Commands.Repair;

public class RepairCommandTests
{
    [Test]
    public async Task RepairIndex_RunsRepairAndReturnsSuccess()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var rootCommand = CliBuilder.BuildRootCommand(serviceProviderFactory: (_, _, _, _, _) =>
        {
            var services = new ServiceCollection();
            services.AddSingleton(new ChunkIndexService(blobs, new PlaintextPassthroughService(), $"acct-repair-{Guid.NewGuid():N}", "ctr"));
            return Task.FromResult<IServiceProvider>(services.BuildServiceProvider());
        });

        var exitCode = await rootCommand.Parse("repair-index -a acct -k key -c ctr").InvokeAsync();

        exitCode.ShouldBe(0);
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
