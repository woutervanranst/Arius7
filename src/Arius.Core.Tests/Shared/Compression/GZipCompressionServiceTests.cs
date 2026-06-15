using System.IO.Compression;
using System.Text;
using Arius.Core.Shared.Compression;

namespace Arius.Core.Tests.Shared.Compression;

/// <summary>
/// Tests for the legacy gzip codec: round-trip correctness, interop with the BCL <see cref="GZipStream"/>
/// (so old "+gzip" blobs decode), header detection, and that it opts out of inline round-trip verification.
/// </summary>
public class GZipCompressionServiceTests
{
    private static readonly ICompressionService Sut = new GZipCompressionService();

    [Test]
    public void DoesNotRequireRoundTripVerification()
    {
        // The BCL gzip encoder is mature and trusted, so chunk uploads skip inline verification. (Contrast zstd.)
        Sut.RequireRoundTripVerification.ShouldBeFalse();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(100)]
    [Arguments(64 * 1024)]
    public async Task Compress_Then_Decompress_RoundTrips_ByteForByte(int size)
    {
        var original = new byte[size];
        Random.Shared.NextBytes(original);

        var compressed = await CompressAsync(original);
        var restored   = await DecompressAsync(compressed);

        restored.ShouldBe(original);
    }

    [Test]
    public async Task WritesStandardGZipFrame_WithMagicNumber()
    {
        var compressed = await CompressAsync(Encoding.UTF8.GetBytes("hello"));

        compressed.Length.ShouldBeGreaterThanOrEqualTo(2);
        compressed[0].ShouldBe((byte)0x1F);
        compressed[1].ShouldBe((byte)0x8B);
        GZipCompressionService.IsGZipHeader(compressed).ShouldBeTrue();
    }

    [Test]
    public async Task Decompress_ReadsBlobWrittenByBclGZipStream()
    {
        // The whole reason this codec exists: blobs written by the gzip era of Arius (raw BCL GZipStream)
        // must still decode.
        var original = Encoding.UTF8.GetBytes("content written by the gzip era of Arius");

        var gz = new MemoryStream();
        await using (var g = new GZipStream(gz, CompressionLevel.SmallestSize, leaveOpen: true))
            await g.WriteAsync(original);

        var restored = await DecompressAsync(gz.ToArray());

        restored.ShouldBe(original);
    }

    [Test]
    public void IsGZipHeader_RejectsNonGZipAndTooShortHeaders()
    {
        GZipCompressionService.IsGZipHeader([0x28, 0xB5, 0x2F, 0xFD]).ShouldBeFalse(); // zstd magic
        GZipCompressionService.IsGZipHeader([0x1F]).ShouldBeFalse();                   // truncated
        GZipCompressionService.IsGZipHeader([]).ShouldBeFalse();                       // empty
    }

    private static async Task<byte[]> CompressAsync(byte[] data)
    {
        var ms = new MemoryStream();
        await using (var compress = Sut.WrapForCompression(ms))
            await compress.WriteAsync(data);
        return ms.ToArray();
    }

    private static async Task<byte[]> DecompressAsync(byte[] data)
    {
        await using var source     = new MemoryStream(data);
        await using var decompress = Sut.WrapForDecompression(source, leaveOpen: true);
        var output = new MemoryStream();
        await decompress.CopyToAsync(output);
        return output.ToArray();
    }
}
