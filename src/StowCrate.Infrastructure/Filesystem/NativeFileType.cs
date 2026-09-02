using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StowCrate.Core.Filesystem;

namespace StowCrate.Infrastructure.Filesystem;

internal static class NativeFileType
{
    internal readonly record struct FileIdentity(ulong DeviceOrVolume, ulong FileId);
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint IoReparseTagSymbolicLink = 0xA000000C;
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixRegularFile = 0x8000;
    private const uint UnixDirectory = 0x4000;
    private const uint UnixSymbolicLink = 0xA000;
    private const uint UnixExecutableMask = 0x49;
    private const int AtFileDescriptorCurrentWorkingDirectory = -100;
    private const int AtSymbolicLinkNoFollow = 0x100;
    private const uint StatXBasicStats = 0x7ff;
    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static FileSystemEntryKind GetEntryKind(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return isDirectory ? FileSystemEntryKind.Directory : FileSystemEntryKind.File;
        }

        var mode = GetUnixMode(path);
        return (mode & UnixFileTypeMask) switch
        {
            UnixRegularFile => FileSystemEntryKind.File,
            UnixDirectory => FileSystemEntryKind.Directory,
            UnixSymbolicLink => FileSystemEntryKind.Link,
            _ => FileSystemEntryKind.Special,
        };
    }

    public static bool IsExecutable(string path)
    {
        return !OperatingSystem.IsWindows() && (GetUnixMode(path) & UnixExecutableMask) != 0;
    }

    public static LinkKind GetLinkKind(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return LinkKind.SymbolicLink;
        }

        var handle = FindFirstFile(path, out var data);
        if (handle == InvalidHandleValue)
        {
            return LinkKind.Other;
        }

        try
        {
            return data.Reserved0 switch
            {
                IoReparseTagSymbolicLink => LinkKind.SymbolicLink,
                IoReparseTagMountPoint when IsVolumeTarget(path) => LinkKind.MountPoint,
                IoReparseTagMountPoint => LinkKind.Junction,
                _ => LinkKind.Other,
            };
        }
        finally
        {
            _ = FindClose(handle);
        }
    }

    public static FileIdentity GetIdentityNoFollow(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // FILE_FLAG_BACKUP_SEMANTICS 使同一 no-follow identity primitive 同时覆盖普通文件与目录。
            using var handle = CreateFile(path, GenericRead, ShareReadWriteDelete, IntPtr.Zero, OpenExisting,
                FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException($"无法打开文件系统对象 identity：{path}", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
            if (!GetFileInformationByHandle(handle, out var information))
                throw new IOException($"无法读取文件系统对象 identity：{path}", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
            return new(information.VolumeSerialNumber, ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
        }

        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            var result = OperatingSystem.IsLinux()
                ? StatX(AtFileDescriptorCurrentWorkingDirectory, path, AtSymbolicLinkNoFollow, StatXBasicStats, buffer)
                : LStat(path, buffer);
            if (result != 0) throw new IOException($"无法读取文件系统对象 identity：{path}");
            if (OperatingSystem.IsMacOS())
                return new(unchecked((uint)Marshal.ReadInt32(buffer, 0)), unchecked((ulong)Marshal.ReadInt64(buffer, 8)));
            // Linux statx ABI 中 stx_ino 位于 32，stx_dev_major/minor 位于 136/140；组合后只用于同平台同轮次 identity 比较。
            var device = ((ulong)unchecked((uint)Marshal.ReadInt32(buffer, 136)) << 32)
                | unchecked((uint)Marshal.ReadInt32(buffer, 140));
            return new(device, unchecked((ulong)Marshal.ReadInt64(buffer, 32)));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool IsVolumeTarget(string path)
    {
        var target = new DirectoryInfo(path).LinkTarget;
        return target?.Contains("Volume{", StringComparison.OrdinalIgnoreCase) is true;
    }

    private static uint GetUnixMode(string path)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            var result = OperatingSystem.IsLinux()
                ? StatX(
                    AtFileDescriptorCurrentWorkingDirectory,
                    path,
                    AtSymbolicLinkNoFollow,
                    StatXBasicStats,
                    buffer)
                : LStat(path, buffer);
            if (result != 0)
            {
                throw new IOException($"无法读取文件系统对象 metadata：{path}");
            }

            return OperatingSystem.IsMacOS()
                ? unchecked((ushort)Marshal.ReadInt16(buffer, 4))
                : unchecked((ushort)Marshal.ReadInt16(buffer, 28));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport(
        "libc",
        EntryPoint = "lstat",
        SetLastError = true,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern int LStat(string path, IntPtr buffer);

    [DllImport(
        "libc",
        EntryPoint = "statx",
        SetLastError = true,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern int StatX(int directoryFileDescriptor, string path, int flags, uint mask, IntPtr buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindFirstFileW", SetLastError = true)]
    private static extern IntPtr FindFirstFile(string fileName, out Win32FindData findFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateFileName;
    }
}
