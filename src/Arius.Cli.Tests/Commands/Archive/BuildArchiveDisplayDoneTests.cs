using Arius.Cli.Commands.Archive;
using Shouldly;
using Spectre.Console;

namespace Arius.Cli.Tests;

/// <summary>
/// Verifies that files removed from <see cref="ProgressState.TrackedFiles"/> do not
/// appear in the display (Done state = removed from dictionary).
/// </summary>
public class BuildArchiveDisplayDoneTests
{
    [Test]
    public void BuildArchiveDisplay_DoesNotShowRemovedFiles()
    {
        var state = new ProgressState();
        state.AddFile("completed.bin", 1000);
        state.SetFileHashed("completed.bin", "done1");
        state.RemoveFile("completed.bin");

        var writer  = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi        = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out         = new AnsiConsoleOutput(writer),
        });
        console.Write(ArchiveVerb.BuildDisplay(state));
        var output = writer.ToString();

        output.ShouldNotContain("completed.bin");
    }
}
