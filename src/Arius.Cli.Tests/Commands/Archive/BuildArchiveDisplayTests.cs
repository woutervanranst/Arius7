using Arius.Cli.Commands.Archive;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.Hashes;
using Arius.Tests.Shared.Hashes;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Arius.Cli.Tests.Commands.Archive;

/// <summary>
/// Verifies <see cref="ArchiveVerb.BuildDisplay"/> renders the new three-section
/// layout: scanning header with live counter, hashing header with unique count + queue depth,
/// uploading header, per-file lines (only Hashing/Uploading), and TAR bundle lines.
/// </summary>
public class BuildArchiveDisplayTests
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

    private static string GetLineContaining(string output, string token)
        => output.Split('\n').Select(line => line.TrimEnd('\r')).Single(line => line.Contains(token));

    // ── Stage headers ─────────────────────────────────────────────────────────

    [Test]
    public void BuildArchiveDisplay_ShowsAllThreeStageHeaders()
    {
        var state = new ProgressState();
        var renderable = ArchiveVerb.BuildDisplay(state);
        var output     = RenderToString(renderable);

        output.ShouldContain("Scanning");
        output.ShouldContain("Hashing");
        output.ShouldContain("Uploading");
    }

    // ── 6.1 Scanning header ───────────────────────────────────────────────────

    [Test]
    public void BuildArchiveDisplay_ScanningHeader_ShowsLiveCount()
    {
        var state = new ProgressState();
        state.IncrementFilesScanned(1024);
        state.IncrementFilesScanned(2048);

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        var scanningLine = GetLineContaining(output, "Scanning");

        scanningLine.ShouldContain("○");
        scanningLine.ShouldContain("Scanning");
        scanningLine.ShouldContain("2 files");
    }

    [Test]
    public void BuildArchiveDisplay_ScanningHeader_FilledCircle_WhenScanComplete()
    {
        var state = new ProgressState();
        state.SetScanComplete(1523, 5_000_000L);

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        var scanningLine = GetLineContaining(output, "Scanning");

        scanningLine.ShouldContain("●");
        scanningLine.ShouldContain(1523.ToString("N0") + " files");
    }

    // ── 6.2 Hashing header with unique count + queue depth ───────────────────

    [Test]
    public void BuildArchiveDisplay_HashingHeader_ShowsUniqueCount()
    {
        var state = new ProgressState();
        state.IncrementFilesUnique();
        state.IncrementFilesUnique();
        state.IncrementFilesUnique();

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        var hashingLine = GetLineContaining(output, "Hashing");

        hashingLine.ShouldContain("Hashing");
        hashingLine.ShouldContain("(3 unique)");
    }

    [Test]
    public void BuildArchiveDisplay_HashingHeader_ShowsQueueDepth_WhenNonZero()
    {
        var state = new ProgressState();
        state.HashQueueDepth = () => 12;

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        var hashingLine = GetLineContaining(output, "Hashing");

        hashingLine.ShouldContain("[12 pending]");
    }

    [Test]
    public void BuildArchiveDisplay_HashingHeader_NoQueueDepth_WhenZero()
    {
        var state = new ProgressState();
        state.HashQueueDepth = () => 0;

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        var hashingLine = GetLineContaining(output, "Hashing");

        hashingLine.ShouldNotContain("pending");
    }

    // ── 6.3 Uploading header with queue depth ────────────────────────────────

    [Test]
    public void BuildArchiveDisplay_UploadingHeader_ShowsQueueDepth_WhenNonZero()
    {
        var state = new ProgressState();
        state.IncrementChunksUploaded(100);
        state.UploadQueueDepth = () => 3;

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        var uploadingLine = GetLineContaining(output, "Uploading");

        uploadingLine.ShouldContain("Uploading");
        uploadingLine.ShouldContain("[3 pending]");
    }

    [Test]
    public void BuildArchiveDisplay_UploadingHeader_FilledCircle_WhenSnapshotComplete()
    {
        var state = new ProgressState();
        state.IncrementChunksUploaded(100);
        state.SetSnapshotComplete();

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        var uploadingLine = GetLineContaining(output, "Uploading");

        uploadingLine.ShouldContain("●");
    }

    [Test]
    public void BuildArchiveDisplay_UploadingHeader_OpenCircle_WhenNotComplete()
    {
        var state = new ProgressState();
        state.IncrementChunksUploaded(100);
        // SnapshotComplete NOT set

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        var uploadingLine = GetLineContaining(output, "Uploading");

        uploadingLine.ShouldContain("○");
    }

    // ── 6.4 Per-file lines: only Hashing or Uploading ────────────────────────

    [Test]
    public void BuildArchiveDisplay_ShowsHashingFile()
    {
        var state = new ProgressState();
        state.AddFile("video.mp4", 5_000_000);
        // State is Hashing by default

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        output.ShouldContain("video.mp4");
        output.ShouldContain("Hashing");
    }

    [Test]
    public void BuildArchiveDisplay_ShowsUploadingFile()
    {
        var state = new ProgressState();
        state.AddFile("large.bin", 10_000_000);
        state.SetFileHashed("large.bin", HashTestData.Content('1'));
        state.SetFileUploading(HashTestData.Content('1'));

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        output.ShouldContain("large.bin");
        output.ShouldContain("Uploading");
    }

    [Test]
    public void BuildArchiveDisplay_DoesNotShowHashedFile()
    {
        // Hashed state is invisible
        var state = new ProgressState();
        state.AddFile("pending.bin", 1000);
        state.SetFileHashed("pending.bin", HashTestData.Content('2'));
        // State is now Hashed — should not appear

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        output.ShouldNotContain("pending.bin");
    }

    [Test]
    public void BuildArchiveDisplay_DoesNotShowRemovedFiles()
    {
        var state = new ProgressState();
        state.AddFile("completed.bin", 1000);
        state.SetFileHashed("completed.bin", HashTestData.Content('3'));
        state.RemoveFile("completed.bin");

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        output.ShouldNotContain("completed.bin");
    }

    // ── 6.5 TAR bundle lines ─────────────────────────────────────────────────

    [Test]
    public async Task BuildArchiveDisplay_ShowsTarLine_WhenAccumulating()
    {
        var state    = new ProgressState();
        var startedH = new TarBundleStartedHandler(state);
        await startedH.Handle(new TarBundleStartedEvent(), CancellationToken.None);

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        output.ShouldContain("TAR #1");
        output.ShouldContain("Accumulating");
    }

    [Test]
    public async Task BuildArchiveDisplay_ShowsTarLine_WhenSealing()
    {
        var state    = new ProgressState();
        var startedH = new TarBundleStartedHandler(state);
        var sealingH = new TarBundleSealingHandler(state);
        await startedH.Handle(new TarBundleStartedEvent(), CancellationToken.None);
        await sealingH.Handle(
            new TarBundleSealingEvent(3, 300, HashTestData.Chunk('a'), [HashTestData.Content('a'), HashTestData.Content('b'), HashTestData.Content('c')]),
            CancellationToken.None);

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        output.ShouldContain("TAR #1");
        output.ShouldContain("Sealing");
    }

    [Test]
    public async Task BuildArchiveDisplay_ShowsTarLine_WhenUploading()
    {
        var state      = new ProgressState();
        var startedH   = new TarBundleStartedHandler(state);
        var sealingH   = new TarBundleSealingHandler(state);
        var uploadingH = new ChunkUploadingHandler(state);
        await startedH.Handle(new TarBundleStartedEvent(), CancellationToken.None);
        await sealingH.Handle(
            new TarBundleSealingEvent(2, 200, HashTestData.Chunk('b'), [HashTestData.Content('d'), HashTestData.Content('e')]),
            CancellationToken.None);
        await uploadingH.Handle(new ChunkUploadingEvent(HashTestData.Chunk('b'), 200), CancellationToken.None);

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        var tarLine = GetLineContaining(output, "TAR #1");

        tarLine.ShouldContain("TAR #1");
        tarLine.ShouldContain("Uploading");
    }

    // ── Truncation + size column ──────────────────────────────────────────────

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
        output.ShouldContain("...ory/structure/with/file.bin");
        output.ShouldNotContain(longPath);
    }

    [Test]
    public void BuildArchiveDisplay_ShowsSizeInMB_ForHashingFile()
    {
        var state = new ProgressState();
        state.AddFile("doc.pdf", 5_000_000);

        var output = RenderToString(ArchiveVerb.BuildDisplay(state));
        output.ShouldContain("MB");
    }
}
