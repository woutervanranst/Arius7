using Arius.AzureBlob;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Shouldly;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Arius.E2E.Tests;

/// <summary>
/// End-to-end tests for Archive-tier rehydration flow against real Azure Blob Storage.
///
/// Cost note: Archive tier has a 180-day early deletion policy.  Each test archives
/// files of ~100-500 bytes and then immediately deletes the container in teardown.
/// The prorated early deletion fee for tiny files is negligible (fractions of a cent).
///
/// These tests are gated by the same env-var pair as the main E2E suite:
///   ARIUS_E2E_ACCOUNT  — storage account name
///   ARIUS_E2E_KEY      — storage account key
///
/// Covers tasks 2.1–4.3.
/// </summary>
[ClassDataSource<AzureFixture>(Shared = SharedType.PerTestSession)]
public class RehydrationE2ETests(AzureFixture azure)
{
    // ── Task 2.1: E2E archive/restore against real Azure, gated by env vars ───

    /// <summary>
    /// Full Archive-tier rehydration cycle:
    ///   1. Archive 3 small files (~100-500 bytes) to Archive tier.
    ///   2. Poll until blobs are confirmed in Archive tier.
    ///   3. Attempt restore — expect rehydration to be initiated (ChunksPendingRehydration > 0).
    ///   4. Re-run restore — verify pending rehydration is re-reported without duplicate copy calls.
    ///   5. Sideload rehydrated chunk content to chunks-rehydrated/&lt;hash&gt; in Hot tier.
    ///   6. Re-run restore — verify files are byte-identical after downloading from the sideloaded blob.
    ///
    /// Cost note: tiny files archived to Archive tier and deleted immediately — cost is fractions of a cent.
    /// </summary>
    [Test]
    [Timeout(60_000)] // Task 4.2: 60-second timeout for Archive tier operations
    public async Task E2E_Rehydration_FullCycle(CancellationToken ct)
    {
        var (container, svc, cleanup) = await azure.CreateTestContainerAsync(ct);
        try
        {
            // ── Task 2.2: Create 3 test files of ~100-500 bytes ───────────────

            var fix = await E2EFixture.CreateAsync(container, svc, BlobTier.Archive);

            var content1 = new byte[100]; Random.Shared.NextBytes(content1);
            var content2 = new byte[300]; Random.Shared.NextBytes(content2);
            var content3 = new byte[500]; Random.Shared.NextBytes(content3);
            fix.WriteFile("file1.bin", content1);
            fix.WriteFile("file2.bin", content2);
            fix.WriteFile("file3.bin", content3);

            // ── Task 2.3: Archive to Archive tier ─────────────────────────────

            var archiveResult = await fix.ArchiveAsync(ct);
            archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

            // ── Task 2.4: Poll until all chunk blobs are in Archive tier ──────
            // Archive tier transition can take several seconds after SetBlobTier.

            var chunkBlobName = await PollForArchiveTierAsync(svc, BlobPaths.Chunks, ct);
            chunkBlobName.ShouldNotBeNullOrEmpty("Expected at least one chunk blob to transition to Archive tier");

            // ── Task 3.1: First restore — expect rehydration to be initiated ──

            // Track copy calls to verify exactly one rehydration request per chunk
            var trackingSvc = new CopyTrackingBlobService(svc);
            var restoreFixture = await E2EFixture.CreateAsync(container,
                new AzureBlobContainerService(container), BlobTier.Archive);

            var restoreOpts1 = new RestoreOptions
            {
                RootDirectory    = fix.RestoreRoot,
                Overwrite        = true,
                ConfirmRehydration = (est, _) =>
                {
                    // Verify cost estimate captures the right chunk counts
                    (est.ChunksNeedingRehydration + est.ChunksPendingRehydration).ShouldBeGreaterThan(0,
                        "cost estimate should include archive-tier chunks");
                    return Task.FromResult<RehydratePriority?>(RehydratePriority.Standard);
                },
            };

            var restoreHandler1 = new RestoreCommandHandler(
                fix.Encryption, fix.Index,
                new ChunkStorageService(trackingSvc, fix.Encryption),
                new FileTreeService(trackingSvc, fix.Encryption, fix.Index, container.AccountName, container.Name),
                new SnapshotService(trackingSvc, fix.Encryption, container.AccountName, container.Name),
                NSubstitute.Substitute.For<Mediator.IMediator>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RestoreCommandHandler>.Instance,
                container.AccountName, container.Name);

            var result1 = await restoreHandler1.Handle(new RestoreCommand(restoreOpts1), ct).AsTask();

            result1.Success.ShouldBeTrue(result1.ErrorMessage);
            result1.ChunksPendingRehydration.ShouldBeGreaterThan(0,
                "rehydration should have been initiated");
            result1.FilesRestored.ShouldBe(0,
                "no files restored yet — blobs are in Archive tier");

            var copiesAfterFirstRestore = trackingSvc.CopyCalls.Count;
            copiesAfterFirstRestore.ShouldBeGreaterThan(0,
                "restore should have initiated at least one rehydration copy");

            // ── Task 3.2: Re-run restore — verify pending rehydration detected ─

            var trackingSvc2 = new CopyTrackingBlobService(svc);
            var restoreHandler2 = new RestoreCommandHandler(
                fix.Encryption, fix.Index,
                new ChunkStorageService(trackingSvc2, fix.Encryption),
                new FileTreeService(trackingSvc2, fix.Encryption, fix.Index, container.AccountName, container.Name),
                new SnapshotService(trackingSvc2, fix.Encryption, container.AccountName, container.Name),
                NSubstitute.Substitute.For<Mediator.IMediator>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RestoreCommandHandler>.Instance,
                container.AccountName, container.Name);

            var restoreOpts2 = new RestoreOptions
            {
                RootDirectory    = fix.RestoreRoot,
                Overwrite        = true,
                ConfirmRehydration = (_, __) => Task.FromResult<RehydratePriority?>(RehydratePriority.Standard),
            };

            var result2 = await restoreHandler2.Handle(new RestoreCommand(restoreOpts2), ct).AsTask();

            result2.Success.ShouldBeTrue(result2.ErrorMessage);
            result2.ChunksPendingRehydration.ShouldBeGreaterThan(0,
                "chunks still pending rehydration on re-run");

            // The re-run must NOT issue any new copy calls — the copy is already in progress
            // and re-requesting would throw BlobArchived 409.
            trackingSvc2.CopyCalls.Count.ShouldBe(0,
                "re-run should not issue copy calls for already-pending rehydration");

            // ── Task 3.3: Sideload rehydrated chunk content ───────────────────
            // Bypass the ~15-hour rehydration wait: reconstruct the tar bundle
            // from raw file content bytes and upload to chunks-rehydrated/<hash>
            // in Hot tier. This simulates what Azure does when rehydration completes.
            // NOTE: we cannot DownloadAsync from Archive-tier blobs — they are offline.

            // Compute content hashes (SHA256 of raw bytes, lowercase hex)
            var contentHashToBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [Convert.ToHexString(SHA256.HashData(content1)).ToLowerInvariant()] = content1,
                [Convert.ToHexString(SHA256.HashData(content2)).ToLowerInvariant()] = content2,
                [Convert.ToHexString(SHA256.HashData(content3)).ToLowerInvariant()] = content3,
            };

            await SideloadRehydratedChunksAsync(svc, contentHashToBytes, fix.Index, ct);

            // ── Task 3.4: Third restore — files should be restored from sideloaded blobs ─

            var restoreRoot3 = Path.Combine(Path.GetTempPath(), $"arius-restore3-{Guid.NewGuid():N}");
            Directory.CreateDirectory(restoreRoot3);
            try
            {
                var restoreHandler3 = new RestoreCommandHandler(
                    fix.Encryption, fix.Index,
                    new ChunkStorageService(svc, fix.Encryption),
                    new FileTreeService(svc, fix.Encryption, fix.Index, container.AccountName, container.Name),
                    new SnapshotService(svc, fix.Encryption, container.AccountName, container.Name),
                    NSubstitute.Substitute.For<Mediator.IMediator>(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<RestoreCommandHandler>.Instance,
                    container.AccountName, container.Name);

                var restoreOpts3 = new RestoreOptions
                {
                    RootDirectory = restoreRoot3,
                    Overwrite     = true,
                };

                var result3 = await restoreHandler3.Handle(new RestoreCommand(restoreOpts3), ct).AsTask();

                result3.Success.ShouldBeTrue(result3.ErrorMessage);
                result3.FilesRestored.ShouldBe(3, "all 3 files should be restored from sideloaded blobs");
                result3.ChunksPendingRehydration.ShouldBe(0, "no chunks pending after sideload");

                // Verify byte-identical content
                File.ReadAllBytes(Path.Combine(restoreRoot3, "file1.bin")).ShouldBe(content1);
                File.ReadAllBytes(Path.Combine(restoreRoot3, "file2.bin")).ShouldBe(content2);
                File.ReadAllBytes(Path.Combine(restoreRoot3, "file3.bin")).ShouldBe(content3);
            }
            finally
            {
                if (Directory.Exists(restoreRoot3))
                    Directory.Delete(restoreRoot3, recursive: true);
            }

            await fix.DisposeAsync();
            await restoreFixture.DisposeAsync();
        }
        finally
        {
            // Task 4.3: container cleanup in teardown
            await cleanup();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Polls <paramref name="svc"/> for blobs under <paramref name="prefix"/> until at least one
    /// is confirmed in Archive tier (or <paramref name="ct"/> is cancelled).
    /// Returns the name of the first Archive-tier blob found, or null if none transition.
    /// </summary>
    private static async Task<string?> PollForArchiveTierAsync(
        AzureBlobContainerService svc,
        string                  prefix,
        CancellationToken       ct)
    {
        // Archive tier transition typically completes in seconds.
        // Poll every 2 seconds for up to 55 seconds (leaving margin in the 60s test timeout).
        var deadline = DateTime.UtcNow.AddSeconds(55);
        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            await foreach (var blobName in svc.ListAsync(prefix, ct))
            {
                var meta = await svc.GetMetadataAsync(blobName, ct);
                if (meta.Tier == BlobTier.Archive)
                    return blobName;
            }
            await Task.Delay(2000, ct);
        }
        return null;
    }

    /// <summary>
    /// Reconstructs each tar bundle from raw content bytes and uploads it to
    /// <c>chunks-rehydrated/&lt;tarHash&gt;</c> in Hot tier, simulating completed rehydration.
    ///
    /// Archive-tier blobs cannot be downloaded via <see cref="AzureBlobContainerService.DownloadAsync"/>;
    /// instead we rebuild the PAX tar + gzip bundle entirely from the known raw bytes.
    /// </summary>
    private static async Task SideloadRehydratedChunksAsync(
        AzureBlobContainerService          svc,
        Dictionary<string, byte[]>       contentHashToBytes,
        ChunkIndexService                index,
        CancellationToken                ct)
    {
        // Use the chunk index to map contentHash → ChunkHash (tarHash)
        var allHashes    = contentHashToBytes.Keys.ToList();
        var indexEntries = await index.LookupAsync(allHashes, ct);

        // Group: tarHash → list of contentHashes bundled in that tar
        var tarToContents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (contentHash, entry) in indexEntries)
        {
            if (!tarToContents.TryGetValue(entry.ChunkHash, out var list))
                tarToContents[entry.ChunkHash] = list = new List<string>();
            list.Add(contentHash);
        }

        foreach (var (tarHash, contentHashes) in tarToContents)
        {
            var rehydratedBlobName = BlobPaths.ChunkRehydrated(tarHash);

            // Skip if already present as a downloadable (non-Archive) blob
            var rehydratedMeta = await svc.GetMetadataAsync(rehydratedBlobName, ct);
            if (rehydratedMeta.Exists && rehydratedMeta.Tier != BlobTier.Archive)
                continue;

            // If the destination exists in Archive tier (from a pending CopyAsync), delete it first.
            // Azure does not allow UploadAsync to overwrite an Archive-tier blob.
            if (rehydratedMeta.Exists && rehydratedMeta.Tier == BlobTier.Archive)
                await svc.DeleteAsync(rehydratedBlobName, ct);

            // Get metadata from source blob (GetProperties succeeds even on Archive-tier blobs)
            var sourceBlobName = BlobPaths.Chunk(tarHash);
            var sourceMeta     = await svc.GetMetadataAsync(sourceBlobName, ct);

            // Reconstruct the tar bundle in memory: PAX tar (entries named by contentHash) → GZip
            using var ms = new MemoryStream();
            await using (var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                await using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);
                foreach (var contentHash in contentHashes)
                {
                    if (!contentHashToBytes.TryGetValue(contentHash, out var rawBytes))
                        continue;
                    var tarEntry = new PaxTarEntry(TarEntryType.RegularFile, contentHash)
                    {
                        DataStream = new MemoryStream(rawBytes),
                    };
                    await tar.WriteEntryAsync(tarEntry, ct);
                }
            }
            ms.Position = 0;

            // Upload to chunks-rehydrated/<tarHash> as Hot tier, overwriting any pending-copy Archive blob
            await svc.UploadAsync(
                blobName:         rehydratedBlobName,
                content:          ms,
                metadata:         sourceMeta.Metadata,
                tier:             BlobTier.Hot,
                overwrite:        true,
                cancellationToken: ct);
        }
    }
}

/// <summary>
/// Wraps <see cref="AzureBlobContainerService"/> and records all <see cref="CopyAsync"/> calls.
/// Used to verify the restore pipeline does not issue duplicate rehydration requests.
/// </summary>
internal sealed class CopyTrackingBlobService(AzureBlobContainerService inner) : IBlobContainerService
{
    public List<(string Source, string Destination)> CopyCalls { get; } = new();

    public Task CreateContainerIfNotExistsAsync(CancellationToken ct = default)
        => inner.CreateContainerIfNotExistsAsync(ct);

    public Task UploadAsync(string blobName, Stream content,
        IReadOnlyDictionary<string, string> metadata, BlobTier tier,
        string? contentType = null, bool overwrite = false, CancellationToken ct = default)
        => inner.UploadAsync(blobName, content, metadata, tier, contentType, overwrite, ct);

    public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null,
        CancellationToken ct = default)
        => inner.OpenWriteAsync(blobName, contentType, ct);

    public Task<Stream> DownloadAsync(string blobName, CancellationToken ct = default)
        => inner.DownloadAsync(blobName, ct);

    public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken ct = default)
        => inner.GetMetadataAsync(blobName, ct);

    public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct = default)
        => inner.ListAsync(prefix, ct);

    public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata,
        CancellationToken ct = default)
        => inner.SetMetadataAsync(blobName, metadata, ct);

    public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken ct = default)
        => inner.SetTierAsync(blobName, tier, ct);

    public async Task CopyAsync(string sourceBlobName, string destinationBlobName,
        BlobTier destinationTier, RehydratePriority? rehydratePriority = null,
        CancellationToken ct = default)
    {
        CopyCalls.Add((sourceBlobName, destinationBlobName));
        await inner.CopyAsync(sourceBlobName, destinationBlobName, destinationTier, rehydratePriority, ct);
    }

    public Task DeleteAsync(string blobName, CancellationToken ct = default)
        => inner.DeleteAsync(blobName, ct);
}
