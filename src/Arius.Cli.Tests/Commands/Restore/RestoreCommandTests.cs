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

    [Test]
    public async Task Restore_NoTarget_RestoresFullSnapshot()
    {
        var harness = new CliHarness();
        var exitCode = await harness.InvokeAsync("restore /data -a acct -k key -c ctr");

        exitCode.ShouldBe(0);
        ReceivedOptions(harness).TargetPaths.ShouldBeEmpty();
    }

    [Test]
    public async Task Restore_RepeatedTargetOption_ParsedIntoTargetPaths()
    {
        var harness = new CliHarness();
        var exitCode = await harness.InvokeAsync("restore /data -a acct -k key -c ctr -t photos --target \"docs/Invoice 1,2.pdf\"");

        exitCode.ShouldBe(0);
        ReceivedOptions(harness).TargetPaths.ShouldBe([RelativePath.Parse("photos"), RelativePath.Parse("docs/Invoice 1,2.pdf")]);
    }

    [Test]
    public async Task Restore_TargetOptionWithSeveralValues_ParsedIntoTargetPaths()
    {
        var harness = new CliHarness();
        var exitCode = await harness.InvokeAsync("restore /data -a acct -k key -c ctr -t photos/ docs/a.pdf.pointer.arius");

        exitCode.ShouldBe(0);
        ReceivedOptions(harness).TargetPaths.ShouldBe([RelativePath.Parse("photos"), RelativePath.Parse("docs/a.pdf.pointer.arius")]);
    }

    [Test]
    public async Task Restore_TargetWithBackslashSeparators_ParsedIntoTargetPaths()
    {
        var harness = new CliHarness();
        var exitCode = await harness.InvokeAsync(@"restore /data -a acct -k key -c ctr -t photos\2024\ docs\a.pdf");

        exitCode.ShouldBe(0);
        ReceivedOptions(harness).TargetPaths.ShouldBe([RelativePath.Parse("photos/2024"), RelativePath.Parse("docs/a.pdf")]);
    }

    [Test]
    public async Task Restore_InvalidTarget_ReturnsExitCode1WithoutRestoring()
    {
        var harness = new CliHarness();
        var exitCode = await harness.InvokeAsync("restore /data -a acct -k key -c ctr -t /absolute/path");

        exitCode.ShouldBe(1);
        harness.RestoreHandler.ReceivedCalls().ShouldBeEmpty();
    }

    static RestoreOptions ReceivedOptions(CliHarness harness) =>
        ((RestoreCommand)harness.RestoreHandler.ReceivedCalls().Single().GetArguments()[0]!).Options;
}
