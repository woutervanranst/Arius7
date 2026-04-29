using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Storage;
using Arius.Core.Tests.Fakes;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreeBuilderTests
{
    private static readonly PlaintextPassthroughService s_enc = new();

    private sealed class BlockingFileTreeUploadBlobContainerService : IBlobContainerService
    {
        private readonly TaskCompletionSource<bool> _allowUploads = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _twoUploadsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startedUploads;

        public HashSet<string> Uploaded { get; } = new(StringComparer.Ordinal);

        public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
        {
            if (blobName.StartsWith(BlobPaths.FileTrees, StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _startedUploads) >= 2)
                    _twoUploadsStarted.TrySetResult(true);

                await _allowUploads.Task.WaitAsync(cancellationToken);
            }

            Uploaded.Add(blobName);
        }

        public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BlobMetadata { Exists = false });

        public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<string>();

        public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<bool> WaitForTwoUploadsAsync(TimeSpan timeout)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            try
            {
                await _twoUploadsStarted.Task.WaitAsync(timeoutCts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public void AllowUploads() => _allowUploads.TrySetResult(true);
    }

    private static FileTreeBuilder CreateBuilder(
        IBlobContainerService blobs,
        string accountName,
        string containerName,
        out FileTreeService fileTreeService)
    {
        var index = new ChunkIndexService(blobs, s_enc, accountName, containerName);
        fileTreeService = new FileTreeService(blobs, s_enc, index, accountName, containerName);
        return new FileTreeBuilder(s_enc, fileTreeService);
    }

    [Test]
    public async Task SynchronizeAsync_EmptyManifest_ReturnsNull()
    {
        var manifestPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(manifestPath, "");

            var blobs   = new FakeRecordingBlobContainerService();
            var builder = CreateBuilder(blobs, "account", "container", out var fileTreeService);
            await fileTreeService.ValidateAsync();
            var root    = await builder.SynchronizeAsync(manifestPath);

            root.ShouldBeNull();
            blobs.Uploaded.ShouldBeEmpty();
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }

    [Test]
    public async Task SynchronizeAsync_SingleFile_RootTreeUploaded()
    {
        const string acct = "acct-single";
        const string cont = "cont-single";
        var cacheDir = FileTreeService.GetDiskCacheDirectory(acct, cont);

        if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);

        var manifestPath = Path.GetTempFileName();
        try
        {
            var now   = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            var entry = new ManifestEntry("readme.txt", FakeContentHash('b'), now, now);
            await File.WriteAllTextAsync(manifestPath, entry.Serialize() + "\n");

            var blobs   = new FakeRecordingBlobContainerService();
            var builder = CreateBuilder(blobs, acct, cont, out var fileTreeService);
            await fileTreeService.ValidateAsync();
            var root    = await builder.SynchronizeAsync(manifestPath);

            root.ShouldNotBeNull();
            blobs.Uploaded.Count.ShouldBeGreaterThanOrEqualTo(1);
        }
        finally
        {
            File.Delete(manifestPath);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task SynchronizeAsync_IdenticalManifest_SameRootHash()
    {
        const string acct1 = "acc-identical-1", cont1 = "con-identical-1";
        const string acct2 = "acc-identical-2", cont2 = "con-identical-2";
        var cache1 = FileTreeService.GetDiskCacheDirectory(acct1, cont1);
        var cache2 = FileTreeService.GetDiskCacheDirectory(acct2, cont2);
        if (Directory.Exists(cache1)) Directory.Delete(cache1, recursive: true);
        if (Directory.Exists(cache2)) Directory.Delete(cache2, recursive: true);

        var manifestPath1 = Path.GetTempFileName();
        var manifestPath2 = Path.GetTempFileName();
        try
        {
            var now   = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            var lines = new[]
            {
                new ManifestEntry("photos/a.jpg", FakeContentHash('c'), now, now).Serialize(),
                new ManifestEntry("photos/b.jpg", FakeContentHash('d'), now, now).Serialize(),
                new ManifestEntry("docs/r.pdf",   FakeContentHash('e'), now, now).Serialize(),
            };
            var content = string.Join("\n", lines) + "\n";
            await File.WriteAllTextAsync(manifestPath1, content);
            await File.WriteAllTextAsync(manifestPath2, content);

            var blobs1   = new FakeRecordingBlobContainerService();
            var blobs2   = new FakeRecordingBlobContainerService();
            var builder1 = CreateBuilder(blobs1, acct1, cont1, out var fileTreeService1);
            var builder2 = CreateBuilder(blobs2, acct2, cont2, out var fileTreeService2);
            await fileTreeService1.ValidateAsync();
            await fileTreeService2.ValidateAsync();
            var root1    = await builder1.SynchronizeAsync(manifestPath1);
            var root2    = await builder2.SynchronizeAsync(manifestPath2);

            root1.ShouldBe(root2);
        }
        finally
        {
            File.Delete(manifestPath1);
            File.Delete(manifestPath2);
            if (Directory.Exists(cache1)) Directory.Delete(cache1, recursive: true);
            if (Directory.Exists(cache2)) Directory.Delete(cache2, recursive: true);
        }
    }

    [Test]
    public async Task SynchronizeAsync_MetadataChange_DifferentRootHash()
    {
        const string acct = "acc-meta", cont = "con-meta";
        var cacheDir = FileTreeService.GetDiskCacheDirectory(acct, cont);
        if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);

        var manifestPath1 = Path.GetTempFileName();
        var manifestPath2 = Path.GetTempFileName();
        try
        {
            var now1  = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            var now2  = new DateTimeOffset(2025, 1, 1,  0,  0, 0, TimeSpan.Zero);
            await File.WriteAllTextAsync(manifestPath1,
                new ManifestEntry("file.txt", FakeContentHash('f'), now1, now1).Serialize() + "\n");
            await File.WriteAllTextAsync(manifestPath2,
                new ManifestEntry("file.txt", FakeContentHash('f'), now1, now2).Serialize() + "\n");

            var blobs1 = new FakeRecordingBlobContainerService();
            var blobs2 = new FakeRecordingBlobContainerService();
            var builder1 = CreateBuilder(blobs1, acct, cont, out var fileTreeService1);
            await fileTreeService1.ValidateAsync();
            var root1  = await builder1.SynchronizeAsync(manifestPath1);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
            var builder2 = CreateBuilder(blobs2, acct, cont, out var fileTreeService2);
            await fileTreeService2.ValidateAsync();
            var root2  = await builder2.SynchronizeAsync(manifestPath2);

            root1.ShouldNotBe(root2);
        }
        finally
        {
            File.Delete(manifestPath1);
            File.Delete(manifestPath2);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task SynchronizeAsync_DeduplicatesBlob_WhenAlreadyOnDisk()
    {
        var manifestPath = Path.GetTempFileName();
        try
        {
            var now   = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var entry = new ManifestEntry("file.txt", FakeContentHash('1'), now, now);
            await File.WriteAllTextAsync(manifestPath, entry.Serialize() + "\n");

            var blobs   = new FakeRecordingBlobContainerService();
            var builder = CreateBuilder(blobs, "acc", "con", out var fileTreeService1);
            await fileTreeService1.ValidateAsync();

            var root = await builder.SynchronizeAsync(manifestPath);
            var uploadCount1 = blobs.Uploaded.Count;
            uploadCount1.ShouldBeGreaterThan(0);

            var blobs2   = new FakeRecordingBlobContainerService();
            var builder2 = CreateBuilder(blobs2, "acc", "con", out var fileTreeService2);
            await fileTreeService2.ValidateAsync();
            var root2    = await builder2.SynchronizeAsync(manifestPath);

            root2.ShouldBe(root);
            blobs2.Uploaded.Count.ShouldBe(0);
        }
        finally
        {
            File.Delete(manifestPath);
            var cacheDir = FileTreeService.GetDiskCacheDirectory("acc", "con");
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task SynchronizeAsync_WithoutValidation_FailsFastBeforeUpload()
    {
        var manifestPath = Path.GetTempFileName();
        try
        {
            var now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await File.WriteAllTextAsync(
                manifestPath,
                new ManifestEntry("file.txt", FakeContentHash('2'), now, now).Serialize() + "\n");

            var blobs = new FakeRecordingBlobContainerService();
            var builder = CreateBuilder(blobs, "acc-unvalidated", "con-unvalidated", out _);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await builder.SynchronizeAsync(manifestPath));

            ex.Message.ShouldContain("ValidateAsync");
            blobs.Uploaded.ShouldBeEmpty();
        }
        finally
        {
            File.Delete(manifestPath);
            var cacheDir = FileTreeService.GetDiskCacheDirectory("acc-unvalidated", "con-unvalidated");
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task SynchronizeAsync_StartsMultipleFileTreeUploadsBeforeReturning()
    {
        const string accountName = "acc-parallel";
        const string containerName = "con-parallel";
        var cacheDir = FileTreeService.GetDiskCacheDirectory(accountName, containerName);
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);

        var manifestPath = Path.GetTempFileName();
        try
        {
            var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            var lines = new[]
            {
                new ManifestEntry("photos/2024/june/a.jpg", FakeContentHash('7'), now, now).Serialize(),
                new ManifestEntry("photos/2024/june/b.jpg", FakeContentHash('8'), now, now).Serialize(),
                new ManifestEntry("docs/report.pdf", FakeContentHash('9'), now, now).Serialize(),
            };
            await File.WriteAllTextAsync(manifestPath, string.Join("\n", lines) + "\n");

            var blobs = new BlockingFileTreeUploadBlobContainerService();
            var builder = CreateBuilder(blobs, accountName, containerName, out var fileTreeService);
            await fileTreeService.ValidateAsync();

            var synchronizeTask = builder.SynchronizeAsync(manifestPath);
            var sawTwoConcurrentStarts = await blobs.WaitForTwoUploadsAsync(TimeSpan.FromMilliseconds(500));

            blobs.AllowUploads();

            var root = await synchronizeTask.WaitAsync(TimeSpan.FromSeconds(5));
            root.ShouldNotBeNull();
            sawTwoConcurrentStarts.ShouldBeTrue();
        }
        finally
        {
            File.Delete(manifestPath);
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task SynchronizeAsync_NestedDirectories_ProducesStableRootHash()
    {
        const string accountName = "acc-nested-core";
        const string containerName = "con-nested-core";
        var cacheDir = FileTreeService.GetDiskCacheDirectory(accountName, containerName);
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);

        var manifestPath = Path.GetTempFileName();
        try
        {
            var now = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            var lines = new[]
            {
                new ManifestEntry("a/b/c/file.txt", FakeContentHash('a'), now, now).Serialize(),
                new ManifestEntry("a/b/other.txt", FakeContentHash('b'), now, now).Serialize(),
                new ManifestEntry("z.txt", FakeContentHash('c'), now, now).Serialize(),
            };
            await File.WriteAllTextAsync(manifestPath, string.Join("\n", lines) + "\n");

            var blobs = new FakeRecordingBlobContainerService();
            var builder = CreateBuilder(blobs, accountName, containerName, out var fileTreeService);
            await fileTreeService.ValidateAsync();

            var root1 = await builder.SynchronizeAsync(manifestPath);

            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);

            var blobs2 = new FakeRecordingBlobContainerService();
            var builder2 = CreateBuilder(blobs2, accountName, containerName, out var fileTreeService2);
            await fileTreeService2.ValidateAsync();

            var root2 = await builder2.SynchronizeAsync(manifestPath);

            root1.ShouldNotBeNull();
            root2.ShouldBe(root1);
        }
        finally
        {
            File.Delete(manifestPath);
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }
}
