using Arius.Core.Archive;
using Arius.Core.ChunkIndex;
using Arius.Core.Encryption;
using Arius.Core.Restore;
using Arius.Core.Storage;
using Arius.Integration.Tests.Storage;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Arius.Integration.Tests.Pipeline;

// ── Tier-simulating blob service wrapper ──────────────────────────────────────

/// <summary>
/// Wraps a real <see cref="IBlobStorageService"/> and overrides <see cref="GetMetadataAsync"/>
/// to simulate Archive-tier behaviour for a specific set of blob names, and <see cref="CopyAsync"/>
/// to record rehydration requests without actually copying.
/// Used to test restore pipeline rehydration state machine logic without real Azure Archive tier.
/// </summary>
internal sealed class RehydrationSimulatingBlobService(IBlobStorageService inner) : IBlobStorageService
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
        var archiveOpts = new ArchiveOptions
        {
            RootDirectory = fix.LocalRoot,
            UploadTier    = BlobTier.Hot,
        };
        var archiveResult = await fix.ArchiveAsync(archiveOpts);
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        // Find the tar blob (which is what the restore pipeline downloads / rehydrates).
        // Small files are bundled into tar chunks; the thin chunk is just a pointer to the tar.
        var chunkBlobName = string.Empty;
        await foreach (var name in fix.BlobStorage.ListAsync(BlobPaths.Chunks))
        {
            var meta = await fix.BlobStorage.GetMetadataAsync(name);
            if (meta.Metadata.TryGetValue(BlobMetadataKeys.AriusType, out var t) && t == BlobMetadataKeys.TypeTar)
            {
                chunkBlobName = name;
                break;
            }
        }
        chunkBlobName.ShouldNotBeNullOrEmpty("Expected at least one tar chunk blob");

        var sim = new RehydrationSimulatingBlobService(fix.BlobStorage);
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
        await using var srcStream = await fix.BlobStorage.DownloadAsync(chunkBlobName);
        using var ms = new MemoryStream();
        await srcStream.CopyToAsync(ms);
        ms.Position = 0;

        var origMeta = await fix.BlobStorage.GetMetadataAsync(chunkBlobName);
        await fix.BlobStorage.UploadAsync(
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
}
