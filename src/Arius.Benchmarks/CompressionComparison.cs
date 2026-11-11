using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Arius.Core.Shared.Compression;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Arius.Benchmarks;

/// <summary>Isolated codec measurements; invoke each codec/workload in a fresh process for peak RSS.</summary>
internal static class CompressionComparison
{
    public static async Task RunAsync(string codec, string workload)
    {
        if (codec is not ("framework" or "previous"))
            throw new ArgumentException("Codec must be framework or previous.", nameof(codec));
        if (workload is not ("text" or "mixed" or "random"))
            throw new ArgumentException("Workload must be text, mixed or random.", nameof(workload));

        const int iterations = 3;
        var data = new byte[32 * 1024 * 1024];
        new Random(220).NextBytes(data);
        ReadOnlySpan<byte> text = "Arius compression comparison: binary files, filetrees, shards and snapshots.\n"u8;
        if (workload != "random")
        {
            for (var offset = 0; offset < data.Length; offset++)
                if (workload == "text" || offset / 65536 % 2 == 0)
                    data[offset] = text[offset % text.Length];
        }

        using var compressed = new MemoryStream(data.Length + 4096);
        var framework = new ZstdCompressionService();
        await CompressAsync(); // Warm JIT and codec before timing.
        var compressionTime = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
            await CompressAsync();
        compressionTime.Stop();

        compressed.Position = 0;
        await using (var decoder = Decompress())
            if (!(await SHA256.HashDataAsync(decoder)).AsSpan().SequenceEqual(SHA256.HashData(data)))
                throw new InvalidDataException("Codec comparison failed round-trip verification.");

        var decompressionTime = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            compressed.Position = 0;
            await using var decoder = Decompress();
            await decoder.CopyToAsync(Stream.Null);
        }
        decompressionTime.Stop();

        using var process = Process.GetCurrentProcess();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Codec = codec,
            Workload = workload,
            InputBytes = data.Length,
            StoredBytes = compressed.Length,
            CompressionMiBPerSecond = iterations * 32 / compressionTime.Elapsed.TotalSeconds,
            DecompressionMiBPerSecond = iterations * 32 / decompressionTime.Elapsed.TotalSeconds,
            PeakWorkingSetBytes = OperatingSystem.IsWindows() ? process.PeakWorkingSet64 : (long?)null,
        }));

        async Task CompressAsync()
        {
            compressed.Position = 0;
            compressed.SetLength(0);
            await using var encoder = Compress();
            // Match streaming archive input rather than pledging the whole input in one write.
            for (var offset = 0; offset < data.Length; offset += 81920)
                await encoder.WriteAsync(data.AsMemory(offset, Math.Min(81920, data.Length - offset)));
        }

        Stream Compress()
        {
            if (codec == "framework")
                return framework.WrapForCompression(compressed);
            var encoder = new CompressionStream(compressed, 15, leaveOpen: true);
            encoder.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
            encoder.SetParameter(ZSTD_cParameter.ZSTD_c_nbWorkers, 0);
            return encoder;
        }

        Stream Decompress() => codec == "framework"
            ? framework.WrapForDecompression(compressed, leaveOpen: true)
            : new DecompressionStream(compressed, leaveOpen: true);
    }
}
