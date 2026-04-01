using Arius.Core.Features.Restore;
using Arius.Core.Shared.Storage;
using Arius.Integration.Tests.Storage;
using Shouldly;

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
        fix.WriteFile("data.bin", new byte[1024 * 1024]); // 1 MB
        var archiveResult = await fix.ArchiveAsync(new()
        {
            RootDirectory = fix.LocalRoot,
            UploadTier    = BlobTier.Archive,
        });
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        RestoreCostEstimate? capturedEstimate = null;

        var restoreOpts = new RestoreOptions
        {
            RootDirectory = fix.RestoreRoot,
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

        // 5.1: all 4 cost components should be non-zero for archive-tier restore
        capturedEstimate!.RetrievalCostStandard.ShouldBeGreaterThan(0,
            "Retrieval cost should be non-zero for archive-tier chunks");
        capturedEstimate.ReadOpsCostStandard.ShouldBeGreaterThan(0,
            "Read ops cost should be non-zero for archive-tier chunks");
        capturedEstimate.WriteOpsCost.ShouldBeGreaterThan(0,
            "Write ops cost should be non-zero for archive-tier chunks");
        capturedEstimate.StorageCost.ShouldBeGreaterThan(0,
            "Storage cost should be non-zero for archive-tier chunks");

        // High priority should always cost more than Standard
        capturedEstimate.TotalHigh.ShouldBeGreaterThan(capturedEstimate.TotalStandard);
    }

    // ── 5.2: Zero costs when no rehydration needed (Hot tier) ────────────────────

    [Test]
    public async Task Restore_HotTierChunks_CostEstimateHasZeroCosts()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Archive a file to Hot tier — no rehydration needed
        fix.WriteFile("data.bin", new byte[1024]);
        var archiveResult = await fix.ArchiveAsync(new()
        {
            RootDirectory = fix.LocalRoot,
            UploadTier    = BlobTier.Hot,
        });
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

        RestoreCostEstimate? capturedEstimate = null;

        var restoreOpts = new RestoreOptions
        {
            RootDirectory = fix.RestoreRoot,
            Overwrite     = true,
            ConfirmRehydration = (estimate, ct) =>
            {
                capturedEstimate = estimate;
                return Task.FromResult<RehydratePriority?>(RehydratePriority.Standard);
            },
        };

        var result = await fix.CreateRestoreHandler().Handle(new RestoreCommand(restoreOpts), CancellationToken.None);
        result.Success.ShouldBeTrue(result.ErrorMessage);

        // 5.2: For Hot-tier chunks, ConfirmRehydration should not be invoked at all
        // (no archive-tier chunks). The callback is only called when there are chunks needing rehydration.
        // Verify that either:
        //   (a) the callback was never invoked (typical path), OR
        //   (b) if it was invoked, all rehydration-related costs are zero
        if (capturedEstimate is not null)
        {
            capturedEstimate.ChunksNeedingRehydration.ShouldBe(0);
            capturedEstimate.ChunksPendingRehydration.ShouldBe(0);
            capturedEstimate.RetrievalCostStandard.ShouldBe(0.0);
            capturedEstimate.RetrievalCostHigh.ShouldBe(0.0);
            capturedEstimate.ReadOpsCostStandard.ShouldBe(0.0);
            capturedEstimate.ReadOpsCostHigh.ShouldBe(0.0);
            capturedEstimate.WriteOpsCost.ShouldBe(0.0);
            capturedEstimate.StorageCost.ShouldBe(0.0);
        }
    }
}
