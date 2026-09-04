using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StowCrate.Application.StorageMaintenance;

namespace StowCrate.Infrastructure.Filesystem;

internal static class LinuxOrdinalDirectoryProbe
{
    internal static bool Supports(long filesystemType, uint flags)
        // ext2/3/4 共享 magic；casefold 或 fscrypt 目录不在本适配器证明范围内。
        => filesystemType == 0xef53 && (flags & (0x40000000U | 0x00000800U)) == 0;

    internal static StorageObjectIdentity Observe(string path)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new StorageRelocationComparisonUnavailableException();
        // 只打开现存普通目录：O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC。
        var descriptor = Open(path, 0x10000 | 0x20000 | 0x80000);
        if (descriptor < 0) throw new StorageRelocationComparisonUnavailableException();
        using var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            // 此布局仅用于 Linux 64-bit ABI；f_type 是首个 native long。
            if (FStatFs(handle, buffer) != 0) throw new StorageRelocationComparisonUnavailableException();
            var filesystemType = Marshal.ReadInt64(buffer);
            // FS_IOC_GETFLAGS = _IOR('f', 1, long)，请求大小为 8，内核实际写入 flags int。
            if (Ioctl(handle, 0x80086601, out var flags) != 0 || !Supports(filesystemType, flags))
                throw new StorageRelocationComparisonUnavailableException();
            // AT_EMPTY_PATH 对同一打开的目录取得 identity，避免将另一对象的查询结果授予当前路径。
            if (StatX(handle, "", 0x1000, 0x7ff, buffer) != 0
                || (Marshal.ReadInt32(buffer) & 0x103) != 0x103
                || (unchecked((ushort)Marshal.ReadInt16(buffer, 28)) & 0xf000) != 0x4000)
                throw new StorageRelocationComparisonUnavailableException();
            var device = ((ulong)unchecked((uint)Marshal.ReadInt32(buffer, 136)) << 32)
                | unchecked((uint)Marshal.ReadInt32(buffer, 140));
            var inode = unchecked((ulong)Marshal.ReadInt64(buffer, 32));
            return new("linux-device-inode", 1, string.Create(CultureInfo.InvariantCulture, $"{device:x16}:{inode:x16}"));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static extern int FStatFs(SafeFileHandle handle, IntPtr buffer);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(SafeFileHandle handle, nuint request, out uint flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int StatX(SafeFileHandle handle, string path, int flags, uint mask, IntPtr buffer);
}
