using Arius.Core.Features.SnapshotDiffQuery;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Tests.Fakes;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using SnapshotDiffQueryType = Arius.Core.Features.SnapshotDiffQuery.SnapshotDiffQuery;

namespace Arius.Core.Tests.Features.SnapshotDiffQuery;

public class SnapshotDiffQueryHandlerTests
{
    private static readonly DateTimeOffset s_t1 = new(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_t2 = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_tsA = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_tsB = new(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Handle_ClassifiesAddedRemovedModifiedAndTimestampChanged()
    {
        var rootA = Entries(
            File("keep.txt",  ContentHashOf("keep"),  s_t1, s_t1),   // unchanged
            File("edit.txt",  ContentHashOf("v1"),    s_t1, s_t1),   // modified
            File("gone.txt",  ContentHashOf("gone"),  s_t1, s_t1),   // removed
            File("touch.txt", ContentHashOf("same"),  s_t1, s_t1));  // timestamp-only
        var rootB = Entries(
            File("keep.txt",  ContentHashOf("keep"),  s_t1, s_t1),
            File("edit.txt",  ContentHashOf("v2"),    s_t1, s_t1),
            File("new.txt",   ContentHashOf("new"),   s_t1, s_t1),   // added
            File("touch.txt", ContentHashOf("same"),  s_t1, s_t2));  // modified-time only

        await using var fixture = await OpenFixtureAsync("acct-diff-1", "ctr-diff-1", rootA, rootB);
        var handler = Handler(fixture);

        var results = await handler.Handle(new SnapshotDiffQueryType(VersionOf(s_tsA), VersionOf(s_tsB)), CancellationToken.None).ToListAsync();

        Single(results, ChangeType.Added).Path.ToString().ShouldBe("new.txt");
        Single(results, ChangeType.Removed).Path.ToString().ShouldBe("gone.txt");
        Single(results, ChangeType.Modified).Path.ToString().ShouldBe("edit.txt");
        Single(results, ChangeType.TimestampChanged).Path.ToString().ShouldBe("touch.txt");
        results.Count.ShouldBe(4); // keep.txt (unchanged) is not emitted
    }

    [Test]
    public async Task Handle_RecursesIntoChangedSubdirectories()
    {
        var subA = Entries(File("inner.txt", ContentHashOf("inner"), s_t1, s_t1));
        var subB = Entries(
            File("inner.txt",    ContentHashOf("inner"),    s_t1, s_t1),
            File("innernew.txt", ContentHashOf("innernew"), s_t1, s_t1));
        var subAHash = FileTreeBuilder.ComputeHash(subA, IEncryptionService.PlaintextInstance);
        var subBHash = FileTreeBuilder.ComputeHash(subB, IEncryptionService.PlaintextInstance);

        var rootA = Entries(Dir("changing", subAHash));
        var rootB = Entries(Dir("changing", subBHash));

        var blobs = new FakeSeededBlobContainerService();
        await SeedTreeAsync(blobs, subA);
        await SeedTreeAsync(blobs, subB);
        await using var fixture = await OpenFixtureAsync("acct-diff-2", "ctr-diff-2", rootA, rootB, blobs);
        var handler = Handler(fixture);

        var results = await handler.Handle(new SnapshotDiffQueryType(VersionOf(s_tsA), VersionOf(s_tsB)), CancellationToken.None).ToListAsync();

        Single(results, ChangeType.Added).Path.ToString().ShouldBe("changing/innernew.txt");
        results.Count.ShouldBe(1); // inner.txt unchanged
    }

    [Test]
    public async Task Handle_IdenticalSubtree_IsPrunedAndNotRead()
    {
        // Both sides reference the SAME directory hash that is deliberately NOT seeded as a blob.
        // If pruning regresses, the handler would ReadAsync this hash and throw.
        var sharedHash = FakeFileTreeHash('a');
        var rootA = Entries(Dir("static", sharedHash), File("a.txt", ContentHashOf("a"), s_t1, s_t1));
        var rootB = Entries(Dir("static", sharedHash), File("a.txt", ContentHashOf("a"), s_t1, s_t1));

        await using var fixture = await OpenFixtureAsync("acct-diff-3", "ctr-diff-3", rootA, rootB);
        var handler = Handler(fixture);

        var results = await handler.Handle(new SnapshotDiffQueryType(VersionOf(s_tsA), VersionOf(s_tsB)), CancellationToken.None).ToListAsync();

        results.ShouldBeEmpty(); // identical everywhere; pruned subtree never read
    }

    [Test]
    public async Task Handle_MissingSnapshot_Throws()
    {
        var rootA = Entries(File("x.txt", ContentHashOf("x"), s_t1, s_t1));
        await using var fixture = await OpenFixtureAsync("acct-diff-4", "ctr-diff-4", rootA, rootA);
        var handler = Handler(fixture);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in handler.Handle(new SnapshotDiffQueryType(VersionOf(s_tsA), "9999-nope"), CancellationToken.None)) { }
        });
    }

    [Test]
    public async Task Handle_DifferentAriusVersion_LogsWarning()
    {
        var rootA = Entries(File("x.txt", ContentHashOf("x1"), s_t1, s_t1));
        var rootB = Entries(File("x.txt", ContentHashOf("x2"), s_t1, s_t1));
        var logger = new FakeLogger<SnapshotDiffQueryHandler>();
        await using var fixture = await OpenFixtureAsync("acct-diff-5", "ctr-diff-5", rootA, rootB, ariusVersionA: "v1", ariusVersionB: "v2");
        var handler = Handler(fixture, logger);

        await handler.Handle(new SnapshotDiffQueryType(VersionOf(s_tsA), VersionOf(s_tsB)), CancellationToken.None).ToListAsync();

        logger.Collector.GetSnapshot().ShouldContain(r => r.Level == LogLevel.Warning && r.Message.Contains("different Arius versions"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SnapshotDiffEntry Single(IReadOnlyList<SnapshotDiffEntry> results, ChangeType change)
        => results.Single(e => e.Change == change);

    private static string VersionOf(DateTimeOffset ts) => ts.UtcDateTime.ToString(SnapshotService.TimestampFormat);

    private static IReadOnlyList<FileTreeEntry> Entries(params FileTreeEntry[] entries) => entries;

    private static FileEntry File(string name, ContentHash hash, DateTimeOffset created, DateTimeOffset modified) => new()
    {
        Name = PathSegment.Parse(name), ContentHash = hash, Created = created, Modified = modified
    };

    private static DirectoryEntry Dir(string name, FileTreeHash hash) => new()
    {
        Name = PathSegment.Parse(name), FileTreeHash = hash
    };

    private static async Task<RepositoryTestFixture> OpenFixtureAsync(
        string account, string container,
        IReadOnlyList<FileTreeEntry> rootA, IReadOnlyList<FileTreeEntry> rootB,
        FakeSeededBlobContainerService? blobs = null,
        string ariusVersionA = "test", string ariusVersionB = "test")
    {
        blobs ??= new FakeSeededBlobContainerService();
        var rootAHash = await SeedTreeAsync(blobs, rootA);
        var rootBHash = await SeedTreeAsync(blobs, rootB);
        await SeedSnapshotAsync(blobs, s_tsA, rootAHash, ariusVersionA);
        await SeedSnapshotAsync(blobs, s_tsB, rootBHash, ariusVersionB);

        return await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, account, container, IEncryptionService.PlaintextInstance);
    }

    private static SnapshotDiffQueryHandler Handler(RepositoryTestFixture fixture, ILogger<SnapshotDiffQueryHandler>? logger = null)
        => new(fixture.Snapshot, fixture.FileTreeService, logger ?? NullLogger<SnapshotDiffQueryHandler>.Instance);

    private static async Task<FileTreeHash> SeedTreeAsync(FakeSeededBlobContainerService blobs, IReadOnlyList<FileTreeEntry> entries)
    {
        var plaintext = FileTreeSerializer.Serialize(entries);
        var hash = FileTreeHashOf(plaintext);
        using var ms = new MemoryStream();
        await using (var encStream = IEncryptionService.PlaintextInstance.WrapForEncryption(ms))
        await using (var gzipStream = new System.IO.Compression.GZipStream(encStream, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            await gzipStream.WriteAsync((ReadOnlyMemory<byte>)plaintext);
        blobs.AddBlob(BlobPaths.FileTreePath(hash), ms.ToArray());
        return hash;
    }

    private static async Task SeedSnapshotAsync(FakeSeededBlobContainerService blobs, DateTimeOffset ts, FileTreeHash rootHash, string ariusVersion)
    {
        var manifest = new SnapshotManifest { Timestamp = ts, RootHash = rootHash, FileCount = 0, OriginalSize = 0, AriusVersion = ariusVersion };
        blobs.AddBlob(BlobPaths.SnapshotPath(ts), await SnapshotSerializer.SerializeAsync(manifest, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance));
    }
}
