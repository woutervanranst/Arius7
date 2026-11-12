using System.IO.Compression;
using System.Text;
using Arius.Core.Shared.Compression;

namespace Arius.Core.Tests.Shared.Compression;

public class ZstdCompressionServiceTests
{
    private static readonly ICompressionService Sut = new ZstdCompressionService();

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
    [Arguments(1024)]
    [Arguments(64 * 1024)]
    [Arguments(8 * 1024 * 1024)]
    public async Task RoundTripTest(int size)
    {
        // Exercise match finding and entropy coding with compressible input.
        foreach (var original in new[] { Compressible(size), Textual(size) })
        {
            var compressed = await CompressAsync(original);

            compressed.Length.ShouldBeLessThan(original.Length);

            var restored = await DecompressAsync(compressed);
            restored.ShouldBe(original);
        }
    }

    [Test]
    public async Task WritesStandardZstdFrame_WithMagicNumber()
    {
        var compressed = await CompressAsync([1, 2, 3, 4, 5]);

        // RFC 8878 frame magic in little-endian order.
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
        var restored = await DecompressAsync([]);

        restored.Length.ShouldBe(0);
    }

    [Test]
    public async Task Decompress_CorruptZstdFrame_ThrowsInsteadOfReturningWrongBytes()
    {
        var original = new byte[8192];
        Random.Shared.NextBytes(original);
        var compressed = await CompressAsync(original);

        // Corrupt the frame body without changing the format-detection header.
        compressed[compressed.Length / 2] ^= 0xFF;

        await Should.ThrowAsync<InvalidDataException>(async () => await DecompressAsync(compressed));
    }

    [Test]
    public async Task Decompress_TruncatedZstdFrame_ThrowsInsteadOfReturningPartialBytes()
    {
        // Truncation is detected only when the decoder reaches the end of the stream.
        var original = new byte[8192];
        Random.Shared.NextBytes(original);
        var compressed = await CompressAsync(original);

        var truncated = compressed[..(compressed.Length / 2)];

        await Should.ThrowAsync<InvalidDataException>(async () => await DecompressAsync(truncated));
    }

    [Test]
    public async Task Decompress_UnrecognizedHeader_Throws()
    {
        await Should.ThrowAsync<InvalidDataException>(async () => await DecompressAsync([0x00, 0x11, 0x22, 0x33, 0x44]));
    }

    [Test]
    public async Task Decompress_RoundTrips_ViaByteArrayReadOverloads()
    {
        var original = new byte[20_000];
        Random.Shared.NextBytes(original);
        var compressed = await CompressAsync(original);

        (await ReadAllViaByteArrayAsync(compressed)).ShouldBe(original);
        ReadAllViaByteArraySync(compressed).ShouldBe(original);
    }

    [Test]
    public async Task Decompress_RejectsWindowLargerThan128MiB()
    {
        // An empty frame exercises the window limit without a large payload.
        byte[] frame = [0x28, 0xB5, 0x2F, 0xFD, 0x00, 0x88, 0x01, 0x00, 0x00];
        (await DecompressAsync(frame)).ShouldBeEmpty();
        frame[5] = 0x90; // Window descriptor: 256 MiB.
        await Should.ThrowAsync<IOException>(async () => await DecompressAsync(frame));
    }

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    public async Task Decompress_MissingChecksumTrailer_ThrowsInSyncAndAsyncReads(int missingBytes)
    {
        var compressed = await CompressAsync(Textual(8192));
        var truncated = compressed[..^missingBytes];

        await Should.ThrowAsync<InvalidDataException>(async () => await DecompressAsync(truncated));
        Should.Throw<InvalidDataException>(() => ReadAllViaByteArraySync(truncated));
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Decompress_CancellationIsHonoredBeforeAndAfterDetection(bool initialized)
    {
        var compressed = await CompressAsync(Textual(8192));
        await using var source = new MemoryStream(compressed);
        await using var decompress = Sut.WrapForDecompression(source, leaveOpen: true);
        var buffer = new byte[1];
        if (initialized)
            (await decompress.ReadAsync(buffer)).ShouldBe(1);
        else
            source.Position.ShouldBe(0L);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => (await decompress.ReadAsync(buffer, cancelled.Token)).ShouldBe(1));
    }

    [Test]
    public async Task Compress_CancelledWriteDoesNotConsumeInput()
    {
        using var output = new MemoryStream();
        await using var compress = Sut.WrapForCompression(output);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await compress.WriteAsync(new byte[8192], cancelled.Token));
    }

    [Test]
    [Arguments(true, true)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(false, false)]
    public async Task StreamOwnership_IsPreserved(bool leaveOpen, bool asyncDispose)
    {
        var compressed = await CompressAsync([1, 2, 3]);
        foreach (var initialize in new[] { false, true })
        {
            using var source = new MemoryStream(compressed);
            var decompress = Sut.WrapForDecompression(source, leaveOpen);
            source.Position.ShouldBe(0L);
            if (initialize)
                decompress.ReadByte().ShouldBe(1);
            if (asyncDispose)
                await decompress.DisposeAsync();
            else
                decompress.Dispose();
            source.CanRead.ShouldBe(leaveOpen);
        }

        using var output = new MemoryStream();
        var compress = Sut.WrapForCompression(output, leaveOpen);
        if (asyncDispose)
            await compress.DisposeAsync();
        else
            compress.Dispose();
        output.CanWrite.ShouldBe(leaveOpen);
        output.ToArray().ShouldNotBeEmpty();
        (await DecompressAsync(output.ToArray())).ShouldBeEmpty();
    }

    private static async Task<byte[]> ReadAllViaByteArrayAsync(byte[] compressed)
    {
        await using var source     = new MemoryStream(compressed);
        await using var decompress = Sut.WrapForDecompression(source, leaveOpen: true);
        var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await decompress.ReadAsync(buffer, 0, buffer.Length)) > 0)
            output.Write(buffer, 0, read);
        return output.ToArray();
    }

    private static byte[] ReadAllViaByteArraySync(byte[] compressed)
    {
        using var source     = new MemoryStream(compressed);
        using var decompress = Sut.WrapForDecompression(source, leaveOpen: true);
        var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = decompress.Read(buffer, 0, buffer.Length)) > 0)
            output.Write(buffer, 0, read);
        return output.ToArray();
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

    private static byte[] Compressible(int size)
    {
        // Use a nonconstant pattern to avoid the all-zero special case.
        var data = new byte[size];
        for (var i = 0; i < size; i++)
            data[i] = (byte)(i % 64);
        return data;
    }

    private static byte[] Textual(int size)
    {
        const string lorem = "The quick brown fox jumps over the lazy dog. ";
        var sb = new StringBuilder(size + lorem.Length);
        while (sb.Length < size)
            sb.Append(lorem);
        return Encoding.UTF8.GetBytes(sb.ToString(0, size));
    }
}
