using Arius.Cli.Commands.Restore;
using Arius.Core.Features.RestoreCommand;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Arius.Cli.Tests.Commands.Restore;

/// <summary>
/// Verifies <see cref="RestoreVerb.BuildDisplay"/> renders correctly in
/// its 3-stage layout: Resolved, Checked, Restoring — plus tail lines.
/// </summary>
public class BuildRestoreDisplayTests
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
    public void BuildRestoreDisplay_BeforeAnyEvents_ShowsAllDimCircles()
    {
        var state = new ProgressState();

        var output = RenderToString(RestoreVerb.BuildDisplay(state));

        // Before tree traversal completes, stage 1 shows "Resolving" (not "Resolved")
        output.ShouldContain("Resolving");
        output.ShouldContain("Checked");
        output.ShouldContain("Restoring");
        // All stages should show dim circles (○) — no green bullets yet
        output.ShouldNotContain("●");
    }

    [Test]
    public void BuildRestoreDisplay_InProgress_ShowsStagesAndTailLines()
    {
        var state = new ProgressState();
        state.SetTreeTraversalComplete(10, 7_000_000L);
        state.SnapshotTimestamp = new DateTimeOffset(2026, 3, 28, 14, 0, 0, TimeSpan.Zero);
        state.IncrementDisposition(RestoreDisposition.New);
        state.IncrementDisposition(RestoreDisposition.New);
        state.SetChunkResolution(3, 1, 2);
        state.IncrementFilesRestored(1024L);
        state.AddRestoreEvent("foo/bar.txt", 1024L, skipped: false);
        state.AddRestoreEvent("baz/skip.txt", 512L, skipped: true);

        var output = RenderToString(RestoreVerb.BuildDisplay(state));

        // Stage 1: Resolved should show green bullet + snapshot info
        output.ShouldContain("Resolved");
        output.ShouldContain("2026");  // timestamp
        output.ShouldContain("10");    // file count

        // Stage 2: Checked should show disposition tallies
        output.ShouldContain("Checked");
        output.ShouldContain("2 new");
        output.ShouldContain("0 identical");

        // Stage 3: Restoring with file count (new format: no Restored/Skipped sub-lines)
        output.ShouldContain("Restoring");

        // Tail lines (shown when no active downloads)
        output.ShouldContain("foo/bar.txt");
        output.ShouldContain("baz/skip.txt");
    }

    [Test]
    public void BuildRestoreDisplay_Completed_ShowsGreenBulletsNoTail()
    {
        var state = new ProgressState();
        state.SetTreeTraversalComplete(2, 800L);
        state.SnapshotTimestamp = DateTimeOffset.UtcNow;
        state.IncrementDisposition(RestoreDisposition.New);
        state.IncrementDisposition(RestoreDisposition.New);
        state.SetChunkResolution(1, 1, 0);
        state.IncrementFilesRestored(500L);
        state.IncrementFilesRestored(300L);
        state.AddRestoreEvent("done.txt", 500L, skipped: false);

        var output = RenderToString(RestoreVerb.BuildDisplay(state));

        output.ShouldContain("●");
        output.ShouldContain("Restoring");
        output.ShouldNotContain("done.txt");
    }

    [Test]
    public void BuildRestoreDisplay_ZeroFileRestore_ShowsAllGreenBullets()
    {
        // A snapshot with 0 files — tree traversal completes, no dispositions, no downloads.
        // All stages should show as completed (green ●).
        var state = new ProgressState();
        state.SetTreeTraversalComplete(0, 0L);
        state.SnapshotTimestamp = DateTimeOffset.UtcNow;

        var output = RenderToString(RestoreVerb.BuildDisplay(state));

        // Stage 1: Resolved (green ●)
        output.ShouldContain("Resolved");
        output.ShouldNotContain("Resolving");

        // Stage 3: Restoring should show 0/0 files and be green (●)
        output.ShouldContain("Restoring");
        output.ShouldContain("0/0 files");

        // Count ● occurrences — all 3 stages should be green
        var bulletCount = output.Split('●').Length - 1;
        bulletCount.ShouldBe(3, $"expected 3 green bullets for zero-file restore, got {bulletCount}. Output:\n{output}");
    }
}
