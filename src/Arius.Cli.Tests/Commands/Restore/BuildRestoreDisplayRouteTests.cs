using Arius.Cli.Commands.Restore;
using Arius.Core.Features.RestoreCommand;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Arius.Cli.Tests.Commands.Restore;

public class BuildRestoreDisplayRouteTests
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
    public void BuildRestoreDisplay_RouteTallies_ShowAllFourCategories()
    {
        var state = new ProgressState();
        state.SetTreeTraversalComplete(4, 4000L);
        state.SnapshotTimestamp = DateTimeOffset.UtcNow;
        state.IncrementRoute(RestoreRoute.New);
        state.IncrementRoute(RestoreRoute.SkipIdentical);
        state.IncrementRoute(RestoreRoute.Overwrite);
        state.IncrementRoute(RestoreRoute.KeepLocalDiffers);

        var output = RenderToString(RestoreVerb.BuildDisplay(state));

        output.ShouldContain("1 new");
        output.ShouldContain("1 identical");
        output.ShouldContain("1 overwrite");
        output.ShouldContain("1 kept");
    }
}
