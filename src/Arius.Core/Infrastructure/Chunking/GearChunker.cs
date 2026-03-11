using System.Numerics;
using Arius.Core.Models;

namespace Arius.Core.Infrastructure.Chunking;

/// <summary>
/// Gear hash content-defined chunker.
///
/// Uses a 256-entry ulong gear table (generated from a deterministic seed) and a
/// rolling hash to find chunk boundaries:
///   hash = (hash &lt;&lt; 1) + gearTable[byte]
///
/// A boundary is emitted when (hash &amp; mask) == 0, subject to min/max size enforcement.
/// The mask is derived from the average chunk size: mask = (1 &lt;&lt; log2(avg)) - 1.
///
/// Parameters (configurable at repo init, stored in RepoConfig):
///   ChunkMin  = 256 KB (no boundary before this position, except end-of-stream)
///   ChunkAvg  =   1 MB (target average; determines boundary probability via mask)
///   ChunkMax  =   4 MB (forced boundary if no natural boundary found)
/// </summary>
public sealed class GearChunker : IChunker
{
    // ── Defaults ─────────────────────────────────────────────────────────────
    public const int DefaultMin  = 256 * 1024;       //  256 KB
    public const int DefaultAvg  =   1 * 1024 * 1024; //    1 MB
    public const int DefaultMax  =   4 * 1024 * 1024; //    4 MB

    private readonly ulong[] _gearTable;
    private readonly int     _min;
    private readonly int     _max;
    private readonly ulong   _mask;

    // ── Construction ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a GearChunker using custom parameters.
    /// </summary>
    /// <param name="gearSeed">Deterministic seed stored in RepoConfig.GearSeed.</param>
    /// <param name="min">Minimum chunk size in bytes.</param>
    /// <param name="avg">Average chunk size in bytes (controls boundary probability).</param>
    /// <param name="max">Maximum chunk size in bytes.</param>
    public GearChunker(int gearSeed, int min = DefaultMin, int avg = DefaultAvg, int max = DefaultMax)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(min, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(avg, min);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, avg);

        _min       = min;
        _max       = max;
        _mask      = BuildMask(avg);
        _gearTable = BuildGearTable(gearSeed);
    }

    /// <summary>
    /// Creates a GearChunker from a <see cref="RepoConfig"/>.
    /// </summary>
    public static GearChunker FromConfig(RepoConfig config)
        => new(config.GearSeed, config.ChunkMin, config.ChunkAvg, config.ChunkMax);

    // ── IChunker ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async IAsyncEnumerable<Chunk> ChunkAsync(
        Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        // We read into a buffer and accumulate the current chunk in a MemoryStream.
        // NOTE: Span<T> cannot cross yield/await boundaries, so all slice operations
        // use byte[] indices (start/length) instead of Span slices.
        const int ReadBuffer = 65536; // 64 KB read buffer

        using var chunkBuffer = new MemoryStream(_min);
        var readBuf   = new byte[ReadBuffer];
        var gearTable = _gearTable; // local copy for hot-path perf
        var mask      = _mask;
        var min       = _min;
        var max       = _max;

        ulong hash    = 0;
        int chunkLen  = 0; // bytes in the current chunk

        while (true)
        {
            var bytesRead = await stream.ReadAsync(readBuf, cancellationToken);
            if (bytesRead == 0)
                break;

            int start = 0; // start of un-flushed data in the current ReadAsync slice

            for (int i = 0; i < bytesRead; i++)
            {
                hash = (hash << 1) + gearTable[readBuf[i]];
                chunkLen++;

                bool naturalBoundary = (hash & mask) == 0 && chunkLen >= min;
                bool forcedBoundary  = chunkLen >= max;

                if (naturalBoundary || forcedBoundary)
                {
                    // Write bytes from [start..i] (inclusive) into the chunk buffer,
                    // then emit.
                    chunkBuffer.Write(readBuf, start, i - start + 1);
                    var chunkData = chunkBuffer.ToArray();
                    chunkBuffer.SetLength(0);
                    chunkBuffer.Position = 0;
                    hash     = 0;
                    chunkLen = 0;
                    start    = i + 1;
                    yield return new Chunk(chunkData);
                }
            }

            // Save the un-flushed tail of this ReadAsync call for the next iteration.
            if (start < bytesRead)
                chunkBuffer.Write(readBuf, start, bytesRead - start);
        }

        // Emit any remaining bytes as the final (possibly < min) chunk.
        if (chunkBuffer.Length > 0)
            yield return new Chunk(chunkBuffer.ToArray());
    }

    // ── Gear table ───────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a 256-entry gear table from <paramref name="seed"/> using a
    /// simple linear-congruential generator (LCG).  The same seed always produces
    /// the same table on any platform.
    /// </summary>
    public static ulong[] BuildGearTable(int seed)
    {
        // LCG constants from Numerical Recipes (adapted to ulong)
        // multiplier and increment chosen for full-period 64-bit LCG
        ulong state = unchecked((ulong)seed);
        var table   = new ulong[256];
        for (int i = 0; i < 256; i++)
        {
            state    = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
            table[i] = state;
        }
        return table;
    }

    /// <summary>
    /// Derives the boundary mask from the average chunk size.
    /// mask = (nextPowerOfTwo(avg) >> 1) - 1  ≡  (1 &lt;&lt; floor(log2(avg))) - 1
    ///
    /// This gives on average one boundary per ~avg bytes.
    /// </summary>
    public static ulong BuildMask(int avg)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(avg, 2);
        // floor(log2(avg)) = bit position of the highest set bit
        int bits = BitOperations.Log2((uint)avg);
        return (1UL << bits) - 1;
    }
}
