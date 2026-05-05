using Arius.Cli.Tests.TestSupport;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Storage;
using NSubstitute;

namespace Arius.Cli.Tests.Commands.Restore;

[NotInParallel("AnsiConsoleRecorder")]
public class RestoreCommandTests
{
    [Test]
    public async Task Restore_MissingContainer_ReturnsExitCode1()
    {
        var rootCommand = CliBuilder.BuildRootCommand(serviceProviderFactory: (_, _, _, _, _) =>
            throw new PreflightException(
                PreflightErrorKind.ContainerNotFound,
                authMode: "key",
                accountName: "acct",
                containerName: "missing"));

        var exitCode = await rootCommand.Parse("restore /data -a acct -k key -c missing").InvokeAsync();

        exitCode.ShouldBe(1);
    }

    [Test]
    public async Task Restore_WithVersion_ParsedCorrectly()
    {
        var harness = new CliHarness();
        var exitCode = await harness.InvokeAsync("restore /data -a acct -k key -c ctr -v 2026-03-21T140000.000Z");

        exitCode.ShouldBe(0);

        var call = harness.RestoreHandler.ReceivedCalls().Single();
        var cmd = (RestoreCommand)call.GetArguments()[0]!;
        cmd.Options.Version.ShouldBe("2026-03-21T140000.000Z");
    }

    [Test]
    public async Task Restore_Defaults_Applied()
    {
        var harness = new CliHarness();
        var exitCode = await harness.InvokeAsync("restore /data -a acct -k key -c ctr");

        exitCode.ShouldBe(0);

        var call = harness.RestoreHandler.ReceivedCalls().Single();
        var cmd = (RestoreCommand)call.GetArguments()[0]!;
        cmd.Options.RootDirectory.ShouldBe(RootOf(Path.GetFullPath("/data")));
        cmd.Options.Version.ShouldBeNull();
        cmd.Options.NoPointers.ShouldBeFalse();
        cmd.Options.Overwrite.ShouldBeFalse();
    }

    [Test]
    public async Task Restore_LongVersionAlias_ParsedCorrectly()
    {
        var harness = new CliHarness();
        var exitCode = await harness.InvokeAsync("restore /data -a acct -k key -c ctr --version 2026-01-01");

        exitCode.ShouldBe(0);

        var call = harness.RestoreHandler.ReceivedCalls().Single();
        var cmd = (RestoreCommand)call.GetArguments()[0]!;
        cmd.Options.Version.ShouldBe("2026-01-01");
    }

    [Test]
    public async Task Restore_OverwriteAndNoPointers_ParsedCorrectly()
    {
        var harness = new CliHarness();
        var exitCode = await harness.InvokeAsync("restore /data -a acct -k key -c ctr --overwrite --no-pointers");

        exitCode.ShouldBe(0);

        var call = harness.RestoreHandler.ReceivedCalls().Single();
        var cmd = (RestoreCommand)call.GetArguments()[0]!;
        cmd.Options.Overwrite.ShouldBeTrue();
        cmd.Options.NoPointers.ShouldBeTrue();
    }
}
