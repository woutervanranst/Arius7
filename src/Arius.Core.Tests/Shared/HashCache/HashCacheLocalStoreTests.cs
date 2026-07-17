using Arius.Core.Shared;
using Arius.Core.Shared.HashCache;
using Microsoft.Data.Sqlite;

namespace Arius.Core.Tests.Shared.HashCache;

public class HashCacheLocalStoreTests
{
    [Test]
    public void Initialize_CreatesSchemaVersionAndWalMode()
    {
        var store = NewStore(out var root);
        using var c = Open(root);
        using var v = c.CreateCommand();
        v.CommandText = "SELECT value FROM metadata WHERE key='schema_version';";
        v.ExecuteScalar().ShouldBe("1");
        using var j = c.CreateCommand();
        j.CommandText = "PRAGMA journal_mode;";
        j.ExecuteScalar().ShouldBe("wal");
    }

    [Test]
    public void Upsert_AndFind_RoundTrips()
    {
        var store = NewStore(out _);
        var entry = Sample();
        store.Upsert(entry);
        var found = store.Find(entry.Path)!.Value;
        found.Size.ShouldBe(entry.Size);
        found.CtimeTicks.ShouldBe(entry.CtimeTicks);
        found.Inode.ShouldBe(entry.Inode);
        found.SparseFingerprint.ShouldBe(entry.SparseFingerprint);
        found.ContentHash.ShouldBe(entry.ContentHash);
        found.SignalSet.ShouldBe(entry.SignalSet);
    }

    [Test]
    public void Upsert_SamePath_LastWriterWins()
    {
        var store = NewStore(out _);
        var first  = Sample();
        var second = first with { Size = 999, ContentHash = ContentHash.Parse(new string('b', 64)) };
        store.Upsert(first);
        store.Upsert(second);
        store.Find(first.Path)!.Value.Size.ShouldBe(999);
    }

    [Test]
    public void Find_MissingPath_ReturnsNull()
    {
        var store = NewStore(out _);
        store.Find(RelativePath.Parse("nope.bin")).ShouldBeNull();
    }

    [Test]
    public void Delete_RemovesRow()
    {
        var store = NewStore(out _);
        var entry = Sample();
        store.Upsert(entry);
        store.Delete(entry.Path);
        store.Find(entry.Path).ShouldBeNull();
    }

    private static HashCacheEntry Sample() => new(
        Path: RelativePath.Parse("dir/file.bin"),
        Size: 1234, MtimeTicks: 100, CtimeTicks: 200, Inode: "42", Dev: "dev-1",
        SignalSet: 1, SparseFingerprint: [1, 2, 3, 4], FpAlgo: SparseFingerprint.Algo,
        ContentHash: ContentHash.Parse(new string('a', 64)), LastVerifiedTicks: 300);

    private static HashCacheLocalStore NewStore(out LocalDirectory root)
    {
        var key = $"hc-{Guid.NewGuid():N}";
        root = RepositoryLocalStatePaths.GetHashCacheRoot(key, key);
        return new HashCacheLocalStore(root);
    }

    private static SqliteConnection Open(LocalDirectory root)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = root.Resolve(RelativePath.Root / PathSegment.Parse("cache.sqlite")),
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        c.Open();
        return c;
    }
}
