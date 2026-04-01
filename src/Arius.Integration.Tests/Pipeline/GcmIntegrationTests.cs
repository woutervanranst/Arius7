using Arius.Core.Encryption;
using Arius.Core.Storage;
using Arius.Integration.Tests.Storage;
using Shouldly;

namespace Arius.Integration.Tests.Pipeline;

/// <summary>
/// Integration tests specifically covering AES-256-GCM encryption end-to-end.
/// Covers tasks 6.1 (large file GCM roundtrip), 6.2 (tar bundle GCM roundtrip),
/// and 6.3 (mixed CBC+GCM archive — restore must handle both).
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class GcmIntegrationTests(AzuriteFixture azurite)
{
    private const string Passphrase = "gcm-integration-test-passphrase";

    // ── 6.1: GCM large file roundtrip ────────────────────────────────────────

    [Test]
    public async Task Archive_GcmEncrypted_LargeFile_Restore_ByteIdentical()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite, passphrase: Passphrase);

        // 2 MB > 1 MB threshold → large pipeline
        var original = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(original);
        fix.WriteFile("large.bin", original);

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesUploaded.ShouldBe(1);

        // Verify exactly one chunk was uploaded
        var blobs = new List<string>();
        await foreach (var b in fix.BlobContainer.ListAsync("chunks/"))
            blobs.Add(b);
        blobs.Count.ShouldBe(1);

        var meta = await fix.BlobContainer.GetMetadataAsync(blobs[0]);
        meta.Exists.ShouldBeTrue();
        meta.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeLarge);

        // Verify the blob was GCM-encrypted: first 6 bytes must be the ArGCM1 magic
        await using (var blobStream = await fix.BlobContainer.DownloadAsync(blobs[0]))
        {
            var magic = new byte[6];
            await blobStream.ReadExactlyAsync(magic);
            System.Text.Encoding.ASCII.GetString(magic).ShouldBe("ArGCM1");
        }

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);

        fix.ReadRestored("large.bin").ShouldBe(original);
    }

    // ── 6.2: GCM tar bundle roundtrip ────────────────────────────────────────

    [Test]
    public async Task Archive_GcmEncrypted_TarBundle_Restore_ByteIdentical()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite, passphrase: Passphrase);

        // Small files → tar-bundled pipeline
        var content1 = new byte[512];
        var content2 = new byte[1024];
        Random.Shared.NextBytes(content1);
        Random.Shared.NextBytes(content2);
        fix.WriteFile("small1.bin", content1);
        fix.WriteFile("small2.bin", content2);

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesUploaded.ShouldBe(2);

        // Verify tar chunk was uploaded
        var tarBlobs = new List<string>();
        await foreach (var b in fix.BlobContainer.ListAsync("chunks/"))
        {
            var m = await fix.BlobContainer.GetMetadataAsync(b);
            if (m.Metadata.TryGetValue(BlobMetadataKeys.AriusType, out var t) && t == BlobMetadataKeys.TypeTar)
                tarBlobs.Add(b);
        }
        tarBlobs.Count.ShouldBe(1);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(2);

        fix.ReadRestored("small1.bin").ShouldBe(content1);
        fix.ReadRestored("small2.bin").ShouldBe(content2);
    }

    // ── 6.3: Mixed CBC+GCM archive roundtrip ─────────────────────────────────

    /// <summary>
    /// Simulates a pre-existing CBC-encrypted archive (legacy format) followed by
    /// a new GCM-encrypted archive run. Restore must recover all files correctly
    /// because <see cref="PassphraseEncryptionService.WrapForDecryption"/> auto-detects
    /// the magic bytes and dispatches to the correct decryptor.
    /// </summary>
    [Test]
    public async Task Archive_MixedCbcAndGcm_Restore_AllFiles_ByteIdentical()
    {
        // Phase 1: archive some files using legacy CBC encryption ─────────────
        // Use a CbcEncryptionServiceAdapter that writes CBC but delegates all
        // other operations (hash, IsEncrypted, WrapForDecryption) to the real
        // PassphraseEncryptionService so the chunk index + restore path work.
        await using var cbcFix = await PipelineFixture.CreateAsyncWithEncryption(
            azurite,
            new CbcEncryptionServiceAdapter(Passphrase));

        var cbcLargeContent = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(cbcLargeContent);
        cbcFix.WriteFile("cbc-large.bin", cbcLargeContent);

        var cbcResult = await cbcFix.ArchiveAsync();
        cbcResult.Success.ShouldBeTrue(cbcResult.ErrorMessage);
        cbcResult.FilesUploaded.ShouldBe(1);

        // Phase 2: archive more files using new GCM encryption into the same container.
        // Also place cbc-large.bin in gcmFix's LocalRoot so the new GCM snapshot includes it.
        // Its hash is already in the chunk index (from cbcFix), so the chunk is deduplicated —
        // no re-upload happens, but the file appears in the latest snapshot that restore reads.
        // When restore downloads that chunk it auto-detects the Salted__ magic and decrypts via CBC.
        await using var gcmFix = await PipelineFixture.CreateAsyncWithEncryption(
            azurite,
            new PassphraseEncryptionService(Passphrase),
            existingContainer: cbcFix.Container);

        gcmFix.WriteFile("cbc-large.bin", cbcLargeContent); // deduplicates against cbcFix chunk

        var gcmContent = new byte[300];
        Random.Shared.NextBytes(gcmContent);
        gcmFix.WriteFile("gcm-small.bin", gcmContent);

        var gcmResult = await gcmFix.ArchiveAsync();
        gcmResult.Success.ShouldBeTrue(gcmResult.ErrorMessage);
        gcmResult.FilesUploaded.ShouldBe(1); // only gcm-small.bin is new; cbc-large.bin deduplicates

        // Assert that the container holds both a CBC blob (Salted__ magic) and a GCM blob (ArGCM1 magic),
        // confirming the mixed-format scenario before restore exercises auto-detection.
        var hasCbc = false;
        var hasGcm = false;
        await foreach (var blobName in gcmFix.BlobContainer.ListAsync("chunks/"))
        {
            var header = new byte[8];
            await using var s = await gcmFix.BlobContainer.DownloadAsync(blobName);
            _ = await s.ReadAsync(header);

            if (System.Text.Encoding.ASCII.GetString(header, 0, 8) == "Salted__") hasCbc = true;
            if (System.Text.Encoding.ASCII.GetString(header, 0, 6) == "ArGCM1") hasGcm = true;
        }
        hasCbc.ShouldBeTrue("expected at least one CBC-encrypted (Salted__) blob in the container");
        hasGcm.ShouldBeTrue("expected at least one GCM-encrypted (ArGCM1) blob in the container");

        // Phase 3: restore from the GCM fixture (latest snapshot has both files).
        // The restore auto-detects magic bytes, so both CBC and GCM blobs are readable.
        var restoreResult = await gcmFix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(2);

        // GCM-archived file must be restored correctly
        gcmFix.ReadRestored("gcm-small.bin").ShouldBe(gcmContent);

        // CBC-archived file must also be restored correctly (auto-detection of legacy format)
        gcmFix.ReadRestored("cbc-large.bin").ShouldBe(cbcLargeContent);
    }

}

