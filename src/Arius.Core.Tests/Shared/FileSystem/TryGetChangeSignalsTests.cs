using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.HashCache;

namespace Arius.Core.Tests.Shared.FileSystem;

public class TryGetChangeSignalsTests
{
    [Test]
    public void LocalFile_OnNativeFilesystem_YieldsStableSignals()
    {
        var (fs, path) = WriteTemp([1, 2, 3]);
        var a = fs.TryGetChangeSignals(path);
        var b = fs.TryGetChangeSignals(path);

        // On the CI runner's native FS we expect signals; assert stability + shape when present.
        if (a is null) return; // unsupported FS on this runner → floor path is exercised elsewhere
        b.ShouldNotBeNull();
        a.Value.Inode.ShouldBe(b!.Value.Inode);
        a.Value.Dev.ShouldBe(b.Value.Dev);
        a.Value.CtimeTicks.ShouldBe(b.Value.CtimeTicks);
        a.Value.SignalSet.ShouldBeOneOf(SignalSets.Posix, SignalSets.Windows);
    }

    [Test]
    public void Rewriting_Content_MovesCtime()
    {
        var (fs, path) = WriteTemp([1, 2, 3]);
        var before = fs.TryGetChangeSignals(path);
        if (before is null) return;

        Thread.Sleep(20);
        fs.WriteAllBytes(path, [4, 5, 6, 7]);
        var after = fs.TryGetChangeSignals(path);
        after!.Value.CtimeTicks.ShouldBeGreaterThan(before.Value.CtimeTicks);
    }

    private static (RelativeFileSystem, RelativePath) WriteTemp(byte[] data)
    {
        var dir  = LocalDirectory.Parse(Path.Combine(Path.GetTempPath(), $"arius-sig-{Guid.NewGuid():N}"));
        var fs   = new RelativeFileSystem(dir);
        var path = RelativePath.Parse("f.bin");
        fs.WriteAllBytes(path, data);
        return (fs, path);
    }
}
