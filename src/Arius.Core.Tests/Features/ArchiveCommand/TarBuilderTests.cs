using System.Formats.Tar;
using Arius.Core.Features.ArchiveCommand;

namespace Arius.Core.Tests.Features.ArchiveCommand;

// ── TarBuilder: small-file tar bundling (task 8.8) ────────────────────────────

/// <summary>
/// Unit tests for <see cref="TarBuilder"/> — the small-file accumulator that builds content-addressed tar
/// bundles. The builder is exercised directly (no mediator, no pipeline): files are fed as in-memory streams,
/// and the produced <see cref="SealedTar"/> is read back with a <see cref="TarReader"/> to verify the bytes.
/// </summary>
public class TarBuilderTests
{
    private static readonly PlaintextPassthroughService s_encryption = new();

    // ── Accumulation & sealing ────────────────────────────────────────────────

    [Test]
    public async Task AddAsync_FilesUnderTarget_AccumulateIntoSingleBundle()
    {
        await using var builder = new TarBuilder(targetSize: 1000, s_encryption);

        var (c1, h1) = Content(0x11, 100);
        var (c2, h2) = Content(0x22, 200);

        // Under target → nothing sealed yet.
        (await builder.AddAsync(Upload("a.txt", h1, c1.Length), new MemoryStream(c1), CancellationToken.None)).ShouldBeNull();
        (await builder.AddAsync(Upload("b.txt", h2, c2.Length), new MemoryStream(c2), CancellationToken.None)).ShouldBeNull();

        var bundle = await builder.SealAsync(CancellationToken.None);

        bundle.ShouldNotBeNull();
        bundle.Entries.Count.ShouldBe(2);
        bundle.UncompressedSize.ShouldBe(300);
        bundle.Entries.Select(e => e.ContentHash).ShouldBe([h1, h2], ignoreOrder: true);

        // The bundle bytes are a valid tar whose entries are named by content-hash and carry the original bytes.
        var extracted = await ReadBundleAsync(bundle);
        extracted.ShouldContainKey(h1.ToString());
        extracted.ShouldContainKey(h2.ToString());
        extracted[h1.ToString()].ShouldBe(c1);
        extracted[h2.ToString()].ShouldBe(c2);
    }

    [Test]
    public async Task AddAsync_ReachingTarget_SealsBundleAndStartsFresh()
    {
        await using var builder = new TarBuilder(targetSize: 150, s_encryption);

        var (c1, h1) = Content(0x11, 100);
        var (c2, h2) = Content(0x22, 100); // pushes the bundle to 200 ≥ 150 → seal
        var (c3, h3) = Content(0x33, 50);

        (await builder.AddAsync(Upload("a", h1, c1.Length), new MemoryStream(c1), CancellationToken.None)).ShouldBeNull();
        var firstBundle = await builder.AddAsync(Upload("b", h2, c2.Length), new MemoryStream(c2), CancellationToken.None);

        firstBundle.ShouldNotBeNull();
        firstBundle.Entries.Count.ShouldBe(2);
        firstBundle.UncompressedSize.ShouldBe(200);

        // Builder reset: the next add opens a fresh bundle.
        (await builder.AddAsync(Upload("c", h3, c3.Length), new MemoryStream(c3), CancellationToken.None)).ShouldBeNull();

        var finalBundle = await builder.SealAsync(CancellationToken.None);
        finalBundle.ShouldNotBeNull();
        finalBundle.Entries.Count.ShouldBe(1);
        finalBundle.Entries[0].ContentHash.ShouldBe(h3);
    }

    [Test]
    public async Task SealAsync_NoFilesAdded_ReturnsNull()
    {
        await using var builder = new TarBuilder(targetSize: 1000, s_encryption);

        (await builder.SealAsync(CancellationToken.None)).ShouldBeNull();
    }

    [Test]
    public async Task SealAsync_AfterThresholdSealWithNoNewFiles_ReturnsNull()
    {
        await using var builder = new TarBuilder(targetSize: 50, s_encryption);

        var (c1, h1) = Content(0x11, 100);
        // 100 ≥ 50 → AddAsync seals immediately.
        (await builder.AddAsync(Upload("a", h1, c1.Length), new MemoryStream(c1), CancellationToken.None)).ShouldNotBeNull();

        // Nothing pending → final seal is a no-op.
        (await builder.SealAsync(CancellationToken.None)).ShouldBeNull();
    }

    [Test]
    public async Task SealAsync_TarHash_MatchesHashOfBundleContent()
    {
        await using var builder = new TarBuilder(targetSize: 1000, s_encryption);

        var (c1, h1) = Content(0x11, 100);
        await builder.AddAsync(Upload("a", h1, c1.Length), new MemoryStream(c1), CancellationToken.None);

        var bundle = await builder.SealAsync(CancellationToken.None);

        bundle.ShouldNotBeNull();
        bundle.TarHash.ShouldBe(ChunkHashOf(bundle.Content, s_encryption));
    }

    // ── Lifecycle callbacks ───────────────────────────────────────────────────

    [Test]
    public async Task Callbacks_FireAtLifecycleMoments()
    {
        var started       = 0;
        var entriesAdded  = new List<(ContentHash Hash, int Count, long Size)>();
        var sealedBundles = new List<SealedTar>();

        await using var builder = new TarBuilder(
            targetSize: 150,
            s_encryption,
            onBundleStarted: () => { started++; return ValueTask.CompletedTask; },
            onEntryAdded:    (hash, count, size) => { entriesAdded.Add((hash, count, size)); return ValueTask.CompletedTask; },
            onBundleSealing: bundle => { sealedBundles.Add(bundle); return ValueTask.CompletedTask; });

        var (c1, h1) = Content(0x11, 100);
        var (c2, h2) = Content(0x22, 100); // seals the bundle

        await builder.AddAsync(Upload("a", h1, c1.Length), new MemoryStream(c1), CancellationToken.None);
        await builder.AddAsync(Upload("b", h2, c2.Length), new MemoryStream(c2), CancellationToken.None);

        started.ShouldBe(1); // a single bundle was opened
        entriesAdded.ShouldBe([(h1, 1, 100L), (h2, 2, 200L)]);
        sealedBundles.Count.ShouldBe(1);
        sealedBundles[0].Entries.Count.ShouldBe(2);
    }

    // ── Resource handling ─────────────────────────────────────────────────────

    [Test]
    public async Task AddAsync_DisposesSourceStream()
    {
        await using var builder = new TarBuilder(targetSize: 1000, s_encryption);

        var (c1, h1) = Content(0x11, 100);
        var source = new MemoryStream(c1);

        await builder.AddAsync(Upload("a", h1, c1.Length), source, CancellationToken.None);

        Should.Throw<ObjectDisposedException>(() => _ = source.Position);
    }

    [Test]
    public async Task DisposeAsync_WithUnsealedBundle_DiscardsWithoutSealing()
    {
        var sealedBundles = new List<SealedTar>();
        var builder = new TarBuilder(
            targetSize: 1000,
            s_encryption,
            onBundleSealing: bundle => { sealedBundles.Add(bundle); return ValueTask.CompletedTask; });

        var (c1, h1) = Content(0x11, 100);
        await builder.AddAsync(Upload("a", h1, c1.Length), new MemoryStream(c1), CancellationToken.None);

        // Disposing with an open bundle discards it — no seal, no throw.
        await builder.DisposeAsync();

        sealedBundles.ShouldBeEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static FileToUpload Upload(string path, ContentHash hash, long size)
        => new(new HashedFilePair(new FilePair { RelativePath = RelativePath.Parse(path) }, hash, default, default), size);

    /// <summary>Builds <paramref name="count"/> bytes of <paramref name="fill"/> and their content hash.</summary>
    private static (byte[] Content, ContentHash Hash) Content(byte fill, int count)
    {
        var bytes = Enumerable.Repeat(fill, count).ToArray();
        return (bytes, s_encryption.ComputeHash(bytes));
    }

    /// <summary>Reads a sealed tar bundle back into a map of entry-name → entry-bytes.</summary>
    private static async Task<Dictionary<string, byte[]>> ReadBundleAsync(SealedTar bundle)
    {
        var entries = new Dictionary<string, byte[]>();

        using var ms     = new MemoryStream(bundle.Content.Array!, bundle.Content.Offset, bundle.Content.Count);
        await using var reader = new TarReader(ms);

        while (await reader.GetNextEntryAsync(copyData: true) is { } entry)
        {
            using var data = new MemoryStream();
            if (entry.DataStream is not null)
                await entry.DataStream.CopyToAsync(data);
            entries[entry.Name] = data.ToArray();
        }

        return entries;
    }
}
