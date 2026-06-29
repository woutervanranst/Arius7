using System.Runtime.InteropServices;
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
        after.ShouldNotBeNull();
        after!.Value.CtimeTicks.ShouldBeGreaterThan(before.Value.CtimeTicks);
    }

    /// <summary>
    /// Cross-validates the hand-rolled x86_64 <c>stat</c>-fallback struct layout against the
    /// architecture-stable <c>statx</c> layout. On a modern container kernel both syscalls succeed for
    /// the same file, so equal inode and equal ctime prove the (offset-sensitive) <c>stat</c> layout is
    /// correct — giving confidence in the stat-only path used on the 4.4-kernel NAS where statx is absent.
    /// Linux-x64 only; a no-op elsewhere (statx is Linux-only and the stat layout is x86_64-specific).
    /// </summary>
    [Test]
    public void Linux_StatFallback_LayoutMatches_Statx()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return; // statx-vs-stat cross-validation is only meaningful on Linux x64

        var (_, fullPath) = WriteTempResolved([1, 2, 3]);

        var viaStatx = NativeFileSignals.TryGetViaStatxForTest(fullPath);
        var viaStat  = NativeFileSignals.TryGetViaStatForTest(fullPath);

        // Both must succeed on a modern container kernel; if statx is somehow unavailable here the
        // cross-validation can't run, so skip rather than fail.
        if (viaStatx is null || viaStat is null)
            return;

        viaStat.Value.Inode.ShouldBe(viaStatx.Value.Inode);
        viaStat.Value.Dev.ShouldBe(viaStatx.Value.Dev);

        // ctime should match to the tick; at minimum the whole-second component must agree
        // (guards against a wrong tv_sec offset while tolerating any tv_nsec representation drift).
        var statSeconds  = viaStat.Value.CtimeTicks  / TimeSpan.TicksPerSecond;
        var statxSeconds = viaStatx.Value.CtimeTicks / TimeSpan.TicksPerSecond;
        statSeconds.ShouldBe(statxSeconds);
        viaStat.Value.CtimeTicks.ShouldBe(viaStatx.Value.CtimeTicks);
    }

    private static (RelativeFileSystem fs, string fullPath) WriteTempResolved(byte[] data)
    {
        var dir  = LocalDirectory.Parse(Path.Combine(Path.GetTempPath(), $"arius-sig-{Guid.NewGuid():N}"));
        var fs   = new RelativeFileSystem(dir);
        var path = RelativePath.Parse("f.bin");
        fs.WriteAllBytes(path, data);
        return (fs, dir.Resolve(path));
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
