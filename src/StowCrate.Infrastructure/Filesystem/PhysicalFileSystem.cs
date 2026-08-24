using System.Text;
using System.Security.Cryptography;
using StowCrate.Core.Filesystem;

namespace StowCrate.Infrastructure.Filesystem;

public sealed record PhysicalFileSystemEntry(
    string FullPath,
    FileSystemEntryKind Kind,
    long Length,
    DateTimeOffset? LastWriteTimeUtc,
    LinkKind? LinkKind,
    string? LinkTarget,
    bool LinkTargetIsDirectory,
    SourceMetadata MetadataFlags,
    string FileSystemId);

public interface IPhysicalFileSystem
{
    PhysicalFileSystemEntry Inspect(string path);

    IEnumerable<string> EnumerateChildren(string directoryPath);

    string ReadAllText(string path);
    byte[] ReadAllBytes(string path);
    string ComputeSha256(string path, CancellationToken cancellationToken);
}

public sealed class SystemPhysicalFileSystem : IPhysicalFileSystem
{
    private readonly string[] _volumeRoots = DriveInfo.GetDrives()
        .Where(drive => drive.IsReady)
        .Select(drive => NormalizeRoot(drive.RootDirectory.FullName))
        .OrderByDescending(path => path.Length)
        .ToArray();

    public PhysicalFileSystemEntry Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var attributes = File.GetAttributes(fullPath);
        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
        var isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);
        var fileSystemInfo = isDirectory
            ? (FileSystemInfo)new DirectoryInfo(fullPath)
            : new FileInfo(fullPath);
        fileSystemInfo.Refresh();

        var metadataFlags = GetMetadataFlags(attributes, fullPath);
        if (isReparsePoint)
        {
            string? target;
            try
            {
                target = fileSystemInfo.LinkTarget;
            }
            catch (IOException)
            {
                return CreateSpecial(fileSystemInfo, metadataFlags);
            }

            if (target is null)
            {
                return CreateSpecial(fileSystemInfo, metadataFlags);
            }

            var linkKind = NativeFileType.GetLinkKind(fullPath);
            if (linkKind is LinkKind.Other)
            {
                return CreateSpecial(fileSystemInfo, metadataFlags);
            }

            return new PhysicalFileSystemEntry(
                fullPath,
                FileSystemEntryKind.Link,
                0,
                SafeLastWriteTime(fileSystemInfo),
                linkKind,
                target,
                isDirectory,
                metadataFlags | (isDirectory ? SourceMetadata.DirectoryTarget : SourceMetadata.None),
                GetFileSystemId(fullPath));
        }

        var nativeKind = NativeFileType.GetEntryKind(fullPath, isDirectory);
        if (nativeKind is FileSystemEntryKind.Special || attributes.HasFlag(FileAttributes.Device))
        {
            return CreateSpecial(fileSystemInfo, metadataFlags);
        }

        return new PhysicalFileSystemEntry(
            fullPath,
            isDirectory ? FileSystemEntryKind.Directory : FileSystemEntryKind.File,
            isDirectory ? 0 : ((FileInfo)fileSystemInfo).Length,
            SafeLastWriteTime(fileSystemInfo),
            null,
            null,
            false,
            metadataFlags,
            GetFileSystemId(fullPath));
    }

    public IEnumerable<string> EnumerateChildren(string directoryPath)
    {
        return Directory.EnumerateFileSystemEntries(
            directoryPath,
            "*",
            new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
            });
    }

    public string ReadAllText(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public string ComputeSha256(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private PhysicalFileSystemEntry CreateSpecial(FileSystemInfo fileSystemInfo, SourceMetadata flags)
    {
        return new PhysicalFileSystemEntry(
            fileSystemInfo.FullName,
            FileSystemEntryKind.Special,
            0,
            SafeLastWriteTime(fileSystemInfo),
            null,
            null,
            false,
            flags,
            GetFileSystemId(fileSystemInfo.FullName));
    }

    private string GetFileSystemId(string path)
    {
        var normalized = NormalizeRoot(Path.GetFullPath(path));
        return _volumeRoots.FirstOrDefault(root => IsSameOrDescendant(normalized, root))
            ?? NormalizeRoot(Path.GetPathRoot(normalized) ?? normalized);
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        if (path.Equals(root, PathComparison))
        {
            return true;
        }

        return path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, PathComparison);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static string NormalizeRoot(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static DateTimeOffset? SafeLastWriteTime(FileSystemInfo entry)
    {
        try
        {
            return entry.LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static SourceMetadata GetMetadataFlags(FileAttributes attributes, string path)
    {
        var flags = SourceMetadata.None;
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            flags |= SourceMetadata.ReadOnly;
        }

        if (attributes.HasFlag(FileAttributes.Hidden))
        {
            flags |= SourceMetadata.Hidden;
        }

        if (!OperatingSystem.IsWindows() && NativeFileType.IsExecutable(path))
        {
            flags |= SourceMetadata.Executable;
        }

        return flags;
    }
}
