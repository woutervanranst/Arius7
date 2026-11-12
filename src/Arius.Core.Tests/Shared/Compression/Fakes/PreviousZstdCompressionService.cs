using Arius.Core.Shared.Compression;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Arius.Core.Tests.Shared.Compression.Fakes;

/// <summary>The production zstd settings before the framework-codec migration.</summary>
internal sealed class PreviousZstdCompressionService : ICompressionService
{
    public Stream WrapForCompression(Stream destination, bool leaveOpen = true)
    {
        var stream = new CompressionStream(destination, 15, leaveOpen: leaveOpen);
        stream.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        stream.SetParameter(ZSTD_cParameter.ZSTD_c_nbWorkers, 0);
        return stream;
    }

    public Stream WrapForDecompression(Stream source, bool leaveOpen = false)
        => new DecompressionStream(source, leaveOpen: leaveOpen);
}
