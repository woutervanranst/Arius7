using Arius.Cli.Commands.Restore;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Arius.Cli.Tests;

/// <summary>
/// Verifies <see cref="RestoreVerb.BuildDisplay"/> renders the Resolving phase
/// with <see cref="ProgressState.RestoreFilesDiscovered"/> during tree traversal.
/// </summary>
public class BuildRestoreDisplayResolvingTests
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
    public void BuildRestoreDisplay_DuringTraversal_ShowsResolvingWithFileCount()
    {
        var state = new ProgressState();
        state.SetRestoreFilesDiscovered(523);

        var output = RenderToString(RestoreVerb.BuildDisplay(state));

        output.ShouldContain("Resolving");
        output.ShouldContain("523");
        output.ShouldContain("files");
    }

    [Test]
    public void BuildRestoreDisplay_AfterTraversal_ShowsResolvedNotResolving()
    {
        var state = new ProgressState();
        state.SetRestoreFilesDiscovered(1247);
        state.SetTreeTraversalComplete(1247, 14_200_000_000L);
        state.SnapshotTimestamp = new DateTimeOffset(2026, 3, 28, 14, 0, 0, TimeSpan.Zero);

        var output = RenderToString(RestoreVerb.BuildDisplay(state));

        output.ShouldContain("Resolved");
        output.ShouldContain(1247.ToString("N0"));
        output.ShouldNotContain("Resolving");
    }
}
