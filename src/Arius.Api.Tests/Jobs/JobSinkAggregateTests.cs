using Arius.Api.Jobs;
using Arius.Core.Shared.Hashes;

namespace Arius.Api.Tests.Jobs;

public class JobSinkAggregateTests
{
    private static JobSink NewArchiveSink() => new();   // inert (no hub) — aggregation is hub-independent

    [Test]
    public async Task Byte_layers_and_dedup_accumulate_as_original_bytes()
    {
        var s = NewArchiveSink();
        s.SetTotals(files: 3, bytes: 3000);
        s.AddScanned(3000);
        s.AddHashed(3000);
        s.AddUploaded(stored: 400, original: 2000);
        s.AddDeduped(original: 1000);

        var snap = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
        await Assert.That(snap.TotalBytes).IsEqualTo(3000L);
        await Assert.That(snap.ScannedBytes).IsEqualTo(3000L);
        await Assert.That(snap.HashedBytes).IsEqualTo(3000L);
        await Assert.That(snap.UploadedBytes).IsEqualTo(2000L);   // original units, not stored
        await Assert.That(snap.DedupedBytes).IsEqualTo(1000L);
        await Assert.That(snap.TotalNewBytes).IsEqualTo(2000L);   // total - deduped
    }

    [Test]
    public async Task Tar_uploaded_bytes_use_remembered_uncompressed_size()
    {
        var s = NewArchiveSink();
        var tar = ChunkHash.Parse(new string('a', 64));
        s.RememberTar(tar, uncompressed: 5000);
        s.AddUploadedTar(tar);

        var snap = s.BuildSnapshot(DateTimeOffset.UnixEpoch);
        await Assert.That(snap.UploadedBytes).IsEqualTo(5000L);
    }
}
