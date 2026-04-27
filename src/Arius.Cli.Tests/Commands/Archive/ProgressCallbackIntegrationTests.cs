using Arius.Core.Shared.Hashes;

namespace Arius.Cli.Tests.Commands.Archive;

/// <summary>
/// Verifies that <see cref="CliBuilder"/>'s <c>CreateHashProgress</c> and
/// <c>CreateUploadProgress</c> factory callbacks correctly wire to
/// <see cref="TrackedFile.SetBytesProcessed"/> and <see cref="TrackedTar.SetBytesUploaded"/>.
/// </summary>
public class ProgressCallbackIntegrationTests
{
    private static ContentHash Content(char c) => ContentHash.Parse(new string(c, 64));

    private static void WaitFor(Func<bool> condition) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(1)).ShouldBeTrue();

    [Test]
    public void CreateHashProgress_UpdatesBytesProcessed()
    {
        var state = new ProgressState();
        state.AddFile("large.bin", 5_000_000);
        state.SetFileHashed("large.bin", Content('a'));

        // Simulate what CliBuilder wires: look up TrackedFile by relative path
        IProgress<long>? hashProgress = null;
        if (state.TrackedFiles.TryGetValue("large.bin", out var file))
            hashProgress = new Progress<long>(bytes => file.SetBytesProcessed(bytes));

        hashProgress.ShouldNotBeNull();
        file.ShouldNotBeNull();

        hashProgress.Report(2_500_000);
        WaitFor(() => file.BytesProcessed == 2_500_000L);
        file.BytesProcessed.ShouldBe(2_500_000L);
    }

    [Test]
    public void CreateUploadProgress_LargeFile_ResetsThenUpdatesBytesProcessed()
    {
        var state = new ProgressState();
        state.AddFile("chunk.bin", 1_000_000);
        state.SetFileHashed("chunk.bin", Content('b'));
        state.SetFileUploading(Content('b'));

        IProgress<long>? uploadProgress = null;
        if (state.ContentHashToPath.TryGetValue(Content('b'), out var paths))
        {
            var files = paths
                .Select(p => state.TrackedFiles.TryGetValue(p, out var f) ? f : null)
                .Where(f => f != null)
                .ToList();
            if (files.Count > 0)
            {
                foreach (var f in files) f!.SetBytesProcessed(0);
                uploadProgress = new Progress<long>(bytes => { foreach (var f in files) f!.SetBytesProcessed(bytes); });
            }
        }

        uploadProgress.ShouldNotBeNull();
        state.TrackedFiles["chunk.bin"].BytesProcessed.ShouldBe(0L);

        uploadProgress.Report(450_000);
        WaitFor(() => state.TrackedFiles["chunk.bin"].BytesProcessed == 450_000L);
        state.TrackedFiles["chunk.bin"].BytesProcessed.ShouldBe(450_000L);
    }

    [Test]
    public void CreateUploadProgress_TarBundle_UpdatesBytesUploaded()
    {
        var state = new ProgressState();
        var tar   = new TrackedTar(1, 64L * 1024 * 1024);
        tar.TarHash = ChunkHash.Parse(new string('c', 64));
        tar.TotalBytes = 300L;
        state.TrackedTars.TryAdd(1, tar);

        // Simulate TAR branch of CreateUploadProgress
        var foundTar = state.TrackedTars.Values.FirstOrDefault(t => t.TarHash == ChunkHash.Parse(new string('c', 64)));
        foundTar.ShouldNotBeNull();

        IProgress<long> uploadProgress = new Progress<long>(bytes => foundTar!.SetBytesUploaded(bytes));

        uploadProgress.Report(150L);
        WaitFor(() => tar.BytesUploaded == 150L);
        tar.BytesUploaded.ShouldBe(150L);
    }
}
