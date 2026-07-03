using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Arius.Core.Shared.HashCache;

namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Platform-specific capture of (ctime, inode, dev). Returns <c>null</c> on network filesystems
/// (SMB/CIFS/NFS) and anywhere the signals can't be trusted, so the caller falls back to the
/// sparse-fingerprint floor. See the spec's "Platform signal mapping".
/// </summary>
/// <remarks>
/// The POSIX path uses hand-rolled <see cref="LibraryImportAttribute"/> interop against libc instead of
/// a third-party wrapper. Native struct layouts are reproduced with explicit field offsets — a wrong
/// offset would yield a silently-wrong signal, so only the fields actually read are mapped, and the
/// architecture-specific layouts are guarded by <see cref="RuntimeInformation.ProcessArchitecture"/>.
/// </remarks>
internal static partial class NativeFileSignals
{
    public static FileChangeSignals? TryGet(string fullPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return TryGetWindows(fullPath);
            if (OperatingSystem.IsLinux())
                return TryGetLinux(fullPath);
            if (OperatingSystem.IsMacOS())
                return TryGetMacOs(fullPath);
            return null;
        }
        catch
        {
            return null; // never fault the archive pipeline over a signals probe
        }
    }

    // =====================================================================================
    // Windows via GetFileInformationByHandleEx (unchanged — no Mono dependency).
    // =====================================================================================

    private static FileChangeSignals? TryGetWindows(string fullPath)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(fullPath));
        if (root is not null && GetDriveType(root) == DRIVE_REMOTE)
            return null;

        using var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        if (!GetFileInformationByHandleEx(handle, FileBasicInfo, out FILE_BASIC_INFO basic, Marshal.SizeOf<FILE_BASIC_INFO>()))
            return null;
        if (!GetFileInformationByHandleEx(handle, FileIdInfo, out FILE_ID_INFO id, Marshal.SizeOf<FILE_ID_INFO>()))
            return null;

        // ChangeTime is a FILETIME (100 ns ticks since 1601) → UTC ticks.
        var ctimeTicks = DateTime.FromFileTimeUtc(basic.ChangeTime).Ticks;
        return new FileChangeSignals(
            CtimeTicks: ctimeTicks,
            Inode: Convert.ToHexString(id.FileId),                 // 128-bit FileId
            Dev: id.VolumeSerialNumber.ToString(),
            SignalSet: SignalSets.Windows);
    }

    private const int DRIVE_REMOTE = 4;
    private const int FileBasicInfo = 0;
    private const int FileIdInfo = 18;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetDriveType(string lpRootPathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int infoClass, out FILE_BASIC_INFO info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int infoClass, out FILE_ID_INFO info, int size);

    [StructLayout(LayoutKind.Sequential)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase",
        Justification = "Matches the Win32 FILE_BASIC_INFO struct name; only the field layout is marshaled.")]
    private struct FILE_BASIC_INFO
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase",
        Justification = "Matches the Win32 FILE_ID_INFO struct name; only the field layout is marshaled.")]
    private struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] FileId;
    }


    // =====================================================================================
    // Linux: statx (kernel-stable layout) with a stat fallback for kernels < 4.11.
    // =====================================================================================

    private static FileChangeSignals? TryGetLinux(string fullPath)
    {
        if (IsLinuxNetworkFs(fullPath))
            return null;

        return TryGetViaStatx(fullPath) ?? TryGetViaStat(fullPath);
    }

    /// <summary>
    /// Captures signals via <c>statx</c>. The <c>struct statx</c> layout is architecture-stable
    /// (identical on x86_64 / arm64), so this is preferred over <c>stat</c>. Returns <c>null</c>
    /// when <c>statx</c> is unavailable (ENOSYS on kernel &lt; 4.11) or fails for any other reason,
    /// in which case the caller falls back to <see cref="TryGetViaStat"/>.
    /// </summary>
    private static FileChangeSignals? TryGetViaStatx(string fullPath)
    {
        var buf = default(StatxBuf);
        var rc  = statx(AT_FDCWD, fullPath, 0, STATX_BASIC_STATS, ref buf);
        if (rc != 0)
            return null; // ENOSYS (kernel < 4.11) or any other error → fall back to stat

        var ctimeTicks = ToUtcTicks(buf.CtimeSeconds, buf.CtimeNanos);
        return new FileChangeSignals(
            CtimeTicks: ctimeTicks,
            Inode:      buf.Ino.ToString(),
            Dev:        $"{buf.DevMajor}:{buf.DevMinor}",
            SignalSet:  SignalSets.Posix);
    }

    /// <summary>
    /// Captures signals via the classic <c>stat</c> syscall, used as the fallback when <c>statx</c>
    /// is unavailable (Synology DS918+ runs a 4.4 kernel). The <c>struct stat</c> layout is
    /// architecture-specific; only the x86_64 layout is implemented, so this returns <c>null</c> on
    /// any other architecture rather than risk reading a wrong layout.
    /// </summary>
    private static FileChangeSignals? TryGetViaStat(string fullPath)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return null; // only the x86_64 struct stat layout is implemented

        var buf = default(LinuxStatBuf);
        if (stat(fullPath, ref buf) != 0)
            return null;

        var ctimeTicks = ToUtcTicks(buf.CtimeSeconds, buf.CtimeNanos);
        return new FileChangeSignals(
            CtimeTicks: ctimeTicks,
            Inode:      buf.Ino.ToString(),
            Dev:        DecodeLinuxDev(buf.Dev), // matches the statx "major:minor" format
            SignalSet:  SignalSets.Posix);
    }

    /// <summary>
    /// Decodes a glibc raw <c>dev_t</c> to the <c>"major:minor"</c> string so a <c>stat</c>-sourced
    /// dev compares equal to a <c>statx</c>-sourced dev for the same file on the same machine.
    /// Uses the glibc <c>gnu_dev_major</c>/<c>gnu_dev_minor</c> bit layout.
    /// </summary>
    private static string DecodeLinuxDev(ulong dev)
    {
        var major = (uint)(((dev >> 8) & 0xfff) | ((dev >> 32) & ~0xfffUL));
        var minor = (uint)((dev & 0xff) | ((dev >> 12) & ~0xffUL));
        return $"{major}:{minor}";
    }

    /// <summary>
    /// Best-effort network-filesystem detection via <c>statfs</c>. Returns <c>true</c> only when the
    /// filesystem magic identifies SMB/CIFS/NFS. If <c>statfs</c> fails we trust the path as local
    /// (safe: the hashcache is local and the verdict ladder re-validates), and it never throws.
    /// </summary>
    private static bool IsLinuxNetworkFs(string fullPath)
    {
        try
        {
            var buf = default(StatfsBuf);
            if (statfs(fullPath, ref buf) != 0)
                return false; // can't classify → trust as local (safe)

            return buf.Type is CIFS_MAGIC or SMB2_MAGIC or NFS_MAGIC;
        }
        catch
        {
            return false; // best-effort only; never fault on network detection
        }
    }

    // ---- Linux libc imports -------------------------------------------------

    private const int  AT_FDCWD          = -100;       // operate relative to the current working dir
    private const uint STATX_BASIC_STATS = 0x000007ffu; // request the basic stat fields

    private const long CIFS_MAGIC = 0xFF534D42L; // CIFS
    private const long SMB2_MAGIC = unchecked((long)0xFE534D42UL); // SMB2 / SMB3
    private const long NFS_MAGIC  = 0x6969L;     // NFS

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int statx(int dirfd, string pathname, int flags, uint mask, ref StatxBuf buf);

    [LibraryImport("libc", EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int stat(string pathname, ref LinuxStatBuf buf);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int statfs(string path, ref StatfsBuf buf);

    /// <summary>
    /// Subset of <c>struct statx</c> (256 bytes, architecture-stable). Only the fields read here are
    /// mapped at their documented byte offsets.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuf
    {
        [FieldOffset(32)]  public ulong Ino;          // stx_ino
        [FieldOffset(96)]  public long  CtimeSeconds; // stx_ctime.tv_sec
        [FieldOffset(104)] public uint  CtimeNanos;   // stx_ctime.tv_nsec
        [FieldOffset(136)] public uint  DevMajor;     // stx_dev_major
        [FieldOffset(140)] public uint  DevMinor;     // stx_dev_minor
    }

    /// <summary>
    /// Subset of the x86_64 glibc <c>struct stat</c> (144 bytes). Only the fields read here are
    /// mapped at their x86_64 byte offsets — guarded to x64 by the caller.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct LinuxStatBuf
    {
        [FieldOffset(0)]   public ulong Dev;          // st_dev
        [FieldOffset(8)]   public ulong Ino;          // st_ino
        [FieldOffset(104)] public long  CtimeSeconds; // st_ctim.tv_sec
        [FieldOffset(112)] public long  CtimeNanos;   // st_ctim.tv_nsec
    }

    /// <summary>
    /// Subset of the x86_64 glibc <c>struct statfs</c>. <c>f_type</c> is the first field (8 bytes on
    /// x86_64). A generous <c>Size</c> keeps the marshaller from reading past the buffer.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct StatfsBuf
    {
        [FieldOffset(0)] public long Type; // f_type
    }


    // =====================================================================================
    // macOS (Darwin, arm64 only): stat with the 64-bit-inode struct.
    // =====================================================================================

    private static FileChangeSignals? TryGetMacOs(string fullPath)
    {
        // Apple Silicon (arm64) only. On x86_64 macOS the bare `stat` symbol can resolve to the legacy
        // 32-bit-inode variant (no $INODE64), which would misread the DarwinStatBuf offsets and yield a
        // silently-wrong signal. Intel Macs are unsupported, so we return null and fall to the
        // fingerprint floor rather than risk a wrong reuse.
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            return null;

        // The target volume is local; macOS network detection is intentionally skipped.
        var buf = default(DarwinStatBuf);
        if (darwin_stat(fullPath, ref buf) != 0)
            return null;

        var ctimeTicks = ToUtcTicks(buf.CtimeSeconds, buf.CtimeNanos);
        return new FileChangeSignals(
            CtimeTicks: ctimeTicks,
            Inode:      buf.Ino.ToString(),
            Dev:        buf.Dev.ToString(),
            SignalSet:  SignalSets.Posix);
    }

    [LibraryImport("libc", EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int darwin_stat(string pathname, ref DarwinStatBuf buf);

    /// <summary>
    /// Subset of the Darwin arm64 64-bit-inode <c>struct stat</c> (the only macOS architecture supported;
    /// the caller guards to arm64). Only the fields read here are mapped at their byte offsets;
    /// <c>Size</c> is generous to cover the full struct.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct DarwinStatBuf
    {
        [FieldOffset(0)]  public int   Dev;          // st_dev (dev_t = int32)
        [FieldOffset(8)]  public ulong Ino;          // st_ino (64-bit)
        [FieldOffset(64)] public long  CtimeSeconds; // st_ctimespec.tv_sec
        [FieldOffset(72)] public long  CtimeNanos;   // st_ctimespec.tv_nsec
    }


    // =====================================================================================
    // Shared helpers.
    // =====================================================================================

    /// <summary>Converts a POSIX (seconds, nanoseconds) ctime to UTC ticks (1 tick = 100 ns).</summary>
    private static long ToUtcTicks(long tvSec, long tvNsec)
        => DateTimeOffset.FromUnixTimeSeconds(tvSec).UtcTicks + tvNsec / 100;

    // =====================================================================================
    // Internal test seams: force a specific Linux capture path so the x86_64 stat-fallback
    // layout can be cross-validated against the kernel-stable statx layout on a modern kernel.
    // =====================================================================================

    internal static FileChangeSignals? TryGetViaStatxForTest(string fullPath) => TryGetViaStatx(fullPath);

    internal static FileChangeSignals? TryGetViaStatForTest(string fullPath) => TryGetViaStat(fullPath);
}
