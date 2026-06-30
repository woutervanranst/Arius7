using Arius.Core.Shared;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.HashCache;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Shared.HashCache;

public class HashCacheServiceTests
{
    [Test]
    public void Miss_WhenNoRow_ReturnsMiss()
    {
        var (svc, fs, path) = Setup([1, 2, 3, 4]);
        var r = svc.TryReuse(fs, path, fs.GetFileSize(path), now: 10);
        r.IsHit.ShouldBeFalse();
        r.Reason.ShouldBe("cache miss");
    }

    [Test]
    public void Hit_WhenCtimeMatches_SkipsReads()
    {
        var (svc, fs, path) = Setup([1, 2, 3, 4]);
        var hash = ContentHash.Parse(new string('a', 64));
        var sig  = fs.TryGetChangeSignals(path);
        if (sig is null) return; // floor-only platform; covered by Hit_WhenFpMatches
        svc.Record(path, fs.GetFileSize(path), sig, mtimeTicks: 0, [9, 9], hash, now: 1); // store bogus fp on purpose

        var r = svc.TryReuse(fs, path, fs.GetFileSize(path), now: 2);
        r.Hash.ShouldBe(hash);             // reused without consulting the (bogus) fp
        r.Reason.ShouldBe("ctime match");
    }

    [Test]
    public void Hit_WhenFpMatches_AfterCtimeMoved()
    {
        var (svc, fs, path) = Setup([1, 2, 3, 4]);
        var hash = ContentHash.Parse(new string('a', 64));
        var realFp = SparseFingerprint.ComputeBySeeking(fs, path, fs.GetFileSize(path));
        // Store a row whose ctime/inode won't match the live file (force the floor branch).
        svc.Record(path, fs.GetFileSize(path), signals: null, mtimeTicks: 0, realFp, hash, now: 1);

        var r = svc.TryReuse(fs, path, fs.GetFileSize(path), now: 2);
        r.Hash.ShouldBe(hash);
        r.Reason.ShouldBe("size+fp match");
    }

    [Test]
    public void Miss_WhenSizeChanged()
    {
        var (svc, fs, path) = Setup([1, 2, 3, 4]);
        svc.Record(path, size: 999, signals: null, mtimeTicks: 0, [1], ContentHash.Parse(new string('a', 64)), now: 1);
        var r = svc.TryReuse(fs, path, fs.GetFileSize(path), now: 2);
        r.IsHit.ShouldBeFalse();
        r.Reason.ShouldStartWith("size ");
    }

    [Test]
    public void Miss_WhenFpAlgoBumped()
    {
        var (svc, fs, path) = Setup([1, 2, 3, 4]);
        var fp = SparseFingerprint.ComputeBySeeking(fs, path, fs.GetFileSize(path));
        // Persist a row with a stale fp_algo via the store directly.
        var store = NewStore(out var root, fs);
        store.Upsert(new HashCacheEntry(path, fs.GetFileSize(path), 0, null, null, null,
            SignalSets.None, fp, FpAlgo: 999, ContentHash.Parse(new string('a', 64)), 1));
        var svc2 = new HashCacheService(store);
        svc2.TryReuse(fs, path, fs.GetFileSize(path), now: 2).Reason.ShouldBe("fp_algo bump");
    }

    [Test]
    public void Miss_WhenContentChangedInSampledRegion()
    {
        var (svc, fs, path) = Setup(Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray());
        var fp = SparseFingerprint.ComputeBySeeking(fs, path, fs.GetFileSize(path));
        svc.Record(path, fs.GetFileSize(path), signals: null, mtimeTicks: 0, fp, ContentHash.Parse(new string('a', 64)), now: 1);

        var bytes = fs.ReadAllBytes(path); bytes[0] ^= 0xFF; fs.WriteAllBytes(path, bytes); // same size
        svc.TryReuse(fs, path, fs.GetFileSize(path), now: 2).Reason.ShouldBe("fp differs");
    }

    [Test]
    public void Record_PersistsProvidedMtime_NotRunClock()
    {
        var (_, fs, path) = Setup([1, 2, 3, 4]);
        var store = NewStore(out _, fs);
        var svc   = new HashCacheService(store);

        // mtime is the file's last-write time, passed in by the caller — distinct from `now` (run clock).
        svc.Record(path, fs.GetFileSize(path), signals: null, mtimeTicks: 123_456_789, [1], ContentHash.Parse(new string('a', 64)), now: 999);

        var row = store.Find(path);
        row.ShouldNotBeNull();
        row!.Value.MtimeTicks.ShouldBe(123_456_789);        // the file mtime we recorded
        row.Value.LastVerifiedTicks.ShouldBe(999);          // the run clock, separately
    }

    private static (HashCacheService, RelativeFileSystem, RelativePath) Setup(byte[] data)
    {
        var dir  = LocalDirectory.Parse(Path.Combine(Path.GetTempPath(), $"arius-hcs-{Guid.NewGuid():N}"));
        var fs   = new RelativeFileSystem(dir);
        var path = RelativePath.Parse("f.bin");
        fs.WriteAllBytes(path, data);
        var store = NewStore(out _, fs);
        return (new HashCacheService(store), fs, path);
    }

    private static HashCacheLocalStore NewStore(out LocalDirectory root, RelativeFileSystem _)
    {
        var key = $"hcs-{Guid.NewGuid():N}";
        root = RepositoryLocalStatePaths.GetHashCacheRoot(key, key);
        return new HashCacheLocalStore(root);
    }
}
