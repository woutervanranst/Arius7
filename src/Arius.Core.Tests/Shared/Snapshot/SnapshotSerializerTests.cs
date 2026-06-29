using Arius.Core.Shared.Compression;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Snapshot;
using Arius.Tests.Shared;

namespace Arius.Core.Tests.Shared.Snapshot;

// ── 6.5 Unit tests: manifest serialization roundtrip, timestamp format ─────────

public class SnapshotSerializerTests
{
    [Test]
    public async Task Serialize_ThenDeserialize_Plaintext_RoundTrips()
    {
        var ts       = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero);
        var rootHash = FileTreeHash.Parse("a1b2c3d4" + new string('0', 56));
        var manifest = new SnapshotManifest
        {
            Timestamp    = ts,
            RootHash     = rootHash,
            FileCount    = 42,
            OriginalSize    = 1024L * 1024 * 500,
            AriusVersion = "1.0.0"
        };

        var bytes = await SnapshotSerializer.SerializeAsync(manifest, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        bytes.ShouldNotBeEmpty();

        var back = await SnapshotSerializer.DeserializeAsync(bytes, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);

        back.Timestamp.ShouldBe(ts);
        back.RootHash.ShouldBe(manifest.RootHash);
        back.FileCount.ShouldBe(42);
        back.OriginalSize.ShouldBe(manifest.OriginalSize);
        back.AriusVersion.ShouldBe("1.0.0");
    }

    [Test]
    public async Task Serialize_ThenDeserialize_Passphrase_RoundTrips()
    {
        var enc      = IEncryptionService.EncryptedInstance;
        var ts       = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero);
        var rootHash = FileTreeHash.Parse("deadbeef" + new string('0', 56));
        var manifest = new SnapshotManifest
        {
            Timestamp    = ts,
            RootHash     = rootHash,
            FileCount    = 7,
            OriginalSize    = 512,
            AriusVersion = "2.0.0-test"
        };

        var bytes = await SnapshotSerializer.SerializeAsync(manifest, enc, ICompressionService.ZtdInstance);
        var back  = await SnapshotSerializer.DeserializeAsync(bytes, enc, ICompressionService.ZtdInstance);

        back.RootHash.ShouldBe(manifest.RootHash);
        back.FileCount.ShouldBe(7);
    }

    [Test]
    public async Task Serialize_Plaintext_UsesStringRootHashJsonShape()
    {
        var ts       = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero);
        var rootHash = FileTreeHash.Parse("cafebabe" + new string('0', 56));
        var manifest = new SnapshotManifest
        {
            Timestamp    = ts,
            RootHash     = rootHash,
            FileCount    = 3,
            OriginalSize    = 123,
            AriusVersion = "1.2.3"
        };

        var bytes = await SnapshotSerializer.SerializeAsync(manifest, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);

        using var compressed = new MemoryStream(bytes);
        await using var decompressed = ICompressionService.ZtdInstance.WrapForDecompression(compressed);
        using var json = new MemoryStream();
        await decompressed.CopyToAsync(json);

        System.Text.Encoding.UTF8.GetString(json.ToArray()).ShouldContain($"\"rootHash\":\"{rootHash}\"");
    }

    // ── Passphrase mismatch → friendly RepositoryEncryptionException ───────────────

    [Test]
    public async Task Deserialize_EncryptedBytes_WithoutPassphrase_ThrowsRepositoryEncryptionException()
    {
        // The most common mistake: an encrypted repository read with no passphrase. The ciphertext
        // reaches the decompressor and would otherwise surface as a cryptic "unrecognized compression
        // format" error.
        var bytes = await SnapshotSerializer.SerializeAsync(Manifest(), IEncryptionService.EncryptedInstance, ICompressionService.ZtdInstance);

        var ex = await Should.ThrowAsync<RepositoryEncryptionException>(
            async () => await SnapshotSerializer.DeserializeAsync(bytes, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance));

        ex.PassphraseProvided.ShouldBeFalse();
        ex.InnerException.ShouldNotBeNull();
    }

    [Test]
    public async Task Deserialize_EncryptedBytes_WithWrongPassphrase_ThrowsRepositoryEncryptionException()
    {
        var bytes = await SnapshotSerializer.SerializeAsync(Manifest(), IEncryptionService.EncryptedInstance, ICompressionService.ZtdInstance);

        var ex = await Should.ThrowAsync<RepositoryEncryptionException>(
            async () => await SnapshotSerializer.DeserializeAsync(bytes, new PassphraseEncryptionService("not-the-right-passphrase"), ICompressionService.ZtdInstance));

        ex.PassphraseProvided.ShouldBeTrue();
    }

    [Test]
    public async Task Deserialize_PlaintextBytes_WithPassphrase_ThrowsRepositoryEncryptionException()
    {
        var bytes = await SnapshotSerializer.SerializeAsync(Manifest(), IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);

        var ex = await Should.ThrowAsync<RepositoryEncryptionException>(
            async () => await SnapshotSerializer.DeserializeAsync(bytes, IEncryptionService.EncryptedInstance, ICompressionService.ZtdInstance));

        ex.PassphraseProvided.ShouldBeTrue();
    }

    private static SnapshotManifest Manifest() => new()
    {
        Timestamp    = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero),
        RootHash     = FileTreeHash.Parse("a1b2c3d4" + new string('0', 56)),
        FileCount    = 1,
        OriginalSize = 1,
        AriusVersion = "test"
    };

    [Test]
    public void BlobName_TimestampFormat_MatchesSpec()
    {
        // Spec example: "2026-03-22T150000.000Z"
        var ts   = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero);
        var name = BlobPaths.SnapshotPath(ts);

        name.ShouldBe(Arius.Core.Shared.FileSystem.RelativePath.Parse("snapshots/2026-03-22T150000.000Z"));
    }

    [Test]
    public void ParseTimestamp_RoundTrips()
    {
        var ts       = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero);
        var blobName = BlobPaths.SnapshotPath(ts);
        var parsed   = SnapshotService.ParseTimestamp(blobName);

        parsed.ShouldBe(ts);
    }

    [Test]
    public void ParseTimestamp_WithoutPrefix_AlsoWorks()
    {
        var ts       = new DateTimeOffset(2024, 1, 15, 9, 30, 45, TimeSpan.Zero);
        var rawName  = ts.UtcDateTime.ToString(SnapshotService.TimestampFormat);
        var parsed   = SnapshotService.ParseTimestamp(RelativePath.Parse(rawName));

        parsed.ShouldBe(ts);
    }

    [Test]
    public void ParseTimestamp_NonSnapshotMultiSegmentPath_Throws()
    {
        Should.Throw<FormatException>(() => SnapshotService.ParseTimestamp(Arius.Core.Shared.FileSystem.RelativePath.Parse("other/2024-01-15T093045.000Z")));
    }
}
