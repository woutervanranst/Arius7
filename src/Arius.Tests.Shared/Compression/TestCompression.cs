using Arius.Core.Shared.Compression;

namespace Arius.Tests.Shared.Compression;

/// <summary>
/// Shared <see cref="ICompressionService"/> for tests. Uses a fast zstd level — round-trip correctness
/// is independent of level, so the suite stays quick. The service is stateless, so one instance is reused.
/// </summary>
public static class TestCompression
{
    public static ICompressionService Instance { get; } = new ZstdCompressionService(compressionLevel: 1);
}
