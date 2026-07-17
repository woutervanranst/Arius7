using System.CommandLine;
using Arius.Core.Features.SnapshotDiffQuery;
using Arius.Core.Features.SnapshotsListQuery;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Spectre.Console;

namespace Arius.Cli.Tests.Commands.Snapshot;

[NotInParallel("AnsiConsoleRecorder")]
public class SnapshotDiffVerbTests
{
    [Test]
    public async Task SnapshotDiff_IndexArguments_ResolveAgainstListAndRenderEntries()
    {
        var t1 = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var mediator = Substitute.For<IMediator>();
        mediator.CreateStream(Arg.Any<SnapshotsListQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => new[] { new SnapshotInfo("2024-01-01T000000.000Z", t1, 1), new SnapshotInfo("2024-02-01T000000.000Z", t2, 2) }.ToAsyncEnumerable());
        mediator.CreateStream(Arg.Any<SnapshotDiffQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => new[]
            {
                new SnapshotDiffEntry(ChangeType.Added, RelativePath.Parse("new.txt"), null, MakeFile("new.txt")),
                new SnapshotDiffEntry(ChangeType.Removed, RelativePath.Parse("gone.txt"), MakeFile("gone.txt"), null),
            }.ToAsyncEnumerable());

        var rootCommand = BuildRoot(mediator);
        var (exitCode, output) = await CaptureOutputAsync(() => rootCommand.Parse("snapshot diff 1 2 -a acct -k key -c ctr").InvokeAsync());

        exitCode.ShouldBe(0);
        output.ShouldContain("new.txt");
        output.ShouldContain("gone.txt");
        output.ShouldContain("1 added");
        output.ShouldContain("1 removed");
        mediator.Received(1).CreateStream(
            Arg.Is<SnapshotDiffQuery>(q => q.VersionA == "2024-01-01T000000.000Z" && q.VersionB == "2024-02-01T000000.000Z"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SnapshotDiff_TimestampArguments_StripColonsAndDoNotFetchList()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.CreateStream(Arg.Any<SnapshotDiffQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => AsyncEnumerable.Empty<SnapshotDiffEntry>());

        var rootCommand = BuildRoot(mediator);
        var (exitCode, _) = await CaptureOutputAsync(() => rootCommand.Parse("snapshot diff 2024-01-01T00:00:00 2024-02-01T00:00:00 -a acct -k key -c ctr").InvokeAsync());

        exitCode.ShouldBe(0);
        mediator.Received(1).CreateStream(
            Arg.Is<SnapshotDiffQuery>(q => q.VersionA == "2024-01-01T000000" && q.VersionB == "2024-02-01T000000"),
            Arg.Any<CancellationToken>());
        mediator.DidNotReceive().CreateStream(Arg.Any<SnapshotsListQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SnapshotDiff_SnapshotNotFound_ReturnsFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.CreateStream(Arg.Any<SnapshotDiffQuery>(), Arg.Any<CancellationToken>())
            .Returns<IAsyncEnumerable<SnapshotDiffEntry>>(_ => throw new InvalidOperationException("Snapshot not found: 'nope'."));

        var rootCommand = BuildRoot(mediator);
        var (exitCode, output) = await CaptureOutputAsync(() => rootCommand.Parse("snapshot diff nope other -a acct -k key -c ctr").InvokeAsync());

        exitCode.ShouldBe(1);
        output.ShouldContain("Snapshot not found");
    }

    [Test]
    public async Task SnapshotDiff_RepositoryEncryptionError_ReturnsFriendlyErrorNotCrash()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.CreateStream(Arg.Any<SnapshotDiffQuery>(), Arg.Any<CancellationToken>())
            .Returns(FailsOnEnumeration<SnapshotDiffEntry>(new RepositoryEncryptionException(passphraseProvided: false, new InvalidDataException("Unrecognized compression format"))));

        var rootCommand = BuildRoot(mediator);
        // Timestamp-prefix args take the no-list-fetch path; the diff query itself raises the encryption error.
        var (exitCode, output) = await CaptureOutputAsync(() => rootCommand.Parse("snapshot diff 2024-01 2024-02 -a acct -k key -c ctr").InvokeAsync());

        exitCode.ShouldBe(1);
        output.ShouldContain("passphrase");
        output.ShouldNotContain("Unhandled exception");
    }

    // A stream that faults on first enumeration — faithful to the handler's async-iterator failure
    // (the exception surfaces inside the verb's `await foreach`, not at the CreateStream call).
    private static async IAsyncEnumerable<T> FailsOnEnumeration<T>(Exception ex)
    {
        await Task.CompletedTask;
        if (ex is not null) throw ex;
        yield break;
    }

    private static FileEntry MakeFile(string name) => new()
    {
        Name = PathSegment.Parse(name), ContentHash = FakeContentHash('a'),
        Created = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Modified = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static RootCommand BuildRoot(IMediator mediator) =>
        CliBuilder.BuildRootCommand(serviceProviderFactory: (_, _, _, _, _) =>
        {
            var services = new ServiceCollection();
            services.AddSingleton(mediator);
            return Task.FromResult<IServiceProvider>(services.BuildServiceProvider());
        });

    private static async Task<(int ExitCode, string Output)> CaptureOutputAsync(Func<Task<int>> invokeAsync)
    {
        var recorder = AnsiConsole.Console.CreateRecorder();
        var savedConsole = AnsiConsole.Console;
        AnsiConsole.Console = recorder;
        try { return (await invokeAsync(), recorder.ExportText()); }
        finally { AnsiConsole.Console = savedConsole; }
    }
}
