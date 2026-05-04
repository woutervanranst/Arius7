using Arius.Cli.Tests.TestSupport;
using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.Paths;
using Arius.Core.Shared.Storage;
using NSubstitute;

namespace Arius.Cli.Tests.Commands.Ls;

[NotInParallel("AnsiConsoleRecorder")]
public class ListQueryParsingTests
{
    [Test]
    public async Task ListQuery_MissingContainer_ReturnsExitCode1()
    {
        var rootCommand = CliBuilder.BuildRootCommand(serviceProviderFactory: (_, _, _, _, _) =>
            throw new PreflightException(
                PreflightErrorKind.ContainerNotFound,
                authMode: "key",
                accountName: "acct",
                containerName: "missing"));

        var exitCode = await rootCommand.Parse("ls -a acct -k key -c missing").InvokeAsync();

        exitCode.ShouldBe(1);
    }

    [Test]
    public async Task ListQuery_AllFilters_ParsedCorrectly()
    {
        var harness = new CliHarness();
        var exitCode = await harness.InvokeAsync("ls -a acct -k key -c ctr -v 2026-01-01 --prefix docs/ -f .pdf");

        exitCode.ShouldBe(0);

        var call = harness.ListQueryHandler.ReceivedCalls().Single();
        var cmd = (ListQuery)call.GetArguments()[0]!;
        cmd.Options.Version.ShouldBe("2026-01-01");
        cmd.Options.Prefix.ShouldBe(PathOf("docs"));
        cmd.Options.Filter.ShouldBe(".pdf");
    }

    [Test]
    public async Task ListQuery_Defaults_Applied()
    {
        var harness = new CliHarness();
        var exitCode = await harness.InvokeAsync("ls -a acct -k key -c ctr");

        exitCode.ShouldBe(0);

        var call = harness.ListQueryHandler.ReceivedCalls().Single();
        var cmd = (ListQuery)call.GetArguments()[0]!;
        cmd.Options.Version.ShouldBeNull();
        cmd.Options.Prefix.ShouldBeNull();
        cmd.Options.Filter.ShouldBeNull();
    }

    [Test]
    public async Task ListQuery_MockHandlerCaptures_PrefixAndFilter()
    {
        var harness = new CliHarness();
        var exitCode = await harness.InvokeAsync("ls -a acct -k key -c ctr --prefix photos/ -f .jpg");

        exitCode.ShouldBe(0);

        var call = harness.ListQueryHandler.ReceivedCalls().Single();
        var cmd = (ListQuery)call.GetArguments()[0]!;
        cmd.Options.Prefix.ShouldBe(PathOf("photos"));
        cmd.Options.Filter.ShouldBe(".jpg");
    }
}
