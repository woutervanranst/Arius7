using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.HashCache;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared;
using BenchmarkDotNet.Attributes;

namespace Arius.Benchmarks;

/// <summary>
/// In-process allocation benchmarks for selected Arius.Core components.
/// Run with <c>--class micro</c>; no Azurite or Docker is required.
/// </summary>
[MemoryDiagnoser]
public class AllocationBenchmarks
{
    private const int EntryCount = 1_000;

    /// <summary>
    /// Digest containing hexadecimal letters, ensuring the lowercase conversion path is exercised.
    /// </summary>
    private readonly byte[] _digest = CreateHighNibbleDigest();

    private const string CanonicalHex   = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
    private const string UppercaseHex   = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF";

    [Benchmark(Description = "HashCodec.ToLowerHex")]
    public string HashCodec_ToLowerHex() => HashCodec.ToLowerHex(_digest);

    /// <summary>Parses an already canonical lowercase value.</summary>
    [Benchmark(Description = "HashCodec.NormalizeHex (already canonical)")]
    public string HashCodec_NormalizeHex_Canonical() => HashCodec.NormalizeHex(CanonicalHex);

    [Benchmark(Description = "HashCodec.NormalizeHex (uppercase input)")]
    public string HashCodec_NormalizeHex_Uppercase() => HashCodec.NormalizeHex(UppercaseHex);

    // ── Sparse fingerprint ───────────────────────────────────────────────────────

    private const long SmallFileSize = 200L * 1024;              // one whole-file region
    private const long LargeFileSize = 64L * 1024 * 1024 * 1024; // k = MaxBlocks = 64 regions

    private byte[] _readBuffer = null!;

    /// <summary>Exercises the single-region sampler path for a small file.</summary>
    [Benchmark(Description = "SparseFingerprint.Sampler small file (200 KB)")]
    public byte[] SparseFingerprint_Sampler_SmallFile()
    {
        using var sampler = new SparseFingerprint.Sampler(SmallFileSize);
        var position      = 0L;

        while (position < SmallFileSize)
        {
            var length = (int)Math.Min(_readBuffer.Length, SmallFileSize - position);
            sampler.Capture(position, _readBuffer.AsSpan(0, length));
            position += length;
        }

        return sampler.Finish();
    }

    /// <summary>Exercises the maximum sampled-region buffer size.</summary>
    [Benchmark(Description = "SparseFingerprint.Sampler large file (64 GB logical)")]
    public byte[] SparseFingerprint_Sampler_LargeFile()
    {
        using var sampler = new SparseFingerprint.Sampler(LargeFileSize);
        return sampler.Finish();
    }

    // ── Filetree serialization ───────────────────────────────────────────────────

    private IReadOnlyList<FileTreeEntry> _fileTreeEntries = null!;
    private byte[]                       _fileTreeBytes   = null!;

    [Benchmark(Description = "FileTreeSerializer.Serialize (1000 entries)")]
    public byte[] FileTreeSerializer_Serialize() => FileTreeSerializer.Serialize(_fileTreeEntries);

    [Benchmark(Description = "FileTreeSerializer.Deserialize (1000 entries)")]
    public IReadOnlyList<FileTreeEntry> FileTreeSerializer_Deserialize() => FileTreeSerializer.Deserialize(_fileTreeBytes);

    // ── Tar builder ──────────────────────────────────────────────────────────────

    private const long TarTargetSize  = 64L * 1024 * 1024;
    private const int  TarEntrySize   = 64 * 1024;

    private byte[]             _tarEntryPayload = null!;
    private IEncryptionService _encryption      = null!;

    /// <summary>Builds and seals one 64 MB TAR bundle.</summary>
    [Benchmark(Description = "TarBuilder seal one 64 MB bundle")]
    public async Task<int> TarBuilder_Seal_64MB()
    {
        await using var builder = new TarBuilder(TarTargetSize, _encryption);

        var sealedCount = 0;
        for (var i = 0; i < TarTargetSize / TarEntrySize; i++)
        {
            var source = new MemoryStream(_tarEntryPayload, writable: false);
            if (await builder.AddAsync(CreateUpload(i, TarEntrySize), source, CancellationToken.None) is not null)
                sealedCount++;
        }

        return sealedCount;
    }

    // ── Chunk-index local store ──────────────────────────────────────────────────

    private LocalDirectory       _storeRoot     = default;
    private ChunkIndexLocalStore _store         = null!;
    private ShardEntry[]         _shardEntries  = null!;
    private ContentHash[]        _lookupHashes  = null!;
    private PathSegment          _rangePrefix   = default;

    [Benchmark(Description = "ChunkIndexLocalStore.UpsertRemoteBacked (1000 rows)")]
    public void ChunkIndexLocalStore_UpsertRemoteBacked() => _store.UpsertRemoteBacked(_shardEntries);

    [Benchmark(Description = "ChunkIndexLocalStore.ReadRangeEntries (1000 rows)")]
    public int ChunkIndexLocalStore_ReadRangeEntries()
    {
        var count = 0;
        _store.ReadRangeEntries(_rangePrefix, _ => count++);
        return count;
    }

    /// <summary>Measures the per-hash lookup shape for a 256-hash deduplication batch.</summary>
    [Benchmark(Description = "ChunkIndexLocalStore.FindEntry x256 (one dedup batch)")]
    public int ChunkIndexLocalStore_FindEntry_256()
    {
        var found = 0;
        foreach (var hash in _lookupHashes)
            if (_store.FindEntry(hash) is not null)
                found++;

        return found;
    }

    /// <summary>Measures the batched lookup for a 256-hash deduplication batch.</summary>
    [Benchmark(Description = "ChunkIndexLocalStore.FindEntries x1 (one dedup batch)")]
    public int ChunkIndexLocalStore_FindEntries_Batch() => _store.FindEntries(_lookupHashes).Count;

    // ── Setup ────────────────────────────────────────────────────────────────────

    [GlobalSetup]
    public void Setup()
    {
        _readBuffer      = new byte[81920];
        _tarEntryPayload = new byte[TarEntrySize];
        Random.Shared.NextBytes(_readBuffer);
        Random.Shared.NextBytes(_tarEntryPayload);

        _encryption = IEncryptionService.EncryptedInstance;

        _fileTreeEntries = BuildFileTreeEntries(EntryCount);
        _fileTreeBytes   = FileTreeSerializer.Serialize(_fileTreeEntries);

        _shardEntries = BuildShardEntries(EntryCount);
        _lookupHashes = _shardEntries.Take(256).Select(e => e.ContentHash).ToArray();
        _rangePrefix  = PathSegment.Parse("00");

        _storeRoot = TestTempRoots.CreateDirectory("benchmark-chunkindex");
        _store     = new ChunkIndexLocalStore(_storeRoot);
        _store.UpsertRemoteBacked(_shardEntries);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            RelativeFileSystem.DeleteDirectory(_storeRoot, RelativePath.Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the SQLite connection pool may still hold the file. TestTempRoots sweeps stale dirs.
        }
    }

    // ── Deterministic fixtures ───────────────────────────────────────────────────

    /// <summary>
    /// Creates distinct digests under the <c>"00"</c> range prefix used by the range benchmark.
    /// </summary>
    private static byte[] CreateDigest(int seed)
    {
        var digest = new byte[32];
        BitConverter.TryWriteBytes(digest.AsSpan(1), seed);
        return digest;
    }

    private static ContentHash CreateContentHash(int seed) => ContentHash.FromDigest(CreateDigest(seed));

    /// <summary>Creates a digest containing hexadecimal letters.</summary>
    private static byte[] CreateHighNibbleDigest()
    {
        var digest = new byte[32];
        for (var i = 0; i < digest.Length; i++)
            digest[i] = (byte)(0xA0 | (i & 0x0F));

        return digest;
    }

    private static IReadOnlyList<FileTreeEntry> BuildFileTreeEntries(int count)
    {
        var entries = new List<FileTreeEntry>(count);
        var created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < count; i++)
        {
            entries.Add(new FileEntry
            {
                Name        = PathSegment.Parse($"file-{i:D6}.bin"),
                ContentHash = CreateContentHash(i),
                Created     = created.AddSeconds(i),
                Modified    = created.AddSeconds(i * 2),
            });
        }

        return entries;
    }

    private static ShardEntry[] BuildShardEntries(int count)
    {
        var entries = new ShardEntry[count];
        for (var i = 0; i < count; i++)
        {
            var contentHash = CreateContentHash(i);
            entries[i] = new ShardEntry(
                ContentHash:     contentHash,
                ChunkHash:       ChunkHash.Parse(contentHash), // large chunk: chunk hash == content hash
                OriginalSize:    4096 + i,
                ChunkSize:       2048 + i,
                StorageTierHint: BlobTier.Cool);
        }

        return entries;
    }

    private static FileToUpload CreateUpload(int seed, long size)
    {
        var filePair = new FilePair { RelativePath = RelativePath.Parse($"file-{seed:D6}.bin") };
        var hashed   = new HashedFilePair(filePair, CreateContentHash(seed), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        return new FileToUpload(hashed, size);
    }
}
