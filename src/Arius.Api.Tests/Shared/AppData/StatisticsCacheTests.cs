using Arius.Api.Shared.AppData;
using Shouldly;

namespace Arius.Api.Tests.Shared.AppData;

/// <summary>
/// Covers the statistics cache in <see cref="AppDatabase"/>: a hit is a local read that ignores the fingerprint;
/// writing a new fingerprint prunes the repository's older rows; the cache can be cleared and is deleted with its
/// repository. Callers keep it fresh by clearing it when the snapshot set or connection changes.
/// </summary>
public sealed class StatisticsCacheTests
{
    /// <summary>A fresh on-disk database with one repository for cached rows to reference.</summary>
    private static (AppDatabase Database, long RepositoryId, string Path) NewDatabase()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"arius-api-tests-{Guid.NewGuid():N}.db");
        var database = new AppDatabase(path);
        var accountId = database.InsertAccount("acc", encryptedAccountKey: null);
        var repositoryId = database.InsertRepository("alias", "container", accountId, localPath: null, encryptedPassphrase: null);
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
        // A hit is served regardless of the current snapshot fingerprint, so a warm load needs no blob-storage
        // round-trip. Freshness comes from explicit clears.
        var (database, repositoryId, _) = NewDatabase();
        database.UpsertCachedStatistics(repositoryId, version: "", full: true, fingerprint: "v1", payload: "PAYLOAD-1");

        database.GetCachedStatistics(repositoryId, version: "", full: true).ShouldBe("PAYLOAD-1");
    }

    [Test]
    public async Task Get_DiscriminatesByVersionAndFullFlag()
    {
        // The version and the full-coverage flag are part of the key.
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
        // When the fingerprint advances, writing one variant prunes the repository's older variants so the cache
        // cannot serve figures from an old snapshot set.
        var (database, repositoryId, _) = NewDatabase();
        database.UpsertCachedStatistics(repositoryId, version: "", full: true, fingerprint: "v1", payload: "FULL-OLD");
        database.UpsertCachedStatistics(repositoryId, version: "2024-01", full: true, fingerprint: "v1", payload: "OLD-SNAP");

        database.UpsertCachedStatistics(repositoryId, version: "", full: true, fingerprint: "v2", payload: "FULL-NEW");

        database.GetCachedStatistics(repositoryId, version: "", full: true).ShouldBe("FULL-NEW");
        database.GetCachedStatistics(repositoryId, version: "2024-01", full: true).ShouldBeNull();
    }

    [Test]
    public async Task Prune_DoesNotAffectOtherRepositories()
    {
        var (database, repositoryId, _) = NewDatabase();
        var otherAccountId = database.InsertAccount("acc2", null);
        var otherRepositoryId = database.InsertRepository("alias2", "container2", otherAccountId, null, null);

        database.UpsertCachedStatistics(otherRepositoryId, version: "", full: true, fingerprint: "v1", payload: "OTHER");
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

        // A new repository that reuses the id must not see a stale entry.
        database.GetCachedStatistics(repositoryId, version: "", full: true).ShouldBeNull();
    }
}
