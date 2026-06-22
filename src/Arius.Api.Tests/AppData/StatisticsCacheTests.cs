using Arius.Api.AppData;
using Shouldly;

namespace Arius.Api.Tests.AppData;

/// <summary>
/// Covers the statistics memoization in <see cref="AppDatabase"/>: a cached entry is served by a pure
/// local read (fingerprint independent — no blob storage on a hit); writing a new fingerprint prunes the
/// repository's prior-generation rows; and the cache can be cleared / cascades on repository delete.
/// Freshness is enforced by the callers that clear the cache when the snapshot set or connection changes.
/// </summary>
public sealed class StatisticsCacheTests
{
    /// <summary>A fresh on-disk database with one account + repository (the FK target for cached rows).</summary>
    private static (AppDatabase Database, long RepositoryId, string Path) NewDatabase()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"arius-api-tests-{Guid.NewGuid():N}.db");
        var database = new AppDatabase(path);
        var accountId = database.InsertAccount("acc", encryptedAccountKey: null);
        var repositoryId = database.InsertRepository("alias", "container", accountId, localPath: null, "archive", encryptedPassphrase: null);
        return (database, repositoryId, path);
    }

    [Test]
    public async Task GetCachedStatistics_NoEntry_ReturnsNull()
    {
        var (database, repositoryId, _) = NewDatabase();

        database.GetCachedStatistics(repositoryId, version: "", full: true).ShouldBeNull();
    }

    [Test]
    public async Task UpsertThenGet_ReturnsStoredPayload()
    {
        var (database, repositoryId, _) = NewDatabase();

        database.UpsertCachedStatistics(repositoryId, version: "", full: true, fingerprint: "v1", payload: "PAYLOAD-1");

        database.GetCachedStatistics(repositoryId, version: "", full: true).ShouldBe("PAYLOAD-1");
    }

    [Test]
    public async Task Get_IsAPureLocalRead_DoesNotDependOnFingerprint()
    {
        // A hit is served from the local row regardless of any current snapshot fingerprint — that is what
        // keeps a warm load fast (no blob-storage round-trip). Freshness is handled by explicit clears.
        var (database, repositoryId, _) = NewDatabase();
        database.UpsertCachedStatistics(repositoryId, version: "", full: true, fingerprint: "v1", payload: "PAYLOAD-1");

        database.GetCachedStatistics(repositoryId, version: "", full: true).ShouldBe("PAYLOAD-1");
    }

    [Test]
    public async Task Get_DiscriminatesByVersionAndFullFlag()
    {
        // The request variant (version + full-coverage flag) is part of the key: each is cached separately.
        var (database, repositoryId, _) = NewDatabase();
        database.UpsertCachedStatistics(repositoryId, version: "", full: true, fingerprint: "v1", payload: "FULL-LATEST");
        database.UpsertCachedStatistics(repositoryId, version: "", full: false, fingerprint: "v1", payload: "FAST-LATEST");
        database.UpsertCachedStatistics(repositoryId, version: "2024-01", full: true, fingerprint: "v1", payload: "FULL-OLD");

        database.GetCachedStatistics(repositoryId, version: "", full: true).ShouldBe("FULL-LATEST");
        database.GetCachedStatistics(repositoryId, version: "", full: false).ShouldBe("FAST-LATEST");
        database.GetCachedStatistics(repositoryId, version: "2024-01", full: true).ShouldBe("FULL-OLD");
        database.GetCachedStatistics(repositoryId, version: "2024-01", full: false).ShouldBeNull();
    }

    [Test]
    public async Task Upsert_SameKey_OverwritesPayload()
    {
        var (database, repositoryId, _) = NewDatabase();
        database.UpsertCachedStatistics(repositoryId, version: "", full: true, fingerprint: "v1", payload: "OLD");

        database.UpsertCachedStatistics(repositoryId, version: "", full: true, fingerprint: "v2", payload: "NEW");

        database.GetCachedStatistics(repositoryId, version: "", full: true).ShouldBe("NEW");
    }

    [Test]
    public async Task Upsert_NewFingerprint_PrunesStaleVariantsForThatRepository()
    {
        // When the fingerprint advances (a recompute against a new snapshot set), writing one variant
        // prunes the repo's other now-stale variants so the cache cannot serve figures from an old set.
        var (database, repositoryId, _) = NewDatabase();
        database.UpsertCachedStatistics(repositoryId, version: "", full: true, fingerprint: "v1", payload: "FULL-OLD");
        database.UpsertCachedStatistics(repositoryId, version: "2024-01", full: true, fingerprint: "v1", payload: "OLD-SNAP");

        database.UpsertCachedStatistics(repositoryId, version: "", full: true, fingerprint: "v2", payload: "FULL-NEW");

        database.GetCachedStatistics(repositoryId, version: "", full: true).ShouldBe("FULL-NEW");
        database.GetCachedStatistics(repositoryId, version: "2024-01", full: true).ShouldBeNull(); // pruned (older fingerprint)
    }

    [Test]
    public async Task Prune_DoesNotAffectOtherRepositories()
    {
        var (database, repositoryId, _) = NewDatabase();
        var otherAccountId = database.InsertAccount("acc2", null);
        var otherRepositoryId = database.InsertRepository("alias2", "container2", otherAccountId, null, "archive", null);

        database.UpsertCachedStatistics(otherRepositoryId, version: "", full: true, fingerprint: "v1", payload: "OTHER");
        // Writing repo #1 with a different fingerprint must not prune repo #2's entry.
        database.UpsertCachedStatistics(repositoryId, version: "", full: true, fingerprint: "v9", payload: "MINE");

        database.GetCachedStatistics(otherRepositoryId, version: "", full: true).ShouldBe("OTHER");
    }

    [Test]
    public async Task ClearStatisticsCache_RemovesAllVariantsForRepository()
    {
        var (database, repositoryId, _) = NewDatabase();
        database.UpsertCachedStatistics(repositoryId, version: "", full: true, fingerprint: "v1", payload: "A");
        database.UpsertCachedStatistics(repositoryId, version: "", full: false, fingerprint: "v1", payload: "B");

        database.ClearStatisticsCache(repositoryId);

        database.GetCachedStatistics(repositoryId, version: "", full: true).ShouldBeNull();
        database.GetCachedStatistics(repositoryId, version: "", full: false).ShouldBeNull();
    }

    [Test]
    public async Task DeleteRepository_CascadesCachedStatistics()
    {
        var (database, repositoryId, _) = NewDatabase();
        database.UpsertCachedStatistics(repositoryId, version: "", full: true, fingerprint: "v1", payload: "A");

        database.DeleteRepository(repositoryId);

        // The row is gone (cascade), so a fresh repo reusing the id would never see a stale entry.
        database.GetCachedStatistics(repositoryId, version: "", full: true).ShouldBeNull();
    }
}
