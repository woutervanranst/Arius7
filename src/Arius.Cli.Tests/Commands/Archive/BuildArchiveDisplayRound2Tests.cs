using Arius.Cli.Commands.Archive;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Arius.Cli.Tests.Commands.Archive;

/// <summary>
/// Verifies <see cref="ArchiveVerb.BuildDisplay"/> uses ●/○ symbols,
/// full relative path truncation, and a size column.
/// </summary>
public class BuildArchiveDisplayRound2Tests
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
    public void BuildArchiveDisplay_UsesFilledCircle_WhenScanningComplete()
    {
        var state = new ProgressState();
        state.SetScanComplete(3, 3000L);

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        output.ShouldContain("●");
    }

    [Test]
    public void BuildArchiveDisplay_UsesOpenCircle_WhenScanningInProgress()
    {
        var state  = new ProgressState();   // ScanComplete not set
        var output = RenderToString(ArchiveVerb.BuildDisplay(state));

        output.ShouldContain("○");
    }

    [Test]
    public void BuildArchiveDisplay_ShowsRelativePath_NotJustFilename()
    {
        var state = new ProgressState();
        state.AddFile("some/deep/path/file.bin", 1024);

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        output.ShouldContain("some/deep/path/file.bin");
    }

    [Test]
    public void BuildArchiveDisplay_TruncatesLongRelativePath_WithEllipsisPrefix()
    {
        var longPath = "a/very/long/directory/structure/with/file.bin"; // > 30 chars
        var state = new ProgressState();
        state.AddFile(longPath, 2048);

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        output.ShouldContain("...");
        output.ShouldNotContain(longPath);
    }

    [Test]
    public void BuildArchiveDisplay_ShowsSize_ForHashingState()
    {
        var state = new ProgressState();
        state.AddFile("doc.pdf", 5_000_000);

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        output.ShouldContain("MB");
    }
}
