using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Storage;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Arius.Benchmarks;

/// <summary>Codec and upload-stack measurements; invoke each scenario in a fresh process for peak RSS.</summary>
internal static class CompressionComparison
{
    public static async Task RunAsync(string codec, string workload)
    {
        if (codec is not ("framework" or "previous"))
            throw new ArgumentException("Codec must be framework or previous.", nameof(codec));
        const int iterations = 3;
        var data = CreateData(workload);

        using var compressed = new MemoryStream(data.Length + 4096);
        using var process = Process.GetCurrentProcess();
        var framework = new ZstdCompressionService();
        await CompressAsync(); // Warm JIT and codec before timing.
        var compressionCpuStart = process.TotalProcessorTime;
        var compressionTime = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
            await CompressAsync();
        compressionTime.Stop();
        var compressionCpu = process.TotalProcessorTime - compressionCpuStart;

        compressed.Position = 0;
        await using (var decoder = Decompress())
            if (!(await SHA256.HashDataAsync(decoder)).AsSpan().SequenceEqual(SHA256.HashData(data)))
                throw new InvalidDataException("Codec comparison failed round-trip verification.");

        var decompressionCpuStart = process.TotalProcessorTime;
        var decompressionTime = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            compressed.Position = 0;
            await using var decoder = Decompress();
            await decoder.CopyToAsync(Stream.Null);
        }
        decompressionTime.Stop();
        var decompressionCpu = process.TotalProcessorTime - decompressionCpuStart;

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Codec = codec,
            Workload = workload,
            InputBytes = data.Length,
            StoredBytes = compressed.Length,
            CompressionMiBPerSecond = iterations * 32 / compressionTime.Elapsed.TotalSeconds,
            DecompressionMiBPerSecond = iterations * 32 / decompressionTime.Elapsed.TotalSeconds,
            CompressionCpuMilliseconds = compressionCpu.TotalMilliseconds,
            CompressionCpuPercent = 100 * compressionCpu.TotalSeconds / compressionTime.Elapsed.TotalSeconds,
            DecompressionCpuMilliseconds = decompressionCpu.TotalMilliseconds,
            DecompressionCpuPercent = 100 * decompressionCpu.TotalSeconds / decompressionTime.Elapsed.TotalSeconds,
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

    public static async Task RunUploadAsync(string workload, bool encrypted)
    {
        const int iterations = 10;
        var data = CreateData(workload);
        await using var source = new FileStream(
            Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        await source.WriteAsync(data);
        await source.FlushAsync();
        IEncryptionService encryption = encrypted
            ? new PassphraseEncryptionService("compression-comparison")
            : new PlaintextPassthroughService();
        var chunkHash = ChunkHash.Parse(encryption.ComputeHash(data));
        using var process = Process.GetCurrentProcess();
        var result = await UploadAsync();
        var cpuStart = process.TotalProcessorTime;
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
            result = await UploadAsync();
        timer.Stop();
        var cpu = process.TotalProcessorTime - cpuStart;

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Scenario = "upload",
            Workload = workload,
            Encrypted = encrypted,
            Iterations = iterations,
            InputBytes = data.Length,
            StoredBytes = result.StoredSize,
            UploadMiBPerSecond = iterations * 32 / timer.Elapsed.TotalSeconds,
            UploadCpuMilliseconds = cpu.TotalMilliseconds,
            UploadCpuPercent = 100 * cpu.TotalSeconds / timer.Elapsed.TotalSeconds,
            PeakWorkingSetBytes = OperatingSystem.IsWindows() ? process.PeakWorkingSet64 : (long?)null,
        }));

        async Task<ChunkUploadResult> UploadAsync()
        {
            var blobs = new FakeInMemoryBlobContainerService();
            var storage = new ChunkStorageService(blobs, encryption, new ZstdCompressionService());
            return await storage.UploadLargeAsync(chunkHash, source, data.Length, BlobTier.Hot, smallFileThreshold: 0);
        }
    }

    private static byte[] CreateData(string workload)
    {
        if (workload is not ("text" or "mixed" or "random"))
            throw new ArgumentException("Workload must be text, mixed or random.", nameof(workload));
        var data = new byte[32 * 1024 * 1024];
        new Random(220).NextBytes(data);
        ReadOnlySpan<byte> text = "Arius compression comparison: binary files, filetrees, shards and snapshots.\n"u8;
        if (workload != "random")
            for (var offset = 0; offset < data.Length; offset++)
                if (workload == "text" || offset / 65536 % 2 == 0)
                    data[offset] = text[offset % text.Length];
        return data;
    }
}
