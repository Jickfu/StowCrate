using System.Globalization;
using System.Runtime.InteropServices;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Application.StorageMaintenance;

namespace StowCrate.Infrastructure.Filesystem;

public sealed class StorageRelocationCapacityProbe : IStorageRelocationCapacityProbe
{
    public Task<StorageCapacityObservation> ObserveAsync(ResolvedPhysicalPath root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = StorageRelocationPhysicalStore.InspectIdentity(root.CanonicalPath, true);
        var volume = NativeFileType.GetIdentityNoFollow(root.CanonicalPath).DeviceOrVolume;
        long available;
        if (OperatingSystem.IsWindows())
        {
            // 必须查询实际目录所在卷，不能用盘符根替代挂载目录；UNC 目录需要末尾分隔符。
            var path = Path.EndsInDirectorySeparator(root.CanonicalPath) ? root.CanonicalPath : root.CanonicalPath + Path.DirectorySeparatorChar;
            if (!GetDiskFreeSpaceEx(path, out var userAvailable, out _, out _) || userAvailable > long.MaxValue)
                throw new IOException("Relocation capacity query failed.");
            available = (long)userAvailable;
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // Unix DriveInfo 保留传入路径，按该目录所在 filesystem 查询当前用户可用空间。
            available = new DriveInfo(root.CanonicalPath).AvailableFreeSpace;
        }
        else throw new PlatformNotSupportedException();
        var after = StorageRelocationPhysicalStore.InspectIdentity(root.CanonicalPath, true);
        if (before != after || volume != NativeFileType.GetIdentityNoFollow(root.CanonicalPath).DeviceOrVolume || available < 0)
            throw new IOException("Relocation capacity root changed.");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new StorageCapacityObservation(after,
            new(before.Provider, 1, volume.ToString("x16", CultureInfo.InvariantCulture)), available));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong availableToCaller, out ulong totalBytes, out ulong totalFreeBytes);
}
