using Arius.Core.Shared;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.HashCache;

namespace Arius.Core.Tests.Shared.HashCache;

public class SparseFingerprintTests
{
    [Test]
    public void Regions_SmallFile_IsWholeFile()
    {
        var regions = SparseFingerprint.Regions(1000);
        regions.Count.ShouldBe(1);
        regions[0].ShouldBe((0L, 1000));
    }

    [Test]
    public void Regions_HugeFile_IsCappedAt64Blocks_HeadAndTailIncluded()
    {
        long size = 3L * 1024 * 1024 * 1024 * 1024; // 3 TiB
        var regions = SparseFingerprint.Regions(size);
        regions.Count.ShouldBe(64);
        regions[0].Offset.ShouldBe(0);
        regions[^1].Offset.ShouldBe(size - 256 * 1024);
        // offsets strictly non-decreasing
        for (var i = 1; i < regions.Count; i++)
            regions[i].Offset.ShouldBeGreaterThanOrEqualTo(regions[i - 1].Offset);
    }

    [Test]
    public void ComputeBySeeking_IsDeterministic_AndChangesWhenSampledRegionChanges()
    {
        var (fs, path) = WriteTempFile(Enumerable.Range(0, 2_000_000).Select(i => (byte)i).ToArray());
        var size = fs.GetFileSize(path);

        var fp1 = SparseFingerprint.ComputeBySeeking(fs, path, size);
        var fp2 = SparseFingerprint.ComputeBySeeking(fs, path, size);
        fp1.ShouldBe(fp2);
        fp1.Length.ShouldBe(32);

        // Flip a byte in the head region → fingerprint must change.
        var bytes = fs.ReadAllBytes(path);
        bytes[0] ^= 0xFF;
        fs.WriteAllBytes(path, bytes);
        SparseFingerprint.ComputeBySeeking(fs, path, size).ShouldNotBe(fp1);
    }

    [Test]
    public void Sampler_MatchesSeekingFingerprint_ForSameContent()
    {
        var data = Enumerable.Range(0, 2_000_000).Select(i => (byte)(i * 7)).ToArray();
        var (fs, path) = WriteTempFile(data);
        var size = fs.GetFileSize(path);

        var seekFp = SparseFingerprint.ComputeBySeeking(fs, path, size);

        // Drive the sampler the way a sequential read would.
        var sampler = new SparseFingerprint.Sampler(size);
        var pos = 0;
        const int chunk = 64 * 1024;
        while (pos < data.Length)
        {
            var len = Math.Min(chunk, data.Length - pos);
            sampler.Capture(pos, data.AsSpan(pos, len));
            pos += len;
        }
        sampler.Finish().ShouldBe(seekFp);
    }

    private static (RelativeFileSystem fs, RelativePath path) WriteTempFile(byte[] data)
    {
        var dir  = LocalDirectory.Parse(Path.Combine(Path.GetTempPath(), $"arius-fp-{Guid.NewGuid():N}"));
        var fs   = new RelativeFileSystem(dir);
        var path = RelativePath.Parse("blob.bin");
        fs.WriteAllBytes(path, data);
        return (fs, path);
    }
}
