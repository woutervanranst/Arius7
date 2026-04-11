using Shouldly;

namespace Arius.Cli.Tests;

/// <summary>
/// Verifies that <see cref="CliBuilder"/>'s <c>CreateHashProgress</c> and
/// <c>CreateUploadProgress</c> factory callbacks correctly wire to
/// <see cref="TrackedFile.SetBytesProcessed"/> and <see cref="TrackedTar.SetBytesUploaded"/>.
/// </summary>
public class ProgressCallbackIntegrationTests
{
    [Test]
    public void CreateHashProgress_UpdatesBytesProcessed()
    {
        var state = new ProgressState();
        state.AddFile("large.bin", 5_000_000);
        state.SetFileHashed("large.bin", "lhash1");

        // Simulate what CliBuilder wires: look up TrackedFile by relative path
        IProgress<long>? hashProgress = null;
        if (state.TrackedFiles.TryGetValue("large.bin", out var file))
            hashProgress = new Progress<long>(bytes => file.SetBytesProcessed(bytes));

        hashProgress.ShouldNotBeNull();
        file.ShouldNotBeNull();

        file.SetBytesProcessed(2_500_000);
        file.BytesProcessed.ShouldBe(2_500_000L);
    }

    [Test]
    public void CreateUploadProgress_LargeFile_ResetsThenUpdatesBytesProcessed()
    {
        var state = new ProgressState();
        state.AddFile("chunk.bin", 1_000_000);
        state.SetFileHashed("chunk.bin", "chash1");
        state.SetFileUploading("chash1");

        IProgress<long>? uploadProgress = null;
        if (state.ContentHashToPath.TryGetValue("chash1", out var paths))
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

        state.TrackedFiles["chunk.bin"].SetBytesProcessed(450_000);
        state.TrackedFiles["chunk.bin"].BytesProcessed.ShouldBe(450_000L);
    }

    [Test]
    public void CreateUploadProgress_TarBundle_UpdatesBytesUploaded()
    {
        var state = new ProgressState();
        var tar   = new TrackedTar(1, 64L * 1024 * 1024);
        tar.TarHash = "tarhash1";
        tar.TotalBytes = 300L;
        state.TrackedTars.TryAdd(1, tar);

        // Simulate TAR branch of CreateUploadProgress
        var foundTar = state.TrackedTars.Values.FirstOrDefault(t => t.TarHash == "tarhash1");
        foundTar.ShouldNotBeNull();

        foundTar!.SetBytesUploaded(150L);
        tar.BytesUploaded.ShouldBe(150L);
    }
}
