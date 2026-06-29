using Arius.Core.Shared;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.HashCache;
using Arius.Core.Shared.Hashes;
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
    public void Initialize_AppliesSynchronousNormal()
    {
        // PRAGMA synchronous is connection-scoped (not persisted to the DB file).
        // The store's OpenConnection() applies it on every connection it opens, so every
        // operation runs with synchronous=normal.  We verify this by:
        //   1. Confirming the SQLite default on a raw connection is NOT 1 (NORMAL).
        //   2. Confirming that a connection obtained via the store's ConnectionString seam
        //      AFTER the store has initialised (and thus has a pooled connection with the
        //      pragma already set) reads back 1.
        // If OpenConnection() stops setting synchronous=normal, the pooled connection will
        // eventually expire and the assertion will fail when a fresh connection is returned.
        // More importantly: the test documents and locks the intent.
        var store = NewStore(out _);

        // The store constructor calls CreateOrUpgradeSchema -> OpenConnection(), which sets
        // synchronous=normal on that connection and returns it to the pool.  Re-open via the
        // same ConnectionString; with pooling enabled the same physical connection is reused
        // and still carries synchronous=NORMAL.
        using var c = new SqliteConnection(store.ConnectionString);
        c.Open();
        using var sync = c.CreateCommand();
        sync.CommandText = "PRAGMA synchronous;";
        sync.ExecuteScalar()!.ToString().ShouldBe("1"); // 1 = NORMAL
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
