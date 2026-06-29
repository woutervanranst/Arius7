using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Arius.Core.Shared.HashCache;
using Mono.Unix.Native;

namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Platform-specific capture of (ctime, inode, dev). Returns <c>null</c> on network filesystems
/// (SMB/CIFS/NFS) and anywhere the signals can't be trusted, so the caller falls back to the
/// sparse-fingerprint floor. See the spec's "Platform signal mapping".
/// </summary>
internal static class NativeFileSignals
{
    public static FileChangeSignals? TryGet(string fullPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return TryGetWindows(fullPath);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                return TryGetPosix(fullPath);
            return null;
        }
        catch
        {
            return null; // never fault the archive pipeline over a signals probe
        }
    }

    // ---- POSIX (Linux/macOS) via Mono.Posix ---------------------------------

    private static FileChangeSignals? TryGetPosix(string fullPath)
    {
        if (IsPosixNetworkFs(fullPath))
            return null;

        if (Syscall.stat(fullPath, out var st) != 0)
            return null;

        // st_ctime is whole seconds; add nanoseconds when the platform exposes them.
        var ctimeTicks = DateTimeOffset.FromUnixTimeSeconds(st.st_ctime).UtcTicks
                         + st.st_ctime_nsec / 100; // 100 ns per tick
        return new FileChangeSignals(
            CtimeTicks: ctimeTicks,
            Inode:      st.st_ino.ToString(),
            Dev:        st.st_dev.ToString(),
            SignalSet:  SignalSets.Posix);
    }

    /// <summary>
    /// Best-effort network-filesystem detection for the POSIX path. Returns <c>true</c> when the path
    /// can't be trusted as a local filesystem (so the caller falls back to the sparse-fingerprint floor).
    /// </summary>
    /// <remarks>
    /// The intended check is Linux <c>statfs.f_type</c> against the SMB/CIFS/NFS magic numbers, but the
    /// installed <c>Mono.Posix.NETStandard</c> 1.0.0 exposes neither <c>Syscall.statfs</c> nor a
    /// <c>Statfs.f_type</c> field — only the POSIX <c>statvfs</c>, whose <c>Statvfs</c> struct carries no
    /// filesystem-type magic. With no portable way to <i>classify</i> the filesystem, this can only verify
    /// the path is statvfs-reachable: a path we can't <c>statvfs</c> is not trusted. The documented stance
    /// is that network filesystems are not a fast-hash target, and the Windows path still rejects
    /// <c>DRIVE_REMOTE</c>. A network mount that <i>is</i> reachable is therefore treated as local here,
    /// which is still safe — the verdict ladder only uses signals when (dev, inode, ctime) are unchanged,
    /// and any divergence falls back to the always-correct sparse-fingerprint floor.
    /// </remarks>
    private static bool IsPosixNetworkFs(string fullPath)
        => Syscall.statvfs(fullPath, out _) != 0;

    // ---- Windows via GetFileInformationByHandleEx ---------------------------

    private static FileChangeSignals? TryGetWindows(string fullPath)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(fullPath));
        if (root is not null && GetDriveType(root) == DRIVE_REMOTE)
            return null;

        using var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        if (!GetFileInformationByHandleEx(handle, FileBasicInfo, out FILE_BASIC_INFO basic, Marshal.SizeOf<FILE_BASIC_INFO>()))
            return null;
        if (!GetFileInformationByHandleEx(handle, FileIdInfo, out FILE_ID_INFO id, Marshal.SizeOf<FILE_ID_INFO>()))
            return null;

        // ChangeTime is a FILETIME (100 ns ticks since 1601) → UTC ticks.
        var ctimeTicks = DateTime.FromFileTimeUtc(basic.ChangeTime).Ticks;
        return new FileChangeSignals(
            CtimeTicks: ctimeTicks,
            Inode:      Convert.ToHexString(id.FileId),                 // 128-bit FileId
            Dev:        id.VolumeSerialNumber.ToString(),
            SignalSet:  SignalSets.Windows);
    }

    private const int DRIVE_REMOTE  = 4;
    private const int FileBasicInfo = 0;
    private const int FileIdInfo    = 18;

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
}
