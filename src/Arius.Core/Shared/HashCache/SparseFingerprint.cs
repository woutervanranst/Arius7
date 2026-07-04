using System.Security.Cryptography;

namespace Arius.Core.Shared.HashCache;

/// <summary>
/// Deterministic spot-hash fingerprint of a file used by <c>--fast-hash</c> to detect content
/// change without re-reading the whole file. Regions are derived from the file size, so the same
/// regions are re-sampled across runs (size is the precondition for any comparison).
/// </summary>
[SharedWithinAssembly]
internal static class SparseFingerprint
{
    public const int Algo = 1;

    private const int  BlockSize = 256 * 1024;     // B
    private const long Stride    = 1L << 30;       // S (1 GiB)
    private const int  MinBlocks = 4;
    private const int  MaxBlocks = 64;

    /// <summary>Deterministic (offset,length) regions for a file of <paramref name="size"/> bytes.</summary>
    public static IReadOnlyList<(long Offset, int Length)> Regions(long size)
    {
        if (size <= 0)
            return [];

        var k = (int)Math.Clamp((size + Stride - 1) / Stride, MinBlocks, MaxBlocks);

        // Small files: a single whole-file region (one sequential read, no seeks).
        if (size <= (long)k * BlockSize)
            return [(0L, (int)size)];

        var regions = new (long, int)[k];
        for (var i = 0; i < k; i++)
        {
            var offset = (long)Math.Floor((double)i * (size - BlockSize) / (k - 1));
            regions[i] = (offset, BlockSize);
        }
        return regions;
    }

    /// <summary>
    /// Cold-path twin of <see cref="Sampler"/>: opens the file and seeks to each region, returning
    /// SHA-256 over <c>size ‖ region-bytes</c>. Used on the fingerprint floor when the ctime fast-lane
    /// missed. MUST hash byte-identically to <see cref="Sampler"/> for the same content — both consume
    /// the same <see cref="Regions"/> and the same <c>size ‖ region-bytes</c> framing, so any change to
    /// either MUST be mirrored in both (guarded by <c>SparseFingerprintTests.Sampler_MatchesSeekingFingerprint_ForSameContent</c>).
    /// </summary>
    public static byte[] ComputeBySeeking(RelativeFileSystem fs, RelativePath path, long size)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(BitConverter.GetBytes(size));

        using var stream = fs.OpenRead(path);
        var regions = Regions(size);
        // Buffer the largest region: the single whole-file region for a small file can reach k×BlockSize
        // (up to 1 MiB at k=MinBlocks), which is larger than BlockSize — a fixed BlockSize buffer would
        // overflow ReadExactly for files in (BlockSize, k×BlockSize].
        var buffer = new byte[regions.Count == 0 ? 0 : regions.Max(r => r.Length)];
        foreach (var (offset, length) in regions)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            stream.ReadExactly(buffer, 0, length);
            sha.AppendData(buffer, 0, length);
        }
        return sha.GetHashAndReset();
    }

    /// <summary>
    /// Warm-path twin of <see cref="ComputeBySeeking"/>: a forward-only sink that captures the
    /// fingerprint regions as a sequential read passes, so the fingerprint costs zero extra I/O when the
    /// file is already being fully hashed. MUST hash byte-identically to <see cref="ComputeBySeeking"/>
    /// for the same content (same <see cref="Regions"/>, same <c>size ‖ region-bytes</c> framing) — keep
    /// the two in sync.
    /// </summary>
    public sealed class Sampler
    {
        private readonly long                               _size;
        private readonly IReadOnlyList<(long Off, int Len)> _regions;
        private readonly byte[][]                           _captured;

        public Sampler(long size)
        {
            _size     = size;
            _regions  = Regions(size);
            _captured = _regions.Select(r => new byte[r.Len]).ToArray();
        }

        /// <summary>Offer the bytes read at <paramref name="position"/>; overlapping region bytes are copied out.</summary>
        public void Capture(long position, ReadOnlySpan<byte> buffer)
        {
            for (var i = 0; i < _regions.Count; i++)
            {
                var (off, len) = _regions[i];
                var from = Math.Max(position, off);
                var to   = Math.Min(position + buffer.Length, off + len);
                if (from >= to)
                    continue;
                var srcStart = (int)(from - position);
                var dstStart = (int)(from - off);
                buffer.Slice(srcStart, (int)(to - from)).CopyTo(_captured[i].AsSpan(dstStart));
            }
        }

        public byte[] Finish()
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            sha.AppendData(BitConverter.GetBytes(_size));
            foreach (var region in _captured)
                sha.AppendData(region);
            return sha.GetHashAndReset();
        }
    }
}
