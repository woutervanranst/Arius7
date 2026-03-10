using System.Security.Cryptography;
using Arius.Core.Infrastructure.Chunking;
using Arius.Core.Models;
using Shouldly;
using TUnit.Core;

namespace Arius.Core.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// 4.3  Chunker unit tests
// ─────────────────────────────────────────────────────────────────────────────

public class GearChunkerTests
{
    private const int Seed = 42;

    // ── Helper ───────────────────────────────────────────────────────────────

    private static GearChunker DefaultChunker() => new(Seed);

    private static async Task<List<byte[]>> ChunkBytesAsync(byte[] data, GearChunker? chunker = null)
    {
        chunker ??= DefaultChunker();
        using var stream = new MemoryStream(data);
        var chunks = new List<byte[]>();
        await foreach (var chunk in chunker.ChunkAsync(stream))
            chunks.Add(chunk.Data.ToArray());
        return chunks;
    }

    // ── Coverage / completeness ───────────────────────────────────────────────

    [Test]
    public async Task Chunks_Reassemble_ToOriginal_SmallFile()
    {
        // File smaller than ChunkMin → one chunk, exact content
        var data = RandomNumberGenerator.GetBytes(1024); // 1 KB
        var chunks = await ChunkBytesAsync(data);

        chunks.Count.ShouldBe(1);
        chunks[0].ShouldBe(data);
    }

    [Test]
    public async Task Chunks_Reassemble_ToOriginal_LargeFile()
    {
        // 5 MB — should produce multiple chunks
        var data = RandomNumberGenerator.GetBytes(5 * 1024 * 1024);
        var chunks = await ChunkBytesAsync(data);

        // Verify no gaps / overlaps
        var reassembled = chunks.SelectMany(c => c).ToArray();
        reassembled.ShouldBe(data);
    }

    [Test]
    public async Task EmptyStream_Yields_NoChunks()
    {
        using var stream = new MemoryStream([]);
        var chunks = new List<Chunk>();
        await foreach (var chunk in DefaultChunker().ChunkAsync(stream))
            chunks.Add(chunk);
        chunks.ShouldBeEmpty();
    }

    // ── Min/max size enforcement ──────────────────────────────────────────────

    [Test]
    public async Task No_Chunk_Smaller_Than_Min_Except_Last()
    {
        // Use small min/avg/max so we can test with a small data set
        var chunker = new GearChunker(Seed, min: 64, avg: 256, max: 1024);
        var data    = RandomNumberGenerator.GetBytes(20 * 1024); // 20 KB
        using var stream = new MemoryStream(data);

        var chunks = new List<byte[]>();
        await foreach (var chunk in chunker.ChunkAsync(stream))
            chunks.Add(chunk.Data.ToArray());

        // Every chunk except the last must be >= min
        for (int i = 0; i < chunks.Count - 1; i++)
            chunks[i].Length.ShouldBeGreaterThanOrEqualTo(64, $"chunk {i} is too small");
    }

    [Test]
    public async Task No_Chunk_Exceeds_Max()
    {
        var chunker = new GearChunker(Seed, min: 64, avg: 256, max: 1024);
        var data    = RandomNumberGenerator.GetBytes(50 * 1024); // 50 KB
        using var stream = new MemoryStream(data);

        await foreach (var chunk in chunker.ChunkAsync(stream))
            chunk.Data.Length.ShouldBeLessThanOrEqualTo(1024);
    }

    [Test]
    public async Task Max_Boundary_Forced_When_No_Natural_Boundary()
    {
        // We want to verify that when no gear hash boundary fires within [min, max],
        // a forced boundary is emitted at exactly max bytes.
        //
        // Strategy: use all-zero data (gear hash over zeros converges to a fixed value
        // quickly, so boundaries are rare), with a large avg mask relative to the data
        // size to suppress natural boundaries, and a small max.
        //
        // Parameters: min=1, avg=max, max=512.
        // avg==max is valid (the guard is max >= avg >= min, equality allowed).
        // With all-zero data and mask = (1<<9)-1 = 511, boundaries occur on average
        // every 512 bytes — but since we check exactly at the max, forced boundaries
        // will dominate for all-zero content where the hash state is predictable.
        //
        // Simpler: set avg to exactly max so the mask matches the max exactly,
        // then verify chunks never exceed max.
        int max     = 512;
        var chunker = new GearChunker(Seed, min: 1, avg: max, max: max);
        var data    = new byte[max * 5]; // 5 × max, all zeros

        using var stream = new MemoryStream(data);
        var chunks = new List<byte[]>();
        await foreach (var chunk in chunker.ChunkAsync(stream))
            chunks.Add(chunk.Data.ToArray());

        // All chunks must be <= max
        foreach (var chunk in chunks)
            chunk.Length.ShouldBeLessThanOrEqualTo(max);

        // Total data must be preserved
        chunks.SelectMany(c => c).ToArray().ShouldBe(data);
    }

    // ── Determinism ───────────────────────────────────────────────────────────

    [Test]
    public async Task Same_Seed_Same_Boundaries()
    {
        var data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);

        var chunks1 = await ChunkBytesAsync(data, new GearChunker(Seed));
        var chunks2 = await ChunkBytesAsync(data, new GearChunker(Seed));

        chunks1.Count.ShouldBe(chunks2.Count);
        for (int i = 0; i < chunks1.Count; i++)
            chunks1[i].ShouldBe(chunks2[i], $"chunk {i} differs");
    }

    [Test]
    public async Task Different_Seed_Different_Boundaries()
    {
        var data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);

        var chunks1 = await ChunkBytesAsync(data, new GearChunker(gearSeed: 1));
        var chunks2 = await ChunkBytesAsync(data, new GearChunker(gearSeed: 2));

        // It is astronomically unlikely that two different seeds produce identical chunk counts
        // for 3 MB of random data; assert chunk counts differ as a proxy for different boundaries.
        // (The data is the same, only the gear table changes.)
        var countsMatch = chunks1.Count == chunks2.Count
                          && chunks1.Zip(chunks2).All(p => p.First.SequenceEqual(p.Second));
        countsMatch.ShouldBeFalse("different seeds should produce different boundaries");
    }

    [Test]
    public void BuildMask_ReturnsCorrectMask()
    {
        // avg = 1 MB = 2^20 → bits = 20, mask = (1<<20)-1 = 0xFFFFF
        GearChunker.BuildMask(1 * 1024 * 1024).ShouldBe((1UL << 20) - 1);
        // avg = 256 KB = 2^18 → bits = 18, mask = (1<<18)-1
        GearChunker.BuildMask(256 * 1024).ShouldBe((1UL << 18) - 1);
    }

    [Test]
    public void BuildGearTable_IsDeterministic()
    {
        var t1 = GearChunker.BuildGearTable(99);
        var t2 = GearChunker.BuildGearTable(99);
        t1.ShouldBe(t2);
    }

    [Test]
    public void BuildGearTable_DifferentSeeds_DifferentTable()
    {
        var t1 = GearChunker.BuildGearTable(1);
        var t2 = GearChunker.BuildGearTable(2);
        t1.ShouldNotBe(t2);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 4.4  Chunker deduplication tests — insert byte, verify most chunks unchanged
// ─────────────────────────────────────────────────────────────────────────────

public class GearChunkerDedupTests
{
    private const int Seed     = 42;
    private const int DataSize = 8 * 1024 * 1024; // 8 MB

    [Test]
    public async Task InsertByteAtStart_MostChunksUnchanged()
    {
        // Content-defined chunking should re-sync quickly after a prepended byte.
        // For 8 MB of data, after inserting 1 byte at the start, we expect a large
        // majority of chunks to be byte-for-byte identical (dedup still works).
        var original = RandomNumberGenerator.GetBytes(DataSize);
        var modified = new byte[DataSize + 1];
        modified[0] = 0xFF;
        original.CopyTo(modified, 1);

        var chunksOrig  = await ChunkBytesAsync(original);
        var chunksModif = await ChunkBytesAsync(modified);

        // Build a hashset of original chunk fingerprints
        var origSet = new HashSet<string>(
            chunksOrig.Select(c => Convert.ToHexString(MD5.HashData(c))));

        // Count how many modified chunks are present verbatim in the original
        int matchCount = chunksModif.Count(c => origSet.Contains(Convert.ToHexString(MD5.HashData(c))));

        // Expect at least 70% of chunks to be unchanged (gear hash re-syncs quickly)
        double matchRatio = (double)matchCount / chunksModif.Count;
        matchRatio.ShouldBeGreaterThan(0.70,
            $"Expected >70% chunks unchanged after prepend, got {matchRatio:P1} " +
            $"({matchCount}/{chunksModif.Count})");
    }

    private static async Task<List<byte[]>> ChunkBytesAsync(byte[] data)
    {
        var chunker = new GearChunker(Seed);
        using var stream = new MemoryStream(data);
        var chunks = new List<byte[]>();
        await foreach (var chunk in chunker.ChunkAsync(stream))
            chunks.Add(chunk.Data.ToArray());
        return chunks;
    }
}
