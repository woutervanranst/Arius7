using Arius.Cli.Commands.Restore;
using Arius.Core.Features.RestoreCommand;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Arius.Cli.Tests.Commands.Restore;

/// <summary>
/// Verifies <see cref="RestoreVerb.BuildDisplay"/> renders the aggregate progress bar
/// with dual byte counters (compressed download + original).
/// </summary>
public class BuildRestoreDisplayAggregateProgressTests
{
    private static string RenderToString(IRenderable renderable)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi        = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out         = new AnsiConsoleOutput(writer),
        });
        console.Write(renderable);
        return writer.ToString();
    }

    [Test]
    public void BuildRestoreDisplay_DuringDownloads_ShowsAggregateProgressBar()
    {
        var state = new ProgressState();
        state.SetTreeTraversalComplete(847, 14_200_000_000L);
        state.SnapshotTimestamp = DateTimeOffset.UtcNow;
        state.IncrementDisposition(RestoreDisposition.New);
        state.SetChunkResolution(10, 5, 5);
        state.SetRestoreTotalCompressedBytes(8_310_000_000);
        state.AddRestoreBytesDownloaded(3_170_000_000);

        var output = RenderToString(RestoreVerb.BuildDisplay(state));

        // Should show progress bar
        output.ShouldContain("█");
        output.ShouldContain("░");

        // Should show "download" label
        output.ShouldContain("download");

        // Should show "original" label
        output.ShouldContain("original");
    }

    [Test]
    public void BuildRestoreDisplay_AllComplete_ShowsFullProgressBar()
    {
        var state = new ProgressState();
        state.SetRestoreTotalFiles(5);
        state.SetTreeTraversalComplete(5, 10_000_000L);
        state.SnapshotTimestamp = DateTimeOffset.UtcNow;
        state.SetChunkResolution(2, 1, 1);
        state.SetRestoreTotalCompressedBytes(5_000_000);
        state.AddRestoreBytesDownloaded(5_000_000);
        state.IncrementFilesRestored(4_000_000L);
        state.IncrementFilesRestored(3_000_000L);
        state.IncrementFilesRestored(1_000_000L);
        state.IncrementFilesRestored(1_000_000L);
        state.IncrementFilesRestored(1_000_000L);

        var output = RenderToString(RestoreVerb.BuildDisplay(state));

        // Restoring line should show 100%
        output.ShouldContain("100%");
        // Should have green bullet for completion
        output.ShouldContain("●");
    }

    [Test]
    public void BuildRestoreDisplay_RestoringLine_UseTwoLineLayout()
    {
        // Worst case: large GB values with 4-digit file counts
        var state = new ProgressState();
        state.SetRestoreTotalFiles(4612);
        state.SetTreeTraversalComplete(4612, 5_260_000_000L);
        state.SnapshotTimestamp = DateTimeOffset.UtcNow;
        state.IncrementDisposition(RestoreDisposition.New);
        state.SetChunkResolution(100, 50, 50);
        state.SetRestoreTotalCompressedBytes(4_920_000_000);
        state.AddRestoreBytesDownloaded(10_000_000);
        state.IncrementFilesRestored(100_000L);

        var output = RenderToString(RestoreVerb.BuildDisplay(state));
        var allLines = output.Split('\n');

        // Line 1: progress bar line with file count, bar, and percentage — no byte counters
        var progressLine = allLines.First(l => l.Contains("Restoring"));
        progressLine.Length.ShouldBeLessThanOrEqualTo(80,
            $"Progress line is {progressLine.Length} chars: [{progressLine}]");
        progressLine.ShouldContain("░");
        progressLine.ShouldContain("0%");
        progressLine.ShouldNotContain("download");
        progressLine.ShouldNotContain("original");

        // Line 2: byte counters on a separate indented line
        var byteLine = allLines.First(l => l.Contains("download"));
        byteLine.Length.ShouldBeLessThanOrEqualTo(80,
            $"Byte counter line is {byteLine.Length} chars: [{byteLine}]");
        byteLine.ShouldContain("download");
        byteLine.ShouldContain("original");
    }
}
