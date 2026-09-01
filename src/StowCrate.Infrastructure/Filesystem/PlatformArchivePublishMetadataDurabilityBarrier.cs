using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StowCrate.Application.Publishing;

namespace StowCrate.Infrastructure.Filesystem;

public sealed class PlatformArchivePublishMetadataDurabilityBarrier : IArchivePublishMetadataDurabilityBarrier
{
    public Task<PublishMetadataDurabilityProof> FlushDirectoryMetadataAsync(string destinationDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        destinationDirectory = Path.GetFullPath(destinationDirectory);
        if (OperatingSystem.IsWindows())
        {
            using var handle = CreateFileW(destinationDirectory, 0x80000000, 0x00000007, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            var completed = !handle.IsInvalid && FlushFileBuffers(handle);
            return Task.FromResult(new PublishMetadataDurabilityProof(completed,
                completed ? "windows-directory-handle-flush-v1" : "windows-atomic-namespace-only-directory-flush-unavailable"));
        }

        var directoryFlag = OperatingSystem.IsMacOS() ? 0x00100000 : 0x00010000;
        var descriptor = open(destinationDirectory, directoryFlag, 0);
        var flushed = descriptor >= 0 && fsync(descriptor) == 0;
        if (descriptor >= 0) _ = close(descriptor);
        return Task.FromResult(new PublishMetadataDurabilityProof(flushed,
            flushed ? "posix-directory-fsync-v1" : "posix-atomic-rename-only-directory-fsync-unavailable"));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);

    [DllImport("libc", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int open(string path, int flags, int mode);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int descriptor);
    [DllImport("libc", SetLastError = true)] private static extern int close(int descriptor);
}
