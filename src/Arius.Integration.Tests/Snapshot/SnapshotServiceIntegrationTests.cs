using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Snapshot;
using Arius.Tests.Shared.Storage;

namespace Arius.Integration.Tests.Snapshot;

/// <summary>
/// Integration tests for <see cref="SnapshotService"/> against Azurite.
/// Requires Docker (Azurite via TestContainers). Task 6.6.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class SnapshotServiceIntegrationTests(AzuriteFixture azurite)
{
    private static readonly PlaintextPassthroughService s_enc = new();
    private static readonly FileTreeHash s_rootHash = FileTreeHash.Parse(new string('0', 64));

    private static FileTreeHash Root(char c) => FileTreeHash.Parse(new string(c, 64));

    // ── Create + resolve latest ───────────────────────────────────────────────

    [Test]
    public async Task CreateAsync_ThenResolveLatest_ReturnsCreatedSnapshot()
    {
        var (container, blobs) = await azurite.CreateTestServiceAsync();
        var svc = new SnapshotService(blobs, s_enc, container.AccountName, container.Name);

        var ts       = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero);
        var snapshot = await svc.CreateAsync(s_rootHash, fileCount: 10, totalSize: 1024, timestamp: ts);

        snapshot.RootHash.ShouldBe(s_rootHash);
        snapshot.FileCount.ShouldBe(10);

        var resolved = await svc.ResolveAsync(); // latest
        resolved.ShouldNotBeNull();
        resolved!.RootHash.ShouldBe(s_rootHash);
        resolved.Timestamp.ShouldBe(ts);
    }

    // ── List snapshots sorted ─────────────────────────────────────────────────

    [Test]
    public async Task ListBlobNamesAsync_MultipeSnapshots_ReturnsSortedOldestFirst()
    {
        var (container, blobs) = await azurite.CreateTestServiceAsync();
        var svc = new SnapshotService(blobs, s_enc, container.AccountName, container.Name);

        var ts1 = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ts2 = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var ts3 = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero);

        await svc.CreateAsync(s_rootHash, 1, 100, ts3);
        await svc.CreateAsync(s_rootHash, 1, 100, ts1);
        await svc.CreateAsync(s_rootHash, 1, 100, ts2);

        var names = await svc.ListBlobNamesAsync();

        names.Count.ShouldBe(3);
        SnapshotService.ParseTimestamp(names[0]).ShouldBe(ts1);
        SnapshotService.ParseTimestamp(names[1]).ShouldBe(ts2);
        SnapshotService.ParseTimestamp(names[2]).ShouldBe(ts3);
    }

    // ── Resolve by version string ─────────────────────────────────────────────

    [Test]
    public async Task ResolveAsync_WithVersion_ReturnsMatchingSnapshot()
    {
        var (container, blobs) = await azurite.CreateTestServiceAsync();
        var svc = new SnapshotService(blobs, s_enc, container.AccountName, container.Name);

        var ts1 = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ts2 = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero);

        await svc.CreateAsync(Root('1'), 1, 10, ts1);
        await svc.CreateAsync(Root('2'), 2, 20, ts2);

        // Resolve by year prefix
        var resolved = await svc.ResolveAsync("2024");
        resolved.ShouldNotBeNull();
        resolved!.FileCount.ShouldBe(1);
    }

    // ── No snapshots → resolve returns null ───────────────────────────────────

    [Test]
    public async Task ResolveAsync_NoSnapshots_ReturnsNull()
    {
        var (container, blobs) = await azurite.CreateTestServiceAsync();
        var svc = new SnapshotService(blobs, s_enc, container.AccountName, container.Name);

        var result = await svc.ResolveAsync();
        result.ShouldBeNull();
    }
}
