using Arius.Cli.Commands.Restore;
using Arius.Core.Features.RestoreCommand;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Arius.Cli.Tests.Commands.Restore;

/// <summary>
/// Verifies <see cref="RestoreVerb.BuildDisplay"/> renders active download table
/// with per-item progress bars for <see cref="TrackedDownload"/> entries.
/// </summary>
public class BuildRestoreDisplayActiveDownloadsTests
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
    public void BuildRestoreDisplay_WithActiveDownloads_ShowsDownloadTable()
    {
        var state = new ProgressState();
        state.SetTreeTraversalComplete(10, 100_000_000L);
        state.SnapshotTimestamp = DateTimeOffset.UtcNow;
        state.SetChunkResolution(5, 3, 2);
        state.SetRestoreTotalCompressedBytes(50_000_000);

        // Add a large file download in progress (key is RelativePath for large files)
        var td1 = new TrackedDownload("photos/sunset.jpg", DownloadKind.LargeFile, "photos/sunset.jpg", 25_400_000, 50_000_000);
        td1.SetBytesDownloaded(18_300_000);
        state.TrackedDownloads.TryAdd("photos/sunset.jpg", td1);

        // Add a tar bundle download in progress (key is chunk hash for tar bundles)
        var td2 = new TrackedDownload("chunk2", DownloadKind.TarBundle, "TAR bundle (3 files, 847 KB)", 15_200_000, 847_000);
        td2.SetBytesDownloaded(4_800_000);
        state.TrackedDownloads.TryAdd("chunk2", td2);

        var output = RenderToString(RestoreVerb.BuildDisplay(state));

        // Should show file names / labels
        output.ShouldContain("sunset.jpg");
        output.ShouldContain("TAR bundle");

        // Should show progress bar characters
        output.ShouldContain("█");
        output.ShouldContain("░");
    }

    [Test]
    public void BuildRestoreDisplay_NoActiveDownloads_NoDownloadTable()
    {
        var state = new ProgressState();
        state.SetTreeTraversalComplete(2, 800L);
        state.SnapshotTimestamp = DateTimeOffset.UtcNow;
        state.SetChunkResolution(1, 1, 0);
        state.SetRestoreTotalCompressedBytes(500);
        state.IncrementFilesRestored(400L);
        state.IncrementFilesRestored(400L);
        // No TrackedDownloads

        var output = RenderToString(RestoreVerb.BuildDisplay(state));
        var downloadLines = output.Split('\n')
            .Where(l => (l.Contains("█") || l.Contains("░")) && !l.Contains("Restoring"))
            .ToArray();

        // No progress-bar rows should be rendered for active downloads.
        downloadLines.ShouldBeEmpty();
        output.ShouldNotContain("TAR bundle");
    }

    [Test]
    public void BuildRestoreDisplay_ActiveDownloads_ColumnsAreAligned()
    {
        var state = new ProgressState();
        state.SetTreeTraversalComplete(10, 100_000_000L);
        state.SnapshotTimestamp = DateTimeOffset.UtcNow;
        state.SetChunkResolution(5, 3, 2);
        state.SetRestoreTotalCompressedBytes(50_000_000);

        // Two downloads with different-length names and sizes
        var td1 = new TrackedDownload("photos/sunset.jpg", DownloadKind.LargeFile, "photos/sunset.jpg", 25_400_000, 50_000_000);
        td1.SetBytesDownloaded(18_300_000);
        state.TrackedDownloads.TryAdd("photos/sunset.jpg", td1);

        var td2 = new TrackedDownload("chunk2", DownloadKind.TarBundle, "TAR bundle (3 files, 847 KB)", 15_200_000, 847_000);
        td2.SetBytesDownloaded(4_800_000);
        state.TrackedDownloads.TryAdd("chunk2", td2);

        var output = RenderToString(RestoreVerb.BuildDisplay(state));
        var dlLines = output.Split('\n')
            .Where(l => l.Contains("█") || l.Contains("░"))
            .Where(l => !l.Contains("Restoring"))
            .ToArray();

        dlLines.Length.ShouldBe(2, $"Expected 2 download rows, got:\n{string.Join('\n', dlLines)}");

        // Progress bar character (█ or ░) should start at the same column in both rows
        var barCol0 = dlLines[0].IndexOf('█') >= 0 ? dlLines[0].IndexOf('█') : dlLines[0].IndexOf('░');
        var barCol1 = dlLines[1].IndexOf('█') >= 0 ? dlLines[1].IndexOf('█') : dlLines[1].IndexOf('░');
        barCol0.ShouldBe(barCol1,
            $"Progress bars should start at same column.\nRow 0: [{dlLines[0]}]\nRow 1: [{dlLines[1]}]");
    }
}
