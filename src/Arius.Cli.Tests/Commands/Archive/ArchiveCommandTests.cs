using Arius.Cli.Tests.TestSupport;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.Paths;
using Arius.Core.Shared.Storage;
using NSubstitute;

namespace Arius.Cli.Tests.Commands.Archive;

[NotInParallel("AnsiConsoleRecorder")]
public class ArchiveCommandTests
{
    [Test]
    public async Task Archive_AllOptions_ParsedCorrectly()
    {
        var harness = new CliHarness();
        var exitCode = await harness.InvokeAsync("archive /data -a acct -k key -c ctr -t Hot --remove-local");

        exitCode.ShouldBe(0);

        var call = harness.ArchiveHandler.ReceivedCalls().Single();
        var cmd = (ArchiveCommand)call.GetArguments()[0]!;
        cmd.CommandOptions.UploadTier.ShouldBe(BlobTier.Hot);
        cmd.CommandOptions.RemoveLocal.ShouldBeTrue();
        cmd.CommandOptions.NoPointers.ShouldBeFalse();
    }

    [Test]
    public async Task Archive_Defaults_Applied()
    {
        var harness = new CliHarness();
        var exitCode = await harness.InvokeAsync("archive /data -a acct -k key -c ctr");

        exitCode.ShouldBe(0);

        var call = harness.ArchiveHandler.ReceivedCalls().Single();
        var cmd = (ArchiveCommand)call.GetArguments()[0]!;
        cmd.CommandOptions.UploadTier.ShouldBe(BlobTier.Archive);
        cmd.CommandOptions.RemoveLocal.ShouldBeFalse();
        cmd.CommandOptions.NoPointers.ShouldBeFalse();
    }

    [Test]
    public async Task Archive_RemoveLocalPlusNoPointers_ReturnsExitCode1()
    {
        var harness = new CliHarness();
        var exitCode = await harness.InvokeAsync("archive /data -a acct -k key -c ctr --remove-local --no-pointers");

        exitCode.ShouldBe(1);
        harness.ArchiveHandler.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task Archive_MockHandlerCapturesPath()
    {
        var harness = new CliHarness();
        var exitCode = await harness.InvokeAsync("archive /tmp -a acct -k key -c ctr");

        exitCode.ShouldBe(0);

        var call = harness.ArchiveHandler.ReceivedCalls().Single();
        var cmd = (ArchiveCommand)call.GetArguments()[0]!;
        cmd.CommandOptions.RootDirectory.ShouldBe(LocalRootPath.Parse(Path.GetFullPath("/tmp")));
    }
}
