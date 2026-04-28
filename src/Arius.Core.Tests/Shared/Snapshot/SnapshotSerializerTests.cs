using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Snapshot;

namespace Arius.Core.Tests.Shared.Snapshot;

// ── 6.5 Unit tests: manifest serialization roundtrip, timestamp format ─────────

public class SnapshotSerializerTests
{
    [Test]
    public async Task Serialize_ThenDeserialize_Plaintext_RoundTrips()
    {
        var enc      = new PlaintextPassthroughService();
        var ts       = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero);
        var rootHash = FileTreeHash.Parse("a1b2c3d4" + new string('0', 56));
        var manifest = new SnapshotManifest
        {
            Timestamp    = ts,
            RootHash     = rootHash,
            FileCount    = 42,
            TotalSize    = 1024L * 1024 * 500,
            AriusVersion = "1.0.0"
        };

        var bytes = await SnapshotSerializer.SerializeAsync(manifest, enc);
        bytes.ShouldNotBeEmpty();

        var back = await SnapshotSerializer.DeserializeAsync(bytes, enc);

        back.Timestamp.ShouldBe(ts);
        back.RootHash.ShouldBe(manifest.RootHash);
        back.FileCount.ShouldBe(42);
        back.TotalSize.ShouldBe(manifest.TotalSize);
        back.AriusVersion.ShouldBe("1.0.0");
    }

    [Test]
    public async Task Serialize_ThenDeserialize_Passphrase_RoundTrips()
    {
        var enc      = new PassphraseEncryptionService("my-snapshot-pass");
        var ts       = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero);
        var rootHash = FileTreeHash.Parse("deadbeef" + new string('0', 56));
        var manifest = new SnapshotManifest
        {
            Timestamp    = ts,
            RootHash     = rootHash,
            FileCount    = 7,
            TotalSize    = 512,
            AriusVersion = "2.0.0-test"
        };

        var bytes = await SnapshotSerializer.SerializeAsync(manifest, enc);
        var back  = await SnapshotSerializer.DeserializeAsync(bytes, enc);

        back.RootHash.ShouldBe(manifest.RootHash);
        back.FileCount.ShouldBe(7);
    }

    [Test]
    public async Task Serialize_Plaintext_UsesStringRootHashJsonShape()
    {
        var enc      = new PlaintextPassthroughService();
        var ts       = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero);
        var rootHash = FileTreeHash.Parse("cafebabe" + new string('0', 56));
        var manifest = new SnapshotManifest
        {
            Timestamp    = ts,
            RootHash     = rootHash,
            FileCount    = 3,
            TotalSize    = 123,
            AriusVersion = "1.2.3"
        };

        var bytes = await SnapshotSerializer.SerializeAsync(manifest, enc);

        using var compressed = new MemoryStream(bytes);
        await using var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress);
        using var json = new MemoryStream();
        await gzip.CopyToAsync(json);

        System.Text.Encoding.UTF8.GetString(json.ToArray()).ShouldContain($"\"rootHash\":\"{rootHash}\"");
    }

    [Test]
    public void BlobName_TimestampFormat_MatchesSpec()
    {
        // Spec example: "2026-03-22T150000.000Z"
        var ts   = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero);
        var name = SnapshotService.BlobName(ts);

        name.ShouldBe("snapshots/2026-03-22T150000.000Z");
    }

    [Test]
    public void ParseTimestamp_RoundTrips()
    {
        var ts       = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero);
        var blobName = SnapshotService.BlobName(ts);
        var parsed   = SnapshotService.ParseTimestamp(blobName);

        parsed.ShouldBe(ts);
    }

    [Test]
    public void ParseTimestamp_WithoutPrefix_AlsoWorks()
    {
        var ts       = new DateTimeOffset(2024, 1, 15, 9, 30, 45, TimeSpan.Zero);
        var rawName  = ts.UtcDateTime.ToString(SnapshotService.TimestampFormat);
        var parsed   = SnapshotService.ParseTimestamp(rawName);

        parsed.ShouldBe(ts);
    }
}
