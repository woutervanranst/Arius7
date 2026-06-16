using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Storage;
using Arius.Integration.Tests.Pipeline.Fakes;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;

namespace Arius.Integration.Tests.Pipeline;

/// <summary>
/// Integration tests specifically covering AES-256-GCM encryption end-to-end.
/// Covers tasks 6.1 (large file GCM roundtrip), 6.2 (tar bundle GCM roundtrip),
/// and 6.3 (mixed CBC+GCM archive — restore must handle both).
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class GcmIntegrationTests(AzuriteFixture azurite)
{
    // ── 6.1: GCM large file roundtrip ────────────────────────────────────────

    [Test]
    public async Task Archive_GcmEncrypted_LargeFile_Restore_ByteIdentical()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // 2 MB > 1 MB threshold → large pipeline
        var original = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(original);
        var relativePath = RelativePath.Parse("large.bin");
        await fix.LocalFileSystem.WriteAllBytesAsync(relativePath, original, CancellationToken.None);

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesUploaded.ShouldBe(1);

        // Verify exactly one chunk was uploaded
        var blobs = new List<RelativePath>();
        await foreach (var item in fix.BlobContainer.ListAsync(BlobPaths.ChunksPrefix, includeMetadata: false))
            blobs.Add(item.Name);
        blobs.Count.ShouldBe(1);

        var meta = await fix.BlobContainer.GetMetadataAsync(blobs[0]);
        meta.Exists.ShouldBeTrue();
        meta.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeLarge);

        // Verify the blob was GCM-encrypted: first 6 bytes must be the ArGCM1 magic
        var download = await fix.BlobContainer.DownloadAsync(blobs[0]);
        await using (var blobStream = download.Stream)
        {
            var magic = new byte[6];
            await blobStream.ReadExactlyAsync(magic);
            System.Text.Encoding.ASCII.GetString(magic).ShouldBe("ArGCM1");
        }

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(1);

        fix.RestoreFileSystem.ReadAllBytes(relativePath).ShouldBe(original);
    }

    // ── 6.2: GCM tar bundle roundtrip ────────────────────────────────────────

    [Test]
    public async Task Archive_GcmEncrypted_TarBundle_Restore_ByteIdentical()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);

        // Small files → tar-bundled pipeline
        var content1 = new byte[512];
        var content2 = new byte[1024];
        Random.Shared.NextBytes(content1);
        Random.Shared.NextBytes(content2);
        var relativePath1 = RelativePath.Parse("small1.bin");
        var relativePath2 = RelativePath.Parse("small2.bin");
        await fix.LocalFileSystem.WriteAllBytesAsync(relativePath1, content1, CancellationToken.None);
        await fix.LocalFileSystem.WriteAllBytesAsync(relativePath2, content2, CancellationToken.None);

        var archiveResult = await fix.ArchiveAsync();
        archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
        archiveResult.FilesUploaded.ShouldBe(2);

        // Verify tar chunk was uploaded
        var tarBlobs = new List<RelativePath>();
        await foreach (var item in fix.BlobContainer.ListAsync(BlobPaths.ChunksPrefix, includeMetadata: false))
        {
            var b = item.Name;
            var m = await fix.BlobContainer.GetMetadataAsync(b);
            if (m.Metadata.TryGetValue(BlobMetadataKeys.AriusType, out var t) && t == BlobMetadataKeys.TypeTar)
                tarBlobs.Add(b);
        }
        tarBlobs.Count.ShouldBe(1);

        var restoreResult = await fix.RestoreAsync();
        restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
        restoreResult.FilesRestored.ShouldBe(2);

        fix.RestoreFileSystem.ReadAllBytes(relativePath1).ShouldBe(content1);
        fix.RestoreFileSystem.ReadAllBytes(relativePath2).ShouldBe(content2);
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
            new CbcEncryptionServiceAdapter(TestDefaults.Passphrase));

        var cbcLargeContent = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(cbcLargeContent);
        var cbcLargePath = RelativePath.Parse("cbc-large.bin");
        await cbcFix.LocalFileSystem.WriteAllBytesAsync(cbcLargePath, cbcLargeContent, CancellationToken.None);

        var cbcResult = await cbcFix.ArchiveAsync();
        cbcResult.Success.ShouldBeTrue(cbcResult.ErrorMessage);
        cbcResult.FilesUploaded.ShouldBe(1);

        // Phase 2: archive more files using new GCM encryption into the same container.
        // Also place cbc-large.bin in gcmFix's local directory so the new GCM snapshot includes it.
        // Its hash is already in the chunk index (from cbcFix), so the chunk is deduplicated —
        // no re-upload happens, but the file appears in the latest snapshot that restore reads.
        // When restore downloads that chunk it auto-detects the Salted__ magic and decrypts via CBC.
        await using var gcmFix = await PipelineFixture.CreateAsyncWithEncryption(
            azurite,
            IEncryptionService.EncryptedInstance,
            existingContainer: cbcFix.Container);

        await gcmFix.LocalFileSystem.WriteAllBytesAsync(cbcLargePath, cbcLargeContent, CancellationToken.None); // deduplicates against cbcFix chunk

        var gcmContent = new byte[300];
        Random.Shared.NextBytes(gcmContent);
        var gcmSmallPath = RelativePath.Parse("gcm-small.bin");
        await gcmFix.LocalFileSystem.WriteAllBytesAsync(gcmSmallPath, gcmContent, CancellationToken.None);

        var gcmResult = await gcmFix.ArchiveAsync();
        gcmResult.Success.ShouldBeTrue(gcmResult.ErrorMessage);
        gcmResult.FilesUploaded.ShouldBe(1); // only gcm-small.bin is new; cbc-large.bin deduplicates

        // Assert that the container holds both a CBC blob (Salted__ magic) and a GCM blob (ArGCM1 magic),
        // confirming the mixed-format scenario before restore exercises auto-detection.
        var hasCbc = false;
        var hasGcm = false;
        await foreach (var item in gcmFix.BlobContainer.ListAsync(BlobPaths.ChunksPrefix, includeMetadata: false))
        {
            var blobName = item.Name;
            var header = new byte[8];
            var download = await gcmFix.BlobContainer.DownloadAsync(blobName);
            await using var s = download.Stream;
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
        gcmFix.RestoreFileSystem.ReadAllBytes(gcmSmallPath).ShouldBe(gcmContent);

        // CBC-archived file must also be restored correctly (auto-detection of legacy format)
        gcmFix.RestoreFileSystem.ReadAllBytes(cbcLargePath).ShouldBe(cbcLargeContent);
    }

}
