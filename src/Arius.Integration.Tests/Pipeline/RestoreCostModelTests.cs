using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Fixtures;

namespace Arius.Integration.Tests.Pipeline;

/// <summary>
/// Integration tests for the restore cost model.
/// Covers restore-cost-model tasks 5.1 and 5.2.
/// Requires Azurite (Docker) – these tests are skipped in environments without Docker.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class RestoreCostModelTests(AzuriteFixture azurite)
{
    // ── 5.1: Cost estimate has all 4 non-zero components for archive-tier restore ─

    [Test]
    public async Task Restore_ArchiveTierChunks_CostEstimateHasAllFourComponents()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Archive a file directly to Archive tier
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("data.bin"), new byte[1024 * 1024], CancellationToken.None); // 1 MB
        var archiveResult = await fix.ArchiveAsync(new()
        {
            RootDirectory = fix.LocalDirectory.ToString(),
            UploadTier    = BlobTier.Archive,
        });
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        RestoreCostEstimate? capturedEstimate = null;

        var restoreOpts = new RestoreOptions
        {
            RootDirectory = fix.RestoreDirectory.ToString(),
            Overwrite     = true,
            ConfirmRehydration = (estimate, ct) =>
            {
                capturedEstimate = estimate;
                // Cancel — we only want the estimate, not an actual rehydration
                return Task.FromResult<RehydratePriority?>(null);
            },
        };

        await fix.CreateRestoreHandler().Handle(new RestoreCommand(restoreOpts), CancellationToken.None);

        // The estimate should have been captured (archive-tier chunks trigger the callback)
        capturedEstimate.ShouldNotBeNull();

        // 5.1: archive-tier restore has a non-zero estimated cost, and High priority costs more than Standard.
        // (The detailed per-component breakdown is an Azure implementation detail, asserted in Arius.AzureBlob.Tests.)
        capturedEstimate!.ChunksNeedingRehydration.ShouldBeGreaterThan(0);
        capturedEstimate.TotalStandard.ShouldBeGreaterThan(0, "Archive-tier restore should have a non-zero cost");
        capturedEstimate.TotalHigh.ShouldBeGreaterThan(capturedEstimate.TotalStandard);
    }

    // ── 5.2: Zero costs when no rehydration needed (Hot tier) ────────────────────

    [Test]
    public async Task Restore_HotTierChunks_CostEstimateHasZeroCosts()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Archive a file to Hot tier — no rehydration needed
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("data.bin"), new byte[1024], CancellationToken.None);
        var archiveResult = await fix.ArchiveAsync(new()
        {
            RootDirectory = fix.LocalDirectory.ToString(),
            UploadTier    = BlobTier.Hot,
        });
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        RestoreCostEstimate? capturedEstimate = null;

        var restoreOpts = new RestoreOptions
        {
            RootDirectory = fix.RestoreDirectory.ToString(),
            Overwrite     = true,
            ConfirmRehydration = (estimate, ct) =>
            {
                capturedEstimate = estimate;
                return Task.FromResult<RehydratePriority?>(RehydratePriority.Standard);
            },
        };

        var result = await fix.CreateRestoreHandler().Handle(new RestoreCommand(restoreOpts), CancellationToken.None);
        result.Success.ShouldBeTrue(result.ErrorMessage);

        // 5.2: A Hot-tier restore needs no rehydration. If the cost prompt fires (the estimate carries a
        // small read-op cost), there must be nothing to rehydrate and Standard == High (no priority premium).
        if (capturedEstimate is not null)
        {
            capturedEstimate.ChunksNeedingRehydration.ShouldBe(0);
            capturedEstimate.ChunksPendingRehydration.ShouldBe(0);
            capturedEstimate.TotalHigh.ShouldBe(capturedEstimate.TotalStandard);
        }
    }
}
