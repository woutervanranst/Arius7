using System.IO.Compression;
using System.Text;
using Arius.Core.Shared.Compression;
using Arius.Tests.Shared.Compression;

namespace Arius.Core.Tests.Shared.Compression;

/// <summary>
/// Tests for the zstd compression service: round-trip correctness, standard-frame output, legacy gzip
/// backwards-compatibility on read, and that corruption is loud (never silently wrong bytes).
/// </summary>
public class ZstdCompressionServiceTests
{
    private static readonly ICompressionService Sut = TestCompression.Instance;

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(100)]
    [Arguments(64 * 1024)]
    [Arguments(5 * 1024 * 1024)]
    public async Task Compress_Then_Decompress_RoundTrips_ByteForByte(int size)
    {
        var original = new byte[size];
        Random.Shared.NextBytes(original);

        var compressed = await CompressAsync(original);
        var restored   = await DecompressAsync(compressed);

        restored.ShouldBe(original);
    }

    [Test]
    public async Task WritesStandardZstdFrame_WithMagicNumber()
    {
        var compressed = await CompressAsync([1, 2, 3, 4, 5]);

        // RFC 8878 frame magic 0xFD2FB528, little-endian on the wire.
        compressed.Length.ShouldBeGreaterThanOrEqualTo(4);
        compressed[0].ShouldBe((byte)0x28);
        compressed[1].ShouldBe((byte)0xB5);
        compressed[2].ShouldBe((byte)0x2F);
        compressed[3].ShouldBe((byte)0xFD);
    }

    [Test]
    public async Task Decompress_AutoDetects_LegacyGzipBlob()
    {
        var original = Encoding.UTF8.GetBytes("content written by the gzip era of Arius");

        var gz = new MemoryStream();
        await using (var g = new GZipStream(gz, CompressionLevel.SmallestSize, leaveOpen: true))
            await g.WriteAsync(original);

        var restored = await DecompressAsync(gz.ToArray());

        restored.ShouldBe(original);
    }

    [Test]
    public async Task Decompress_TreatsEmptyLegacyBlob_AsEmptyContent()
    {
        // Legacy gzip wrote empty content (e.g. an empty filetree) as a 0-byte blob.
        var restored = await DecompressAsync([]);

        restored.Length.ShouldBe(0);
    }

    [Test]
    public async Task Decompress_CorruptZstdFrame_ThrowsInsteadOfReturningWrongBytes()
    {
        var original = new byte[8192];
        Random.Shared.NextBytes(original);
        var compressed = await CompressAsync(original);

        // Flip a byte in the frame body (past the 4-byte magic). With the content checksum enabled,
        // any surviving decode must fail the XXH64 check rather than silently emit wrong bytes.
        compressed[compressed.Length / 2] ^= 0xFF;

        await Should.ThrowAsync<Exception>(async () => await DecompressAsync(compressed));
    }

    [Test]
    public async Task Decompress_UnrecognizedHeader_Throws()
    {
        await Should.ThrowAsync<InvalidDataException>(async () => await DecompressAsync([0x00, 0x11, 0x22, 0x33, 0x44]));
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
