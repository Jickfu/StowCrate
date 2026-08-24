using System.Text;
using System.Security.Cryptography;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;
using StowCrate.Core.Planning;
using StowCrate.Core.Rules;

namespace StowCrate.Infrastructure.Filesystem;

public sealed record SourceScanOptions(
    CaseSensitivity CaseSensitivity = CaseSensitivity.Auto,
    FileSystemBoundaryPolicy BoundaryPolicy = FileSystemBoundaryPolicy.StayOnSourceFileSystem,
    bool ObserveBackupIgnoreRuleSource = true,
    bool ComputeFullContentHashes = false);

public sealed class SourceScanner
{
    private const string BackupIgnoreFileName = ".backupignore";
    private readonly IPhysicalFileSystem _fileSystem;

    public SourceScanner()
        : this(new SystemPhysicalFileSystem())
    {
    }

    public SourceScanner(IPhysicalFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
    }

    public SourceScanResult Scan(
        BackupSource source,
        string sourceRoot,
        SourceScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        options ??= new SourceScanOptions();

        var issues = new List<ScanIssue>();
        PhysicalFileSystemEntry root;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            root = _fileSystem.Inspect(Path.GetFullPath(sourceRoot));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return Fatal("SCFS0001", null, $"SourceRoot 不存在或无法访问：{exception.Message}");
        }

        if (root.Kind is not FileSystemEntryKind.Directory)
        {
            var code = root.Kind is FileSystemEntryKind.Link ? "SCFS0002" : "SCFS0003";
            return Fatal(code, null, "SourceRoot 必须是可访问的真实目录，不能是链接或特殊对象。");
        }

        var entries = new List<SourceEntry>();
        ScanDirectory(root.FullPath, root.FileSystemId, root.FullPath, options, entries, issues, cancellationToken);
        if (issues.Any(issue => issue.Severity is ScanIssueSeverity.Fatal))
        {
            return new SourceScanResult(null, issues);
        }

        var caseSensitivity = options.CaseSensitivity is CaseSensitivity.Auto
            ? ResolveDefaultCaseSensitivity()
            : options.CaseSensitivity;
        return new SourceScanResult(new SourceSnapshot(source, caseSensitivity, entries), issues);
    }

    private void ScanDirectory(
        string directoryPath,
        string sourceFileSystemId,
        string sourceRoot,
        SourceScanOptions options,
        List<SourceEntry> entries,
        List<ScanIssue> issues,
        CancellationToken cancellationToken)
    {
        string[] childPaths;
        try
        {
            childPaths = _fileSystem.EnumerateChildren(directoryPath).ToArray();
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            var path = ToLogicalPath(sourceRoot, directoryPath);
            var severity = path.IsRoot ? ScanIssueSeverity.Fatal : ScanIssueSeverity.Warning;
            issues.Add(new ScanIssue(severity, "SCFS1001", path.IsRoot ? null : path, $"目录无法枚举，已跳过：{exception.Message}"));
            return;
        }

        foreach (var childPath in childPaths.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logicalPath = TryGetLogicalPath(sourceRoot, childPath, issues);
            if (logicalPath is null)
            {
                continue;
            }

            PhysicalFileSystemEntry physicalEntry;
            try
            {
                physicalEntry = _fileSystem.Inspect(childPath);
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                issues.Add(new ScanIssue(ScanIssueSeverity.Warning, "SCFS1002", logicalPath, $"对象在扫描期间消失或 metadata 无法读取，已跳过：{exception.Message}"));
                continue;
            }

            if (options.ObserveBackupIgnoreRuleSource
                && logicalPath.Value.Name.Equals(BackupIgnoreFileName, StringComparison.Ordinal)
                && physicalEntry.Kind is not FileSystemEntryKind.File)
            {
                issues.Add(new ScanIssue(ScanIssueSeverity.Fatal, "SCFS0004", logicalPath, ".backupignore 必须是真实 regular file，不能是 Link、Directory 或 Special。"));
                continue;
            }

            string? textContent = null;
            string? rawFileSha256 = null;
            string? fullContentSha256 = null;
            if (options.ObserveBackupIgnoreRuleSource
                && physicalEntry.Kind is FileSystemEntryKind.File
                && logicalPath.Value.Name.Equals(BackupIgnoreFileName, StringComparison.Ordinal))
            {
                try
                {
                    var rawBytes = _fileSystem.ReadAllBytes(childPath);
                    rawFileSha256 = Convert.ToHexStringLower(SHA256.HashData(rawBytes));
                    textContent = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(rawBytes);
                    if (options.ComputeFullContentHashes) fullContentSha256 = rawFileSha256;
                }
                catch (Exception exception) when (IsFileSystemException(exception) || exception is DecoderFallbackException)
                {
                    issues.Add(new ScanIssue(ScanIssueSeverity.Fatal, "SCFS0005", logicalPath, $".backupignore 无法读取：{exception.Message}"));
                    continue;
                }
            }
            else if (options.ComputeFullContentHashes && physicalEntry.Kind is FileSystemEntryKind.File)
            {
                try
                {
                    fullContentSha256 = _fileSystem.ComputeSha256(childPath, cancellationToken);
                }
                catch (Exception exception) when (IsFileSystemException(exception))
                {
                    issues.Add(new ScanIssue(ScanIssueSeverity.Warning, "SCFS1006", logicalPath, $"文件完整内容 hash 无法读取，已跳过：{exception.Message}"));
                    continue;
                }
            }

            var link = physicalEntry.Kind is FileSystemEntryKind.Link
                ? CreateLinkInfo(physicalEntry, sourceRoot)
                : null;
            entries.Add(new SourceEntry(
                logicalPath.Value,
                physicalEntry.Kind,
                physicalEntry.Length,
                textContent,
                lastWriteTimeUtc: physicalEntry.LastWriteTimeUtc,
                link: link,
                metadataFlags: physicalEntry.MetadataFlags,
                fullContentSha256: fullContentSha256,
                rawFileSha256: rawFileSha256));

            if (physicalEntry.Kind is FileSystemEntryKind.Link && link!.IsDangling)
            {
                issues.Add(new ScanIssue(ScanIssueSeverity.Info, "SCFS2001", logicalPath, "Broken link 已作为链接对象保留，不会跟随 target。"));
            }

            if (physicalEntry.Kind is FileSystemEntryKind.Special)
            {
                issues.Add(new ScanIssue(ScanIssueSeverity.Warning, "SCFS1004", logicalPath, "不支持的特殊文件或 Reparse Point 已记录但不会进入归档。"));
                continue;
            }

            if (physicalEntry.Kind is not FileSystemEntryKind.Directory)
            {
                continue;
            }

            if (options.BoundaryPolicy is FileSystemBoundaryPolicy.StayOnSourceFileSystem
                && !physicalEntry.FileSystemId.Equals(sourceFileSystemId, StringComparison.Ordinal))
            {
                issues.Add(new ScanIssue(ScanIssueSeverity.Warning, "SCFS1005", logicalPath, "检测到文件系统边界；目录已保留但未继续枚举。"));
                continue;
            }

            ScanDirectory(childPath, sourceFileSystemId, sourceRoot, options, entries, issues, cancellationToken);
        }
    }

    private static LinkInfo CreateLinkInfo(PhysicalFileSystemEntry entry, string sourceRoot)
    {
        var target = entry.LinkTarget ?? string.Empty;
        string resolvedTarget;
        try
        {
            resolvedTarget = Path.GetFullPath(Path.IsPathFullyQualified(target)
                ? target
                : Path.Combine(Path.GetDirectoryName(entry.FullPath)!, target));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new LinkInfo(entry.LinkKind ?? LinkKind.Other, target, LinkTargetScope.Unresolved, isDangling: true);
        }

        var exists = File.Exists(resolvedTarget) || Directory.Exists(resolvedTarget);
        var scope = !exists
            ? LinkTargetScope.Unresolved
            : IsSameOrDescendant(resolvedTarget, sourceRoot)
                ? LinkTargetScope.WithinSource
                : LinkTargetScope.OutsideSource;
        return new LinkInfo(entry.LinkKind ?? LinkKind.Other, target, scope, !exists);
    }

    private static LogicalPath? TryGetLogicalPath(string sourceRoot, string path, List<ScanIssue> issues)
    {
        try
        {
            var relativePath = Path.GetRelativePath(sourceRoot, path);
            return new LogicalPath(relativePath);
        }
        catch (ArgumentException exception)
        {
            issues.Add(new ScanIssue(ScanIssueSeverity.Fatal, "SCFS0006", null, $"检测到无法映射到 SourceRoot 的路径：{exception.Message}"));
            return null;
        }
    }

    private static LogicalPath ToLogicalPath(string sourceRoot, string path)
    {
        var relativePath = Path.GetRelativePath(sourceRoot, path);
        return relativePath == "." ? LogicalPath.Root : new LogicalPath(relativePath);
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return normalizedPath.Equals(normalizedRoot, comparison)
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static CaseSensitivity ResolveDefaultCaseSensitivity()
    {
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? CaseSensitivity.Insensitive
            : CaseSensitivity.Sensitive;
    }

    private static bool IsFileSystemException(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    }

    private static SourceScanResult Fatal(string code, LogicalPath? path, string message)
    {
        return new SourceScanResult(null, [new ScanIssue(ScanIssueSeverity.Fatal, code, path, message)]);
    }
}
