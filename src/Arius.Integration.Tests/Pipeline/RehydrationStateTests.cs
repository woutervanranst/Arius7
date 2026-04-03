using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.Restore;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Storage;
using Arius.Integration.Tests.Storage;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Arius.Integration.Tests.Pipeline;

// ── Tier-simulating blob service wrapper ──────────────────────────────────────

/// <summary>
/// Wraps a real <see cref="IBlobContainerService"/> and overrides <see cref="GetMetadataAsync"/>
/// to simulate Archive-tier behaviour for a specific set of blob names, and <see cref="CopyAsync"/>
/// to record rehydration requests without actually copying.
/// Used to test restore pipeline rehydration state machine logic without real Azure Archive tier.
/// </summary>
internal sealed class RehydrationSimulatingBlobService(IBlobContainerService inner) : IBlobContainerService
{
    /// <summary>
    /// Blob names that should appear as Archive tier with no rehydration in progress.
    /// </summary>
    public HashSet<string> ArchiveTierBlobs { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Blob names that should appear as Archive tier with rehydration in progress.
    /// </summary>
    public HashSet<string> RehydratingBlobs { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Records each (source, destination) pair passed to <see cref="CopyAsync"/>.
    /// </summary>
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

    public async Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken ct = default)
    {
        var actual = await inner.GetMetadataAsync(blobName, ct);

        if (!actual.Exists)
            return actual;

        if (RehydratingBlobs.Contains(blobName))
            return new BlobMetadata
            {
                Exists        = true,
                Tier          = BlobTier.Archive,
                ContentLength = actual.ContentLength,
                IsRehydrating = true,
                Metadata      = actual.Metadata,
            };

        if (ArchiveTierBlobs.Contains(blobName))
            return new BlobMetadata
            {
                Exists        = true,
                Tier          = BlobTier.Archive,
                ContentLength = actual.ContentLength,
                IsRehydrating = false,
                Metadata      = actual.Metadata,
            };

        return actual;
    }

    public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct = default)
        => inner.ListAsync(prefix, ct);

    public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata,
        CancellationToken ct = default)
        => inner.SetMetadataAsync(blobName, metadata, ct);

    public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken ct = default)
        => inner.SetTierAsync(blobName, tier, ct);

    public Task CopyAsync(string sourceBlobName, string destinationBlobName,
        BlobTier destinationTier, RehydratePriority? rehydratePriority = null,
        CancellationToken ct = default)
    {
        CopyCalls.Add((sourceBlobName, destinationBlobName));
        // Do NOT forward to inner — the source is "archived" and a real copy would fail.
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string blobName, CancellationToken ct = default)
        => inner.DeleteAsync(blobName, ct);
}

// ── Rehydration state machine tests ──────────────────────────────────────────

/// <summary>
/// Mock-based integration tests for all three rehydration states in the restore pipeline.
///
/// Uses <see cref="RehydrationSimulatingBlobService"/> to intercept blob metadata and copy calls,
/// simulating Archive-tier behaviour without requiring real Azure Archive storage.
///
/// Covers tasks 1.1–1.4.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class RehydrationStateTests(AzuriteFixture azurite)
{
    private const string Account = "devstoreaccount1";

    /// <summary>
    /// Archives a single small file and returns the chunk blob name used for that file,
    /// along with the fixture and the simulating service.
    /// </summary>
    private static async Task<(PipelineFixture Fix, RehydrationSimulatingBlobService Sim, string ChunkBlobName)>
        SetupArchivedFixtureAsync(AzuriteFixture azurite)
    {
        var fix = await PipelineFixture.CreateAsync(azurite);
        var content = new byte[200];
        Random.Shared.NextBytes(content);
        fix.WriteFile("test.bin", content);

        // Archive with Hot tier (Azurite) so blobs are accessible
        var archiveOpts = new ArchiveCommandOptions
        {
            RootDirectory = fix.LocalRoot,
            UploadTier    = BlobTier.Hot,
        };
        var archiveResult = await fix.ArchiveAsync(archiveOpts);
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // Find the tar blob (which is what the restore pipeline downloads / rehydrates).
        // Small files are bundled into tar chunks; the thin chunk is just a pointer to the tar.
        var chunkBlobName = string.Empty;
        await foreach (var name in fix.BlobContainer.ListAsync(BlobPaths.Chunks))
        {
            var meta = await fix.BlobContainer.GetMetadataAsync(name);
            if (meta.Metadata.TryGetValue(BlobMetadataKeys.AriusType, out var t) && t == BlobMetadataKeys.TypeTar)
            {
                chunkBlobName = name;
                break;
            }
        }
        chunkBlobName.ShouldNotBeNullOrEmpty("Expected at least one tar chunk blob");

        var sim = new RehydrationSimulatingBlobService(fix.BlobContainer);
        return (fix, sim, chunkBlobName);
    }

    private static RestorePipelineHandler MakeRestoreHandler(
        RehydrationSimulatingBlobService sim, PipelineFixture fix) =>
        new(sim, fix.Encryption,
            new ChunkIndexService(sim, fix.Encryption, Account, fix.Container.Name),
            Substitute.For<IMediator>(),
            NullLogger<RestorePipelineHandler>.Instance,
            Account, fix.Container.Name);

    // ── 1.2: chunk needs rehydration — verify pipeline initiates copy-to-rehydrate ──

    [Test]
    public async Task Restore_ChunkNeedsRehydration_InitiatesCopyToRehydrate()
    {
        var (fix, sim, chunkBlobName) = await SetupArchivedFixtureAsync(azurite);
        await using var _ = fix;

        // Simulate Archive tier (not yet rehydrating)
        sim.ArchiveTierBlobs.Add(chunkBlobName);

        var restoreOpts = new RestoreOptions
        {
            RootDirectory    = fix.RestoreRoot,
            Overwrite        = true,
            // Auto-confirm rehydration with Standard priority
            ConfirmRehydration = (_, __) => Task.FromResult<RehydratePriority?>(RehydratePriority.Standard),
        };

        var result = await MakeRestoreHandler(sim, fix)
            .Handle(new RestoreCommand(restoreOpts), default).AsTask();

        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.ChunksPendingRehydration.ShouldBe(1, "one chunk should be pending rehydration");

        // Pipeline should have issued exactly one copy-to-rehydrate call
        sim.CopyCalls.Count.ShouldBe(1, "expected exactly one rehydration copy call");
        sim.CopyCalls[0].Source.ShouldBe(chunkBlobName);
        sim.CopyCalls[0].Destination.ShouldBe(BlobPaths.ChunkRehydrated(chunkBlobName[BlobPaths.Chunks.Length..]));
    }

    // ── 1.3: chunk rehydration pending — verify no duplicate request issued ──────

    [Test]
    public async Task Restore_ChunkRehydrationPending_DoesNotIssueDuplicateRequest()
    {
        var (fix, sim, chunkBlobName) = await SetupArchivedFixtureAsync(azurite);
        await using var _ = fix;

        // Simulate Archive tier with rehydration in progress
        sim.RehydratingBlobs.Add(chunkBlobName);

        var restoreOpts = new RestoreOptions
        {
            RootDirectory    = fix.RestoreRoot,
            Overwrite        = true,
            ConfirmRehydration = (_, __) => Task.FromResult<RehydratePriority?>(RehydratePriority.Standard),
        };

        var result = await MakeRestoreHandler(sim, fix)
            .Handle(new RestoreCommand(restoreOpts), default).AsTask();

        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.ChunksPendingRehydration.ShouldBe(1, "chunk is still pending");

        // The pipeline does NOT re-issue CopyAsync for chunks already pending rehydration —
        // Azure returns BlobArchived 409 if StartCopyFromUri is called on an archived source
        // that already has a pending copy in progress.
        sim.CopyCalls.ShouldBeEmpty("pending chunk should NOT generate a new copy call — copy already in progress");
    }

    // ── 1.4: chunk already rehydrated — verify pipeline downloads from chunks-rehydrated/ ──

    [Test]
    public async Task Restore_ChunkAlreadyRehydrated_DownloadsFromRehydratedPath()
    {
        var (fix, sim, chunkBlobName) = await SetupArchivedFixtureAsync(azurite);
        await using var _ = fix;

        // The chunk blob name is e.g. "chunks/<hash>" — get just the hash
        var chunkHash = chunkBlobName[BlobPaths.Chunks.Length..];
        var rehydratedBlobName = BlobPaths.ChunkRehydrated(chunkHash);

        // Sideload: copy the real blob content to chunks-rehydrated/<hash>
        // (simulating what Azure does after rehydration completes)
        await using var srcStream = await fix.BlobContainer.DownloadAsync(chunkBlobName);
        using var ms = new MemoryStream();
        await srcStream.CopyToAsync(ms);
        ms.Position = 0;

        var origMeta = await fix.BlobContainer.GetMetadataAsync(chunkBlobName);
        await fix.BlobContainer.UploadAsync(
            blobName:   rehydratedBlobName,
            content:    ms,
            metadata:   origMeta.Metadata,
            tier:       BlobTier.Hot,
            overwrite:  false);

        // Mark the original chunk as Archive tier (not rehydrating)
        sim.ArchiveTierBlobs.Add(chunkBlobName);

        var restoreOpts = new RestoreOptions
        {
            RootDirectory = fix.RestoreRoot,
            Overwrite     = true,
        };

        var result = await MakeRestoreHandler(sim, fix)
            .Handle(new RestoreCommand(restoreOpts), default).AsTask();

        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.FilesRestored.ShouldBe(1, "file should be restored from the rehydrated blob");
        result.ChunksPendingRehydration.ShouldBe(0, "no chunks should be pending");

        // No copy calls — blob was already in chunks-rehydrated/
        sim.CopyCalls.ShouldBeEmpty("rehydrated blob already exists, no copy should be issued");
    }

    // ── Cleanup prompt should appear even when all files are identical (nothing to restore) ──

    [Test]
    public async Task Restore_Cleanup_PromptsWhenAllFilesIdentical()
    {
        var (fix, sim, chunkBlobName) = await SetupArchivedFixtureAsync(azurite);
        await using var _ = fix;

        var chunkHash = chunkBlobName[BlobPaths.Chunks.Length..];
        var rehydratedBlobName = BlobPaths.ChunkRehydrated(chunkHash);

        // First restore: get the file on disk so the second restore sees it as identical.
        var firstResult = await MakeRestoreHandler(sim, fix)
            .Handle(new RestoreCommand(new RestoreOptions
            {
                RootDirectory = fix.RestoreRoot,
                Overwrite     = false,
            }), default).AsTask();
        firstResult.Success.ShouldBeTrue(firstResult.ErrorMessage);
        firstResult.FilesRestored.ShouldBe(1);

        // Sideload a rehydrated blob (simulating a completed rehydration from a previous run)
        await using (var srcStream = await fix.BlobContainer.DownloadAsync(chunkBlobName))
        {
            using var ms = new MemoryStream();
            await srcStream.CopyToAsync(ms);
            ms.Position = 0;
            var origMeta = await fix.BlobContainer.GetMetadataAsync(chunkBlobName);
            await fix.BlobContainer.UploadAsync(rehydratedBlobName, ms, origMeta.Metadata, BlobTier.Hot);
        }

        // Now run restore again — all files are identical, nothing to restore.
        // But the cleanup prompt should still appear because there is a rehydrated blob.
        var cleanupInvoked = false;
        var  cleanupCount   = 0;

        var secondResult = await MakeRestoreHandler(sim, fix)
            .Handle(new RestoreCommand(new RestoreOptions
            {
                RootDirectory  = fix.RestoreRoot,
                Overwrite      = false,
                ConfirmCleanup = (count, bytes, ct) =>
                {
                    cleanupInvoked = true;
                    cleanupCount   = count;
                    return Task.FromResult(true);
                },
            }), default).AsTask();

        secondResult.Success.ShouldBeTrue(secondResult.ErrorMessage);
        secondResult.FilesRestored.ShouldBe(0, "all files are identical, nothing to restore");
        secondResult.FilesSkipped.ShouldBe(1, "one file should be skipped as identical");

        // Cleanup MUST be invoked even though no files were restored
        cleanupInvoked.ShouldBeTrue("ConfirmCleanup should be called even when all files are skipped");
        cleanupCount.ShouldBe(1, "cleanup should report the rehydrated blob");

        // After cleanup, no rehydrated blobs should remain
        var rehydratedAfter = new List<string>();
        await foreach (var name in fix.BlobContainer.ListAsync(BlobPaths.ChunksRehydrated))
            rehydratedAfter.Add(name);
        rehydratedAfter.ShouldBeEmpty("rehydrated blob should be deleted after cleanup");
    }

    // ── Cleanup should delete ALL rehydrated blobs, not just those used by the current restore ──

    [Test]
    public async Task Restore_Cleanup_DeletesAllRehydratedBlobs_NotJustCurrentRestores()
    {
        var (fix, sim, chunkBlobName) = await SetupArchivedFixtureAsync(azurite);
        await using var _ = fix;

        var chunkHash = chunkBlobName[BlobPaths.Chunks.Length..];
        var rehydratedBlobName = BlobPaths.ChunkRehydrated(chunkHash);

        // Sideload: copy the real chunk blob to chunks-rehydrated/ (simulating completed rehydration)
        await using (var srcStream = await fix.BlobContainer.DownloadAsync(chunkBlobName))
        {
            using var ms = new MemoryStream();
            await srcStream.CopyToAsync(ms);
            ms.Position = 0;
            var origMeta = await fix.BlobContainer.GetMetadataAsync(chunkBlobName);
            await fix.BlobContainer.UploadAsync(rehydratedBlobName, ms, origMeta.Metadata, BlobTier.Hot);
        }

        // Sideload an EXTRA rehydrated blob that is NOT related to the current restore.
        // This simulates a leftover rehydrated chunk from a previous restore of different files.
        var extraHash = "extra-orphan-hash-not-in-current-restore";
        var extraRehydratedBlob = BlobPaths.ChunkRehydrated(extraHash);
        using (var extraMs = new MemoryStream(new byte[] { 0x00, 0x01, 0x02 }))
        {
            await fix.BlobContainer.UploadAsync(extraRehydratedBlob, extraMs,
                new Dictionary<string, string>(), BlobTier.Hot);
        }

        // Verify both blobs exist in chunks-rehydrated/
        var rehydratedBefore = new List<string>();
        await foreach (var name in fix.BlobContainer.ListAsync(BlobPaths.ChunksRehydrated))
            rehydratedBefore.Add(name);
        rehydratedBefore.Count.ShouldBe(2, "both rehydrated blobs should exist before restore");

        // Mark the original chunk as Archive tier so restore uses the rehydrated copy
        sim.ArchiveTierBlobs.Add(chunkBlobName);

        var cleanupInvoked = false;
        var  cleanupCount   = 0;
        long cleanupBytes   = 0;

        var restoreOpts = new RestoreOptions
        {
            RootDirectory = fix.RestoreRoot,
            Overwrite     = true,
            ConfirmCleanup = (count, bytes, ct) =>
            {
                cleanupInvoked = true;
                cleanupCount   = count;
                cleanupBytes   = bytes;
                return Task.FromResult(true); // confirm deletion
            },
        };

        var result = await MakeRestoreHandler(sim, fix)
            .Handle(new RestoreCommand(restoreOpts), default).AsTask();

        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.FilesRestored.ShouldBe(1);

        // Cleanup should have been invoked
        cleanupInvoked.ShouldBeTrue("ConfirmCleanup should be called");

        // The count should include ALL rehydrated blobs (2), not just the one used by this restore (1)
        cleanupCount.ShouldBe(2, "cleanup should report ALL rehydrated blobs, including orphans from previous restores");

        // After cleanup, no rehydrated blobs should remain
        var rehydratedAfter = new List<string>();
        await foreach (var name in fix.BlobContainer.ListAsync(BlobPaths.ChunksRehydrated))
            rehydratedAfter.Add(name);
        rehydratedAfter.ShouldBeEmpty("all rehydrated blobs should be deleted after cleanup");
    }
}
