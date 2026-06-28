using Arius.Api.AppData;
using Arius.Api.Endpoints;
using Arius.Core.Features.StorageAccountInfoQuery;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Arius.Api.Tests;

/// <summary>
/// Covers the DB-backed read-through cache for a repository's region (see
/// <see cref="RepositoryEndpoints.ResolveRegionDtoAsync"/> and <see cref="AppDatabase.SetRepositoryRegionHint"/>):
/// a configured region is memoized (immutable once set) and served without opening the container, while an
/// unset/unknown region is resolved live and only persisted when it is a real configured region.
/// </summary>
public sealed class RepositoryRegionResolutionTests
{
    private static (AppDatabase Database, long RepositoryId) NewDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), $"arius-api-tests-{Guid.NewGuid():N}.db");
        var database = new AppDatabase(path);
        var accountId = database.InsertAccount("acc", encryptedAccountKey: null);
        var repositoryId = database.InsertRepository("alias", "container", accountId, localPath: null, "archive", encryptedPassphrase: null);
        return (database, repositoryId);
    }

    // ── DB cache layer ──────────────────────────────────────────────────────────

    [Test]
    public async Task NewRepository_HasNoCachedRegion()
    {
        var (database, repositoryId) = NewDatabase();

        database.GetRepository(repositoryId)!.RegionHint.ShouldBeNull();
    }

    [Test]
    public async Task SetRegionHint_PersistsAndIsReadBack()
    {
        var (database, repositoryId) = NewDatabase();

        database.SetRepositoryRegionHint(repositoryId, "westeurope");

        database.GetRepository(repositoryId)!.RegionHint.ShouldBe("westeurope");
        database.ListRepositories().Single(r => r.Id == repositoryId).RegionHint.ShouldBe("westeurope");
    }

    [Test]
    public async Task SetRegionHint_Null_InvalidatesTheCache()
    {
        var (database, repositoryId) = NewDatabase();
        database.SetRepositoryRegionHint(repositoryId, "westeurope");

        database.SetRepositoryRegionHint(repositoryId, null);

        database.GetRepository(repositoryId)!.RegionHint.ShouldBeNull();
    }

    [Test]
    public async Task Migration_AddsRegionHintColumnToAPreExistingDatabase()
    {
        // A database created before region_hint existed: CREATE TABLE IF NOT EXISTS won't add the column on
        // upgrade, so the additive ALTER in CreateOrUpgradeSchema must. Build the old schema by hand, seed a row,
        // then open AppDatabase (which runs the migration) and confirm the repo loads and the cache is usable.
        var path = Path.Combine(Path.GetTempPath(), $"arius-api-tests-mig-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using (var seed = new SqliteConnection(connectionString))
        {
            seed.Open();
            using var cmd = seed.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE storage_accounts (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE, account_key TEXT, created_at TEXT NOT NULL);
                CREATE TABLE repositories (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, alias TEXT NOT NULL, container TEXT NOT NULL,
                    account_id INTEGER NOT NULL REFERENCES storage_accounts(id), local_path TEXT,
                    default_tier TEXT NOT NULL DEFAULT 'archive', passphrase TEXT, created_at TEXT NOT NULL,
                    UNIQUE(account_id, container));
                INSERT INTO storage_accounts(id, name, created_at) VALUES (1, 'acc', '2024-01-01T00:00:00.0000000+00:00');
                INSERT INTO repositories(id, alias, container, account_id, default_tier, created_at)
                    VALUES (1, 'alias', 'container', 1, 'archive', '2024-01-01T00:00:00.0000000+00:00');
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearPool(new SqliteConnection(connectionString)); // release the seed file handle before reopening

        var database = new AppDatabase(path); // runs the additive region_hint migration

        var repository = database.GetRepository(1)!;
        repository.RegionHint.ShouldBeNull();           // column added, defaults to null on the existing row
        database.SetRepositoryRegionHint(1, "westeurope");
        database.GetRepository(1)!.RegionHint.ShouldBe("westeurope");
    }

    // ── Read-through resolution ─────────────────────────────────────────────────

    [Test]
    public async Task CachedRegion_IsServedWithoutResolvingLive()
    {
        var (database, repositoryId) = NewDatabase();
        database.SetRepositoryRegionHint(repositoryId, "westeurope");
        var repository = database.GetRepository(repositoryId)!;

        var resolvedLive = false;
        var dto = await RepositoryEndpoints.ResolveRegionDtoAsync(
            database, repository,
            (_, _) => { resolvedLive = true; return Task.FromResult<StorageAccountInfo?>(null); },
            CancellationToken.None);

        resolvedLive.ShouldBeFalse(); // the whole point: a cached region must not open the container
        dto.Region.ShouldBe("westeurope");
        dto.RegionIsDefault.ShouldBeFalse();
    }

    [Test]
    public async Task UncachedConfiguredRegion_IsResolvedLive_ThenCached()
    {
        var (database, repositoryId) = NewDatabase();
        var repository = database.GetRepository(repositoryId)!;

        var calls = 0;
        var dto = await RepositoryEndpoints.ResolveRegionDtoAsync(
            database, repository,
            (_, _) => { calls++; return Task.FromResult<StorageAccountInfo?>(new StorageAccountInfo("westeurope", RegionIsDefault: false)); },
            CancellationToken.None);

        calls.ShouldBe(1);
        dto.Region.ShouldBe("westeurope");
        dto.RegionIsDefault.ShouldBeFalse();
        database.GetRepository(repositoryId)!.RegionHint.ShouldBe("westeurope"); // persisted for next time
    }

    [Test]
    public async Task UnsetRegion_IsResolvedLive_ButNotCached()
    {
        var (database, repositoryId) = NewDatabase();
        var repository = database.GetRepository(repositoryId)!;

        var dto = await RepositoryEndpoints.ResolveRegionDtoAsync(
            database, repository,
            (_, _) => Task.FromResult<StorageAccountInfo?>(new StorageAccountInfo("northeurope", RegionIsDefault: true)),
            CancellationToken.None);

        dto.Region.ShouldBe("northeurope");
        dto.RegionIsDefault.ShouldBeTrue();
        // Not cached: an unconfigured container must keep re-resolving so it picks up a region once one is set.
        database.GetRepository(repositoryId)!.RegionHint.ShouldBeNull();
    }

    [Test]
    public async Task UnreadableRegion_YieldsNullRegion_AndIsNotCached()
    {
        var (database, repositoryId) = NewDatabase();
        var repository = database.GetRepository(repositoryId)!;

        var dto = await RepositoryEndpoints.ResolveRegionDtoAsync(
            database, repository,
            (_, _) => Task.FromResult<StorageAccountInfo?>(null), // best-effort live resolve failed
            CancellationToken.None);

        dto.Region.ShouldBeNull();
        dto.RegionIsDefault.ShouldBeFalse();
        database.GetRepository(repositoryId)!.RegionHint.ShouldBeNull();
    }
}
